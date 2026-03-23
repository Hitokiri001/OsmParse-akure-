using System.Collections.Generic;
using UnityEngine;

// -----------------------------------------------------------------------
// Shared OSM data types — single source of truth for the whole project.
// -----------------------------------------------------------------------

public struct ParsedNode
{
    public long    Id;
    public double  Lat;
    public double  Lon;
    public Vector3 WorldPos; // X = east, Y = 0 (elevation applied by mesher), Z = north
}

public class ParsedWay
{
    public long                       Id;
    public List<Vector2>              Points;  // World XZ positions
    public List<Vector2d>             LatLons; // Geographic positions
    public Dictionary<string, string> Tags;
    public WayType                    WayType;
    public bool                       IsClosed;
    public double MinLat, MaxLat, MinLon, MaxLon;
}

public enum WayType
{
    Road,
    Landmass,
    Water,
    Building,
    Unknown
}

/// <summary>
/// Double-precision Vector2 for lat/lon storage.
/// </summary>
public struct Vector2d
{
    public double x; // lat
    public double y; // lon

    public Vector2d(double x, double y) { this.x = x; this.y = y; }
}

/// <summary>
/// All parsed OSM data for a single tile.
/// </summary>
public class TileData
{
    public List<ParsedWay>  Roads     = new List<ParsedWay>();
    public List<ParsedWay>  Landmass  = new List<ParsedWay>();
    public List<ParsedWay>  Water     = new List<ParsedWay>();
    public List<ParsedWay>  Buildings = new List<ParsedWay>();
    public List<ParsedWay>  Unknown   = new List<ParsedWay>();
    public List<ParsedNode> Trees     = new List<ParsedNode>(); // natural=tree nodes

    public bool IsEmpty =>
        Roads.Count == 0 && Landmass.Count == 0 &&
        Water.Count == 0 && Buildings.Count == 0;
}

/// <summary>
/// Raw mesh data built on a background thread and uploaded to a Unity
/// Mesh on the main thread. Shared by TerrainMesher and RoadMesher.
/// Normals are pre-computed on the background thread so the main thread
/// upload is a pure data copy with no heavy computation.
/// </summary>
public class MeshData
{
    public Vector3[] Vertices;
    public int[]     Triangles;
    public Vector2[] UVs;
    public Vector2[] UV1s;    // optional — splatmap coords for terrain
    public Vector3[] Normals; // pre-computed on background thread
}

/// <summary>
/// Lifecycle state of a land or road chunk.
/// </summary>
public enum ChunkState
{
    Unloaded,
    Pending,
    Loaded,
    Hidden
}
