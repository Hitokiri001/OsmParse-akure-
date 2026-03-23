using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime terrain streamer — instantiates pre-baked chunk prefabs
/// instead of generating meshes at runtime.
/// Replaces LandLoader entirely when using pre-baked prefabs.
/// Roads are children of the terrain prefab — no separate RoadLoader needed.
/// LOD switching is handled by Unity's built-in LODGroup per prefab.
///
/// Prefabs are loaded from Resources/ChunkPrefabs/Chunk_X_Y
/// OR from AssetBundles if you bundle them for mobile.
/// </summary>
public class PrefabStreamer : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Path inside Resources/ folder where chunk prefabs are stored")]
    public string PrefabResourcePath = "ChunkPrefabs";

    public float ChunkSize    = 1000f;
    public int   LoadRadius   = 3;
    public int   UnloadRadius = 5;

    [Header("Player")]
    public Transform PlayerTransform;

    private readonly Dictionary<Vector2Int, GameObject> _active
        = new Dictionary<Vector2Int, GameObject>();

    private Transform _root;

    private void Start()
    {
        _root = new GameObject("TerrainChunks").transform;
        _root.SetParent(transform);
    }

    private void Update()
    {
        if (PlayerTransform == null) return;

        Vector3    pos    = PlayerTransform.position;
        Vector2Int center = ChunkBounds.WorldToGrid(
            new Vector2(pos.x, pos.z), ChunkSize);

        // Load chunks in radius
        for (int x = center.x - LoadRadius; x <= center.x + LoadRadius; x++)
        for (int y = center.y - LoadRadius; y <= center.y + LoadRadius; y++)
        {
            var coord = new Vector2Int(x, y);
            if (_active.ContainsKey(coord)) continue;
            LoadChunk(coord);
        }

        // Unload distant chunks
        var keys = new List<Vector2Int>(_active.Keys);
        foreach (var key in keys)
        {
            float dist = Vector2.Distance(
                new Vector2(key.x * ChunkSize + ChunkSize * 0.5f,
                            key.y * ChunkSize + ChunkSize * 0.5f),
                new Vector2(pos.x, pos.z));

            if (dist > ChunkSize * UnloadRadius)
                UnloadChunk(key);
        }
    }

    private void LoadChunk(Vector2Int coord)
    {
        string path   = $"{PrefabResourcePath}/Chunk_{coord.x}_{coord.y}";
        var    prefab = Resources.Load<GameObject>(path);

        if (prefab == null)
        {
            // Chunk not baked yet — silently skip
            // Mark as "checked" so we don't try again every frame
            _active[coord] = null;
            return;
        }

        var instance = Instantiate(prefab, _root);
        instance.name = $"Chunk_{coord.x}_{coord.y}";
        instance.transform.position = Vector3.zero; // vertices are in world space
        _active[coord] = instance;
    }

    private void UnloadChunk(Vector2Int coord)
    {
        if (_active.TryGetValue(coord, out GameObject go))
        {
            if (go != null) Destroy(go);
            _active.Remove(coord);
        }
    }

    /// <summary>
    /// Returns world Y elevation at XZ by raycasting against the terrain collider.
    /// Only works for chunks with a MeshCollider on LOD0.
    /// </summary>
    public float GetElevationAt(float worldX, float worldZ)
    {
        if (Physics.Raycast(
            new Vector3(worldX, 10000f, worldZ),
            Vector3.down,
            out RaycastHit hit,
            20000f,
            LayerMask.GetMask("Terrain")))
        {
            return hit.point.y;
        }

        // Fallback to SRTM if available
        return SRTMHeightmap.Instance?.GetElevation(worldX, worldZ) ?? 0f;
    }

    private void OnDestroy()
    {
        var keys = new List<Vector2Int>(_active.Keys);
        foreach (var key in keys)
            UnloadChunk(key);
    }
}
