using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Streams terrain chunks around the player using SRTM heightmap data.
///
/// Optimisations:
///   - TileCache.Instance shared with RoadLoader — each PBF file read once
///   - Spawn rate limiter — max MaxSpawnsPerFrame new chunks per frame,
///     staggers background thread starts to prevent burst completion spikes
///   - _keysBuffer reused every frame — zero GC alloc
///   - Frame time budget for uploads — spreads GPU upload cost
///   - Normals pre-computed on background thread in TerrainMesher
///   - MeshCollider only active within ColliderRadius via LandChunk.UpdateCollider
/// </summary>
public class LandLoader
{
    public int   LoadRadius        { get; set; } = 2;
    public int   UnloadRadius      { get; set; } = 4;
    public float UploadBudgetMs    { get; set; } = 1.5f;
    public int   MaxSpawnsPerFrame { get; set; } = 1; // stagger chunk starts

    private readonly float     _chunkSize;
    private readonly Transform _parent;
    private readonly Material  _material;
    private readonly OsmLoader _osmLoader;

    private readonly Dictionary<Vector2Int, LandChunk> _chunks      = new();
    private readonly HashSet<Vector2Int>                _pending     = new();
    private readonly Queue<PendingUpload>               _uploadQueue = new();
    private readonly object                             _lock        = new();
    private          CancellationTokenSource            _cts         = new();
    private readonly List<Vector2Int>                   _keysBuffer  = new(64);
    private readonly Stopwatch                          _uploadTimer = new();

    public int ChunkCount   => _chunks.Count;
    public int PendingCount => _pending.Count;

    public LandLoader(float chunkSize, Transform parent, Material material, OsmLoader osmLoader)
    {
        _chunkSize = chunkSize;
        _parent    = parent;
        _material  = material;
        _osmLoader = osmLoader;
    }

    public void Update(Vector3 playerPos)
    {
        if (SRTMHeightmap.Instance == null || !SRTMHeightmap.Instance.IsLoaded) return;

        DrainUploads();

        Vector2Int center = ChunkBounds.WorldToGrid(
            new Vector2(playerPos.x, playerPos.z), _chunkSize);

        // Spawn missing chunks — rate limited so background threads start
        // one per frame and their completions are naturally staggered
        int spawnsThisFrame = 0;
        for (int x = center.x - LoadRadius; x <= center.x + LoadRadius; x++)
        for (int y = center.y - LoadRadius; y <= center.y + LoadRadius; y++)
        {
            if (spawnsThisFrame >= MaxSpawnsPerFrame) break;

            var coord = new Vector2Int(x, y);
            if (_chunks.ContainsKey(coord) || _pending.Contains(coord)) continue;

            SpawnAsync(coord, playerPos);
            spawnsThisFrame++;
        }

        // Iterate loaded chunks without allocating
        _keysBuffer.Clear();
        foreach (var key in _chunks.Keys)
            _keysBuffer.Add(key);

        foreach (var key in _keysBuffer)
        {
            if (!_chunks.TryGetValue(key, out LandChunk chunk)) continue;

            float dist = chunk.DistanceTo(playerPos);

            if (dist > _chunkSize * UnloadRadius)
            {
                TileCache.Instance.Evict(key);
                chunk.Destroy();
                _chunks.Remove(key);
                continue;
            }

            chunk.UpdateCollider(dist);

            if (chunk.State == ChunkState.Loaded && chunk.EvaluateLOD(dist))
                RebuildAsync(key, chunk.Bounds, chunk.CurrentLOD, dist);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        foreach (var c in _chunks.Values) c.Destroy();
        _chunks.Clear();
        _pending.Clear();
        lock (_lock) _uploadQueue.Clear();
    }

    // -----------------------------------------------------------------------
    // Private
    // -----------------------------------------------------------------------

    private void SpawnAsync(Vector2Int coord, Vector3 playerPos)
    {
        _pending.Add(coord);

        ChunkBounds bounds = ChunkBounds.FromGrid(coord.x, coord.y, _chunkSize);
        float       dist   = Vector3.Distance(playerPos,
                                 new Vector3(bounds.WorldCenter.x, 0f, bounds.WorldCenter.y));
        int         lod    = LandChunk.GetLODForDistance(dist);

        var chunk = new LandChunk(bounds, _parent, _material);
        _chunks[coord] = chunk;
        chunk.SetPending();

        var token = _cts.Token;
        Task.Run(async () =>
        {
            if (token.IsCancellationRequested) return;

            // Get tile from shared cache — free if RoadLoader already loaded it
            TileData tile  = await TileCache.Instance.GetAsync(coord, _osmLoader);
            MeshData data  = TerrainMesher.Build(bounds, lod, dist, tile?.Roads);

            lock (_lock)
                _uploadQueue.Enqueue(new PendingUpload { Coord = coord, Data = data, Lod = lod });
        }, token);
    }

    private void RebuildAsync(Vector2Int coord, ChunkBounds bounds, int lod, float dist)
    {
        if (!_chunks.TryGetValue(coord, out LandChunk chunk)) return;
        chunk.SetPending();

        var token = _cts.Token;
        Task.Run(async () =>
        {
            if (token.IsCancellationRequested) return;

            TileData tile = await TileCache.Instance.GetAsync(coord, _osmLoader);
            MeshData data = TerrainMesher.Build(bounds, lod, dist, tile?.Roads);

            lock (_lock)
                _uploadQueue.Enqueue(new PendingUpload { Coord = coord, Data = data, Lod = lod });
        }, token);
    }

    private void DrainUploads()
    {
        _uploadTimer.Restart();

        while (_uploadTimer.Elapsed.TotalMilliseconds < UploadBudgetMs)
        {
            PendingUpload u;
            lock (_lock)
            {
                if (_uploadQueue.Count == 0) break;
                u = _uploadQueue.Dequeue();
            }

            _pending.Remove(u.Coord);

            if (!_chunks.TryGetValue(u.Coord, out LandChunk chunk)) continue;
            if (u.Data == null) { chunk.SetEmpty(); continue; }

            Mesh mesh = TerrainMesher.Upload(u.Data);
            chunk.ApplyMesh(mesh, u.Lod);
        }
    }

    private struct PendingUpload
    {
        public Vector2Int Coord;
        public MeshData   Data;
        public int        Lod;
    }
}
