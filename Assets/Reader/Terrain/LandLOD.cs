using UnityEngine;

/// <summary>
/// Defines LOD levels for terrain chunks.
/// LOD 0 = 129x129 grid (high detail, close range)
/// LOD 1 = 65x65  grid (medium detail)
/// LOD 2 = 33x33  grid (low detail, far range)
/// Distances should match SRTMHeightmap LOD1Distance and LOD2Distance.
/// </summary>
public static class LandLOD
{
    private static float[] _distances = { 1000f, 2000f, 4000f };

    public static int LODCount => _distances.Length;

    public static void Configure(float[] distances, float[] tolerances = null)
    {
        if (distances != null && distances.Length >= 3)
        {
            _distances = distances;
            LandChunk.LODDistances = distances;
        }

        Debug.Log($"[LandLOD] Configured with {LODCount} LOD levels.");
    }

    public static int Evaluate(float distance)
    {
        for (int i = 0; i < _distances.Length; i++)
            if (distance <= _distances[i]) return i;
        return _distances.Length - 1;
    }

    public static float GetDistance(int lodLevel)
    {
        if (lodLevel < 0 || lodLevel >= _distances.Length)
            return _distances[_distances.Length - 1];
        return _distances[lodLevel];
    }

    public static void DebugPrint()
    {
        for (int i = 0; i < _distances.Length; i++)
            Debug.Log($"[LandLOD] LOD {i} — Distance: {_distances[i]}m | " +
                      $"Grid: {new int[]{129,65,33}[i]}x{new int[]{129,65,33}[i]}");
    }
}
