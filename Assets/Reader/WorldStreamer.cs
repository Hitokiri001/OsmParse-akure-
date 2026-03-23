using UnityEngine;

/// <summary>
/// Wires LandLoader (SRTM terrain) and RoadLoader (OSM roads) together.
/// Terrain and roads are completely separate meshes.
/// Roads sit 0.15m above terrain — no z-fighting.
/// Chunk edges are gap-free via integer-snapped border vertices in TerrainMesher.
///
/// REQUIRED SCENE COMPONENTS (same GameObject):
///   SRTMHeightmap, SplatmapLoader, UnityMainThread, WorldStreamer
///
/// REQUIRED STREAMING ASSETS:
///   StreamingAssets/SRTM/       — akure_srtm_lod*.raw + meta JSON
///   StreamingAssets/OsmTiles/   — tile_X_Y.pbf files from splitter.py
///   StreamingAssets/Splatmaps/  — splatmap_X_Y.png from worldcover_splatmap.py
/// </summary>
public class WorldStreamer : MonoBehaviour
{
    [Header("OSM Tiles")]
    public string OsmTilesFolder = "Assets/StreamingAssets/OsmTiles";

    [Header("Origin (match splitter.py)")]
    public double OriginLatitude  = 7.275;
    public double OriginLongitude = 5.215;

    [Header("Chunk Settings")]
    public float ChunkSize = 1000f;

    [Header("Load Radii")]
    public int LandLoadRadius   = 3;
    public int LandUnloadRadius = 5;
    public int RoadLoadRadius   = 3;
    public int RoadUnloadRadius = 5;

    [Header("LOD — Terrain")]
    public float[] LandLODDistances = { 800f, 2000f, 4000f };

    [Header("LOD — Roads")]
    public float[] RoadLODDistances = { 600f, 1500f, 3000f };
    public float   RoadCullDistance = 4000f;

    [Header("Materials")]
    [Tooltip("Terrain — URP Shader Graph with _Splatmap property")]
    public Material LandMaterial;
    [Tooltip("Road — simple asphalt colour or texture")]
    public Material RoadMaterial;

    [Header("Player")]
    public Transform PlayerTransform;

    [Header("Debug")]
    public bool ShowDebugLogs = false;

    private LandLoader _land;
    private RoadLoader _road;
    private OsmLoader  _osm;
    private Transform  _landRoot;
    private Transform  _roadRoot;

    private void Start()
    {
        Mercator.SetOrigin(OriginLatitude, OriginLongitude);

        LandChunk.LODDistances = LandLODDistances;
        RoadChunk.LODDistances = RoadLODDistances;
        RoadChunk.CullDistance = RoadCullDistance;

        LandLOD.Configure(LandLODDistances);

        _landRoot = new GameObject("LandChunks").transform;
        _landRoot.SetParent(transform);

        _roadRoot = new GameObject("RoadChunks").transform;
        _roadRoot.SetParent(transform);

        string tilesPath = ResolveTilesPath();
        _osm = new OsmLoader(tilesPath);

        _land = new LandLoader(ChunkSize, _landRoot, LandMaterial, _osm)
        {
            LoadRadius   = LandLoadRadius,
            UnloadRadius = LandUnloadRadius
        };

        _road = new RoadLoader(ChunkSize, _roadRoot, RoadMaterial, _osm)
        {
            LoadRadius   = RoadLoadRadius,
            UnloadRadius = RoadUnloadRadius
        };

        // ── Vegetation ──────────────────────────────────────────────────────
        var vegLoader = GetComponent<VegetationLoader>();
        if (vegLoader != null)
        {
            vegLoader.Initialize(
                TileCache.Instance,
                _osm,
                GetComponent<SRTMHeightmap>(),
                PlayerTransform,
                ChunkSize,
                System.IO.Path.Combine(
                    Application.streamingAssetsPath, "Splatmaps"));
        }

        if (!(SRTMHeightmap.Instance?.IsLoaded ?? false))
            Debug.LogWarning("[WorldStreamer] SRTMHeightmap not loaded — " +
                             "terrain will not generate until SRTM files are in StreamingAssets/SRTM/");

        Debug.Log($"[WorldStreamer] Ready — tiles: {tilesPath}");
    }

    private void Update()
    {
        if (PlayerTransform == null) return;
        Vector3 pos = PlayerTransform.position;
        _land.Update(pos);
        _road.Update(pos);

        if (ShowDebugLogs && Time.frameCount % 300 == 0)
            Debug.Log($"[WorldStreamer] Land: {_land.ChunkCount} ({_land.PendingCount} pending) | " +
                      $"Road: {_road.ChunkCount} ({_road.PendingCount} pending)");
    }

    private void OnDestroy()
    {
        _land?.Dispose();
        _road?.Dispose();
    }

    public float GetElevationAt(float worldX, float worldZ)
        => SRTMHeightmap.Instance?.GetElevation(worldX, worldZ) ?? 0f;

    public int PendingChunks
        => (_land?.PendingCount ?? 0) + (_road?.PendingCount ?? 0);

    private string ResolveTilesPath()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return System.IO.Path.Combine(Application.streamingAssetsPath, "OsmTiles");
#else
        return OsmTilesFolder;
#endif
    }
}
