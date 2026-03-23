// VegetationLoader.cs
// Streaming vegetation system — mirrors the RoadLoader architecture.
//
// Attach to the same GameObject as WorldStreamer.
// WorldStreamer must call Initialize() in its Start() after its own setup.
//
// Flow per chunk:
//   1. Main thread: detect chunk needed → read splatmap PNG → enqueue Task
//   2. Background thread: VegetationMesher.Build → produce TreePlacement list
//   3. Main thread (next frame): VegetationChunk.Build → instantiate tree GOs
//
// Rate-limited to MaxSpawnsPerFrame to avoid frame spikes.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

public class VegetationLoader : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────

    [Header("Prefabs — slot 0=OilPalm  1=Hardwood  2=FarmlandCrop")]
    [Tooltip("Full-mesh prefab per species. Leave slot null to skip that species' mesh.")]
    public GameObject[] MeshPrefabs  = new GameObject[3];

    [Header("Billboards — must use TreeBillboard shader")]
    [Tooltip("Material per species (TreeBillboard shader). Textures are set on the material.")]
    public Material[]   BillMaterials = new Material[3];

    [Tooltip("World-space (width, height) of billboard quad per species.")]
    public Vector2[]    BillSizes    = { new(3f, 7f), new(4f, 9f), new(2f, 3f) };

    [Header("LOD Distances (metres)")]
    public float MeshDistance = 200f;
    public float BillDistance = 800f;

    [Header("Streaming")]
    public int LoadRadius   = 2;
    public int UnloadRadius = 4;

    [Header("Density")]
    [Range(0f, 1f)]
    public float DensityScale = 1f;

    // ── Runtime wiring (set by WorldStreamer.Initialize) ──────────────────

    [HideInInspector] public TileCache     TileCache;
    [HideInInspector] public OsmLoader     OsmLoader;
    [HideInInspector] public SRTMHeightmap Heightmap;
    [HideInInspector] public Transform     Player;
    [HideInInspector] public float         ChunkSize     = 1000f;
    [HideInInspector] public string        SplatmapFolder;   // StreamingAssets/Splatmaps

    // ── Internal state ────────────────────────────────────────────────────

    readonly Dictionary<Vector2Int, VegetationChunk> _chunks  = new();
    readonly HashSet<Vector2Int>                      _loading = new();

    // Background threads enqueue results here; main thread dequeues in Update
    readonly ConcurrentQueue<PendingChunk> _pending = new();

    readonly List<Vector2Int> _keysBuffer = new(); // pre-allocated, avoids GC

    struct PendingChunk
    {
        public Vector2Int                              Coord;
        public List<VegetationMesher.TreePlacement>   Placements; // null = tile missing
    }

    const int MaxSpawnsPerFrame = 1;

    bool _initialised;
    Transform _vegRoot;

    // ── Initialisation ────────────────────────────────────────────────────

    /// Called by WorldStreamer.Start() after its own setup.
    public void Initialize(
        TileCache     tileCache,
        OsmLoader     osmLoader,
        SRTMHeightmap heightmap,
        Transform     player,
        float         chunkSize,
        string        splatmapFolder)
    {
        TileCache     = tileCache;
        OsmLoader     = osmLoader;
        Heightmap     = heightmap;
        Player        = player;
        ChunkSize     = chunkSize;
        SplatmapFolder = splatmapFolder;
        _vegRoot = new GameObject("VegetationChunks").transform;
        _vegRoot.SetParent(transform);
        _initialised  = true;

        // Push LOD thresholds to VegetationChunk
        VegetationChunk.MeshDistance = MeshDistance;
        VegetationChunk.BillDistance = BillDistance;
    }

    // ── Update loop ───────────────────────────────────────────────────────

    void Update()
    {
        if (!_initialised || Player == null) return;

        int spawnsThisFrame = 0;

        // ── 1. Upload pending results from background threads ──
        while (_pending.TryDequeue(out var item))
        {
            _loading.Remove(item.Coord);

            if (item.Placements != null && item.Placements.Count > 0)
                SpawnChunk(item.Coord, item.Placements);

            if (++spawnsThisFrame >= MaxSpawnsPerFrame) break;
        }

        // ── 2. Schedule loads for nearby chunks ──
        var playerChunk = WorldToChunk(Player.position);

        for (int dx = -LoadRadius; dx <= LoadRadius; dx++)
        for (int dz = -LoadRadius; dz <= LoadRadius; dz++)
        {
            var coord = new Vector2Int(playerChunk.x + dx, playerChunk.y + dz);
            if (!_chunks.ContainsKey(coord) && !_loading.Contains(coord))
                ScheduleLoad(coord);
        }

        // ── 3. LOD update + unload distant chunks ──
        _keysBuffer.Clear();
        _keysBuffer.AddRange(_chunks.Keys);

        foreach (var coord in _keysBuffer)
        {
            float dist = DistToChunkCentre(coord, Player.position);

            if (dist > UnloadRadius * ChunkSize)
            {
                _chunks[coord].Remove();
                _chunks.Remove(coord);
            }
            else
            {
                _chunks[coord].UpdateLOD(Player.position);
            }
        }
    }

    // ── Load scheduling ───────────────────────────────────────────────────

    void ScheduleLoad(Vector2Int coord)
    {
        _loading.Add(coord);

        // Read splatmap bytes on main thread (can't use File.ReadAllBytes from
        // background thread when path uses Application.streamingAssetsPath on Android)
        Color32[] splatmap = LoadSplatmapPixels(coord);

        Rect  worldBounds  = ChunkWorldRect(coord);
        float densScale    = DensityScale;
        uint  seed         = (uint)(coord.x * 73856093 ^ coord.y * 19349663);

        // Capture references for closure — avoid capturing 'this'
        var osmLoader      = OsmLoader;
        var heightmap      = Heightmap;
        var pending        = _pending;

        Task.Run(async () =>
        {
            try
            {
                var tileData = await TileCache.Instance.GetAsync(coord, osmLoader);
                if (tileData == null)
                {
                    pending.Enqueue(new PendingChunk { Coord = coord, Placements = null });
                    return;
                }

                var placements = VegetationMesher.Build(
                    tileData,
                    splatmap,
                    worldBounds,
                    (x, z) => heightmap.GetElevation(x, z),
                    densScale,
                    seed);

                pending.Enqueue(new PendingChunk { Coord = coord, Placements = placements });
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[VegetationLoader] Chunk {coord}: {e.Message}");
                pending.Enqueue(new PendingChunk { Coord = coord, Placements = null });
            }
        });
    }

    // ── Chunk spawning ────────────────────────────────────────────────────

    void SpawnChunk(Vector2Int coord, List<VegetationMesher.TreePlacement> placements)
    {
        if (_chunks.ContainsKey(coord)) return;

        var go    = new GameObject($"VegChunk_{coord.x}_{coord.y}");
        var chunk = go.AddComponent<VegetationChunk>();
        go.transform.SetParent(_vegRoot);
        // StartCoroutine spreads instantiation across frames (TreesPerFrame per frame)
        // instead of spiking all 2000+ Instantiate calls in one frame.
        StartCoroutine(chunk.BuildCoroutine(placements, MeshPrefabs, BillMaterials, BillSizes));
        _chunks[coord] = chunk;
    }

    // ── Splatmap reading (main thread only) ───────────────────────────────

    Color32[] LoadSplatmapPixels(Vector2Int coord)
    {
        if (string.IsNullOrEmpty(SplatmapFolder)) return null;

        string path = Path.Combine(SplatmapFolder, $"splatmap_{coord.x}_{coord.y}.png");
        if (!File.Exists(path)) return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear: true);
            if (!tex.LoadImage(bytes)) { Destroy(tex); return null; }
            var pixels = tex.GetPixels32();
            Destroy(tex);    // free immediately — we only need the raw Color32 array
            return pixels;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[VegetationLoader] Splatmap read failed {coord}: {e.Message}");
            return null;
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    void OnDestroy()
    {
        // Return all active chunks cleanly, then clear the pool.
        // Prevents pooled GOs leaking between Play sessions in the Editor.
        foreach (var chunk in _chunks.Values)
            chunk.Remove();
        _chunks.Clear();
        VegetationPool.Clear();
    }

    // ── Coordinate helpers ────────────────────────────────────────────────

    Rect ChunkWorldRect(Vector2Int coord) =>
        new(coord.x * ChunkSize, coord.y * ChunkSize, ChunkSize, ChunkSize);

    Vector2Int WorldToChunk(Vector3 worldPos) =>
        new(Mathf.FloorToInt(worldPos.x / ChunkSize),
            Mathf.FloorToInt(worldPos.z / ChunkSize));

    float DistToChunkCentre(Vector2Int coord, Vector3 playerPos)
    {
        float cx = (coord.x + 0.5f) * ChunkSize;
        float cz = (coord.y + 0.5f) * ChunkSize;
        float dx = cx - playerPos.x;
        float dz = cz - playerPos.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
