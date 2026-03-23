using UnityEngine;

/// <summary>
/// Defines the geographic and world-space boundaries of a single chunk.
/// Chunks are identified by their grid coordinate (ChunkX, ChunkY).
/// All world-space values are derived from Mercator projection — 
/// make sure Mercator.SetOrigin() is called before creating any ChunkBounds.
/// </summary>
public struct ChunkBounds
{
    // Grid position of this chunk in the world grid
    public int ChunkX;
    public int ChunkY;

    // Geographic bounds (lat/lon)
    public double MinLat;
    public double MaxLat;
    public double MinLon;
    public double MaxLon;

    // World-space bounds (Unity units, derived from Mercator)
    public Vector2 WorldMin;
    public Vector2 WorldMax;
    public Vector2 WorldCenter;
    public float WorldSize;

    /// <summary>
    /// Creates a ChunkBounds from a grid coordinate and chunk size in meters.
    /// Requires Mercator.SetOrigin() to have been called first.
    /// </summary>
    public static ChunkBounds FromGrid(int chunkX, int chunkY, float chunkSizeMeters)
    {
        ChunkBounds b = new ChunkBounds();
        b.ChunkX = chunkX;
        b.ChunkY = chunkY;
        b.WorldSize = chunkSizeMeters;

        b.WorldMin = new Vector2(chunkX * chunkSizeMeters, chunkY * chunkSizeMeters);
        b.WorldMax = new Vector2(b.WorldMin.x + chunkSizeMeters, b.WorldMin.y + chunkSizeMeters);
        b.WorldCenter = (b.WorldMin + b.WorldMax) * 0.5f;

        // Approximate geographic bounds from world space
        // Not perfectly accurate at large scales but sufficient for filtering OSM data
        float mpdLat = Mercator.MetersPerDegreeLat();
        float mpdLon = Mercator.MetersPerDegreeLon();

        b.MinLat = Mercator.OriginLat + (b.WorldMin.y / mpdLat);
        b.MaxLat = Mercator.OriginLat + (b.WorldMax.y / mpdLat);
        b.MinLon = Mercator.OriginLon + (b.WorldMin.x / mpdLon);
        b.MaxLon = Mercator.OriginLon + (b.WorldMax.x / mpdLon);

        return b;
    }

    /// <summary>
    /// Returns the chunk grid coordinate that contains the given world position.
    /// </summary>
    public static Vector2Int WorldToGrid(Vector2 worldPos, float chunkSizeMeters)
    {
        int x = Mathf.FloorToInt(worldPos.x / chunkSizeMeters);
        int y = Mathf.FloorToInt(worldPos.y / chunkSizeMeters);
        return new Vector2Int(x, y);
    }

    /// <summary>
    /// Returns true if the given lat/lon falls within this chunk's geographic bounds.
    /// </summary>
    public bool ContainsLatLon(double lat, double lon)
    {
        return lat >= MinLat && lat <= MaxLat &&
               lon >= MinLon && lon <= MaxLon;
    }

    /// <summary>
    /// Returns true if the given world-space point falls within this chunk.
    /// </summary>
    public bool ContainsWorld(Vector2 point)
    {
        return point.x >= WorldMin.x && point.x <= WorldMax.x &&
               point.y >= WorldMin.y && point.y <= WorldMax.y;
    }

    /// <summary>
    /// Returns true if a lat/lon bounding box overlaps with this chunk's geographic bounds.
    /// Useful for quickly filtering OSM ways and polygons that might cross the chunk.
    /// </summary>
    public bool OverlapsLatLon(double minLat, double maxLat, double minLon, double maxLon)
    {
        return !(maxLat < MinLat || minLat > MaxLat ||
                 maxLon < MinLon || minLon > MaxLon);
    }

    /// <summary>
    /// Returns true if a world-space rect overlaps with this chunk.
    /// </summary>
    public bool OverlapsWorld(Vector2 min, Vector2 max)
    {
        return !(max.x < WorldMin.x || min.x > WorldMax.x ||
                 max.y < WorldMin.y || min.y > WorldMax.y);
    }

    public override string ToString()
    {
        return $"Chunk({ChunkX},{ChunkY}) | Lat[{MinLat:F5},{MaxLat:F5}] Lon[{MinLon:F5},{MaxLon:F5}]";
    }
}
