using UnityEngine;

/// <summary>
/// A single terrain chunk. One GameObject, one mesh from TerrainMesher,
/// one material instance with its own splatmap texture.
///
/// MeshCollider is only active within ColliderRadius chunks of the player.
/// Chunks further away have their collider disabled — reduces PhysX cost
/// from 112ms (49 active colliders) to near zero (only ~9 active at a time).
/// </summary>
public class LandChunk
{
    public ChunkBounds Bounds     { get; private set; }
    public GameObject  Root       { get; private set; }
    public ChunkState  State      { get; private set; }
    public int         CurrentLOD { get; private set; }

    public static float[] LODDistances    = { 800f, 2000f, 4000f };
    public static float   ColliderRadius  = 1200f; // only chunks within this distance get a collider

    private MeshFilter    _meshFilter;
    private MeshRenderer  _meshRenderer;
    private MeshCollider  _meshCollider;
    private Material      _matInstance;
    private Mesh          _mesh;
    private bool          _colliderActive;

    public LandChunk(ChunkBounds bounds, Transform parent, Material sharedMat)
    {
        Bounds     = bounds;
        State      = ChunkState.Unloaded;
        CurrentLOD = -1;

        Root = new GameObject($"Land_{bounds.ChunkX}_{bounds.ChunkY}");
        Root.transform.SetParent(parent, false);
        Root.transform.position = Vector3.zero;

        _meshFilter   = Root.AddComponent<MeshFilter>();
        _meshRenderer = Root.AddComponent<MeshRenderer>();

        // MeshCollider added once but enabled/disabled by distance
        _meshCollider         = Root.AddComponent<MeshCollider>();
        _meshCollider.enabled = false;
        _colliderActive       = false;

        _matInstance                    = new Material(sharedMat);
        _meshRenderer.sharedMaterial    = _matInstance;
        _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _meshRenderer.receiveShadows    = true;

        Root.SetActive(false);
    }

    public void ApplyMesh(Mesh mesh, int lodLevel)
    {
        if (_mesh != null) Object.Destroy(_mesh);
        _mesh            = mesh;
        _mesh.name       = $"Land_{Bounds.ChunkX}_{Bounds.ChunkY}_LOD{lodLevel}";
        _meshFilter.mesh = _mesh;
        CurrentLOD       = lodLevel;
        State            = ChunkState.Loaded;

        // Only assign collider mesh when it is actually active
        // Assigning it when disabled avoids PhysX cooking the mesh immediately
        if (_colliderActive)
            _meshCollider.sharedMesh = _mesh;

        // Assign splatmap
        if (SplatmapLoader.Instance != null)
        {
            var coord = new Vector2Int(Bounds.ChunkX, Bounds.ChunkY);
            var splat = SplatmapLoader.Instance.GetSplatmap(coord);
            if (splat != null)
                _matInstance.SetTexture("_Splatmap", splat);
        }

        Root.SetActive(true);
    }

    /// <summary>
    /// Called every frame by LandLoader to enable or disable the MeshCollider
    /// based on distance. Only the closest chunks have active physics.
    /// </summary>
    public void UpdateCollider(float dist)
    {
        bool shouldBeActive = dist <= ColliderRadius && State == ChunkState.Loaded && _mesh != null;

        if (shouldBeActive == _colliderActive) return; // no change

        _colliderActive = shouldBeActive;

        if (shouldBeActive)
        {
            // Cook the mesh into PhysX now that the player is close enough
            _meshCollider.sharedMesh = _mesh;
            _meshCollider.enabled    = true;
        }
        else
        {
            _meshCollider.enabled    = false;
            _meshCollider.sharedMesh = null; // release PhysX memory for distant chunks
        }
    }

    public void SetPending() => State = ChunkState.Pending;
    public void SetEmpty()   { Root.SetActive(false); State = ChunkState.Loaded; }

    public bool EvaluateLOD(float dist)
    {
        int target = GetLODForDistance(dist);
        if (target == CurrentLOD) return false;
        CurrentLOD = target;
        return true;
    }

    public float DistanceTo(Vector3 pos)
        => Vector3.Distance(new Vector3(Bounds.WorldCenter.x, 0f, Bounds.WorldCenter.y), pos);

    public void Destroy()
    {
        if (SplatmapLoader.Instance != null)
            SplatmapLoader.Instance.Release(new Vector2Int(Bounds.ChunkX, Bounds.ChunkY));
        if (_matInstance != null) Object.Destroy(_matInstance);
        if (_mesh        != null) Object.Destroy(_mesh);
        if (Root         != null) Object.Destroy(Root);
        State = ChunkState.Unloaded;
    }

    public static int GetLODForDistance(float dist)
    {
        for (int i = 0; i < LODDistances.Length; i++)
            if (dist <= LODDistances[i]) return i;
        return LODDistances.Length - 1;
    }
}
