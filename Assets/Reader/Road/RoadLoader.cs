using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Streams road chunks around the player using OSM tile PBF data.
///
/// Optimisations vs original:
///   - TileCache.Instance shared with LandLoader — each PBF file read once
///   - Spawn rate limiter — staggers background thread starts
///   - _keysBuffer reused every frame — zero GC alloc (was allocating per frame)
///   - Frame time budget for uploads
/// </summary>
public class RoadLoader
{
    public int   LoadRadius        { get; set; } = 2;
    public int   UnloadRadius      { get; set; } = 4;
    public float UploadBudgetMs    { get; set; } = 1.0f;
    public int   MaxSpawnsPerFrame { get; set; } = 2; // roads are lighter than terrain

    private readonly float     _chunkSize;
    private readonly Transform _parent;
    private readonly Material  _material;
    private readonly OsmLoader _osmLoader;

    private readonly Dictionary<Vector2Int, RoadChunk> _chunks      = new();
    private readonly HashSet<Vector2Int>               _pending     = new();
    private readonly Queue<PendingUpload>              _uploadQueue = new();
    private readonly object                            _lock        = new();
    private          CancellationTokenSource           _cts         = new();
    private readonly List<Vector2Int>                  _keysBuffer  = new(64);
    private readonly Stopwatch                         _uploadTimer = new();

    public int ChunkCount   => _chunks.Count;
    public int PendingCount => _pending.Count;

    public RoadLoader(float chunkSize, Transform parent, Material material, OsmLoader osmLoader)
    {
        _chunkSize = chunkSize;
        _parent    = parent;
        _material  = material;
        _osmLoader = osmLoader;
    }

    public void Update(Vector3 playerPos)
    {
        DrainUploads();

        Vector2Int center = ChunkBounds.WorldToGrid(
            new Vector2(playerPos.x, playerPos.z), _chunkSize);

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

        // Reuse buffer — fixes the new List allocation that was here before
        _keysBuffer.Clear();
        foreach (var key in _chunks.Keys)
            _keysBuffer.Add(key);

        foreach (var key in _keysBuffer)
        {
            if (!_chunks.TryGetValue(key, out RoadChunk chunk)) continue;

            float dist = chunk.DistanceTo(playerPos);

            if (dist > _chunkSize * UnloadRadius || dist > RoadChunk.CullDistance)
            {
                chunk.Destroy();
                _chunks.Remove(key);
                continue;
            }

            if (chunk.State == ChunkState.Loaded && chunk.EvaluateLOD(dist))
                RebuildAsync(key, chunk.Bounds, chunk.CurrentLOD);
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
        int         lod    = RoadChunk.GetLODForDistance(dist);

        var chunk = new RoadChunk(bounds, _parent, _material);
        _chunks[coord] = chunk;
        chunk.SetPending();

        var token = _cts.Token;
        Task.Run(async () =>
        {
            if (token.IsCancellationRequested) return;

            // Get from shared cache — free if LandLoader already loaded this tile
            TileData tile  = await TileCache.Instance.GetAsync(coord, _osmLoader);
            var      roads = tile?.Roads ?? new System.Collections.Generic.List<ParsedWay>();
            var      meshes = RoadMesher.Build(roads, bounds, lod);

            lock (_lock)
                _uploadQueue.Enqueue(new PendingUpload { Coord = coord, Roads = meshes, Lod = lod });
        }, token);
    }

    private void RebuildAsync(Vector2Int coord, ChunkBounds bounds, int lod)
    {
        if (!_chunks.TryGetValue(coord, out RoadChunk chunk)) return;
        chunk.SetPending();

        var token = _cts.Token;
        Task.Run(async () =>
        {
            if (token.IsCancellationRequested) return;

            TileData tile   = await TileCache.Instance.GetAsync(coord, _osmLoader);
            var      roads  = tile?.Roads ?? new System.Collections.Generic.List<ParsedWay>();
            var      meshes = RoadMesher.Build(roads, bounds, lod);

            lock (_lock)
                _uploadQueue.Enqueue(new PendingUpload { Coord = coord, Roads = meshes, Lod = lod });
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

            if (!_chunks.TryGetValue(u.Coord, out RoadChunk chunk)) continue;
            if (u.Roads == null || u.Roads.Count == 0) { chunk.SetEmpty(); continue; }

            chunk.ApplyRoads(u.Roads, u.Lod);
        }
    }

    private struct PendingUpload
    {
        public Vector2Int         Coord;
        public List<RoadMeshData> Roads;
        public int                Lod;
    }
}
