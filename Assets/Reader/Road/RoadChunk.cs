using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A single road chunk. One parent GameObject with one child per road way.
/// Each child has a road ribbon mesh from RoadMesher.
/// Sits above terrain by RoadYOffset so no z-fighting.
/// </summary>
public class RoadChunk
{
    public ChunkBounds Bounds     { get; private set; }
    public GameObject  Root       { get; private set; }
    public ChunkState  State      { get; private set; }
    public int         CurrentLOD { get; private set; }

    public static float[] LODDistances = { 600f, 1500f, 3000f };
    public static float   CullDistance = 4000f;

    private Material        _material;
    private List<GameObject> _roadObjects = new List<GameObject>();
    private List<Mesh>       _meshes      = new List<Mesh>();

    public RoadChunk(ChunkBounds bounds, Transform parent, Material material)
    {
        Bounds    = bounds;
        State     = ChunkState.Unloaded;
        CurrentLOD = -1;
        _material = material;

        Root = new GameObject($"Roads_{bounds.ChunkX}_{bounds.ChunkY}");
        Root.transform.SetParent(parent, false);
        Root.transform.position = Vector3.zero;
        Root.SetActive(false);
    }

    public void ApplyRoads(List<RoadMeshData> roads, int lodLevel)
    {
        ClearRoads();

        foreach (var road in roads)
        {
            if (road.MeshData == null) continue;

            Mesh mesh = RoadMesher.Upload(road.MeshData);
            if (mesh == null) continue;

            var go = new GameObject($"Road_{road.OsmId}");
            go.transform.SetParent(Root.transform, false);

            var mf             = go.AddComponent<MeshFilter>();
            var mr             = go.AddComponent<MeshRenderer>();
            mf.sharedMesh      = mesh;
            mr.sharedMaterial  = _material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            _roadObjects.Add(go);
            _meshes.Add(mesh);
        }

        CurrentLOD = lodLevel;
        State      = ChunkState.Loaded;
        Root.SetActive(_roadObjects.Count > 0);
    }

    private void ClearRoads()
    {
        foreach (var go   in _roadObjects) if (go)   Object.Destroy(go);
        foreach (var mesh in _meshes)      if (mesh) Object.Destroy(mesh);
        _roadObjects.Clear();
        _meshes.Clear();
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
        ClearRoads();
        if (Root != null) Object.Destroy(Root);
        State = ChunkState.Unloaded;
    }

    public static int GetLODForDistance(float dist)
    {
        for (int i = 0; i < LODDistances.Length; i++)
            if (dist <= LODDistances[i]) return i;
        return LODDistances.Length - 1;
    }
}
