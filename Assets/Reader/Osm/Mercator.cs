using UnityEngine;

/// <summary>
/// Converts geographic coordinates (latitude/longitude) to Unity world space
/// using a flat Mercator-style projection anchored to a defined origin.
/// Set Origin once before any conversions (e.g. center of your OSM bounds).
/// </summary>
public static class Mercator
{
    // Earth's radius in meters
    private const double EarthRadius = 6378137.0;

    // The geographic origin — all world positions are relative to this point
    public static double OriginLat { get; private set; }
    public static double OriginLon { get; private set; }

    private static double _originX;
    private static double _originZ;
    private static bool _initialized;

    /// <summary>
    /// Must be called once before any conversions.
    /// Typically set to the center of your OSM file's bounding box.
    /// </summary>
    public static void SetOrigin(double lat, double lon)
    {
        OriginLat = lat;
        OriginLon = lon;
        _originX = LonToMeters(lon);
        _originZ = LatToMeters(lat);
        _initialized = true;
    }

    /// <summary>
    /// Converts a lat/lon coordinate to a Unity world-space Vector3.
    /// Y is always 0 — terrain height is applied separately.
    /// </summary>
    public static Vector3 ToWorld(double lat, double lon)
    {
        if (!_initialized)
        {
            Debug.LogError("[Mercator] Origin not set. Call Mercator.SetOrigin() first.");
            return Vector3.zero;
        }

        double x = LonToMeters(lon) - _originX;
        double z = LatToMeters(lat) - _originZ;

        // Scale down from meters to Unity units (1 unit = 1 meter here, adjust if needed)
        return new Vector3((float)x, 0f, (float)z);
    }

    /// <summary>
    /// Returns world position as a Vector2 (X/Z only), useful for 2D chunk math.
    /// </summary>
    public static Vector2 ToWorld2D(double lat, double lon)
    {
        Vector3 w = ToWorld(lat, lon);
        return new Vector2(w.x, w.z);
    }

    /// <summary>
    /// Approximate meters per degree of latitude at the origin.
    /// Useful for chunk size calculations.
    /// </summary>
    public static float MetersPerDegreeLat()
    {
        return (float)(Mathf.PI * EarthRadius / 180.0);
    }

    /// <summary>
    /// Approximate meters per degree of longitude at the origin latitude.
    /// </summary>
    public static float MetersPerDegreeLon()
    {
        double latRad = OriginLat * Mathf.Deg2Rad;
        return (float)(Mathf.PI * EarthRadius * System.Math.Cos(latRad) / 180.0);
    }

    // --- Private helpers ---

    private static double LonToMeters(double lon)
    {
        return lon * (System.Math.PI / 180.0) * EarthRadius;
    }

    private static double LatToMeters(double lat)
    {
        double latRad = lat * (System.Math.PI / 180.0);
        return System.Math.Log(System.Math.Tan(System.Math.PI / 4.0 + latRad / 2.0)) * EarthRadius;
    }
}
