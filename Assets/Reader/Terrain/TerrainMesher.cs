using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds a terrain mesh for a chunk by sampling the SRTM heightmap.
/// Gap-free: border vertices are sampled at exact integer heightmap
/// coordinates shared with neighbouring chunks so there are zero
/// floating-point discrepancies at chunk edges.
///
/// Road-terrain blending uses the same three-zone technique as Road Architect
/// (GSDTerraforming.cs):
///   Zone 1 — road bed   (0 → halfWidth)              flat at road elevation
///   Zone 2 — shoulder   (halfWidth → +ShoulderWidth)  lerp road → SRTM
///   Zone 3 — natural    (beyond shoulder)              pure SRTM
///
/// Roads are still a separate mesh — the terrain is carved to match them,
/// so the road ribbon sits flush with no clipping from below.
/// Safe to call on a background thread.
/// </summary>
public static class TerrainMesher
{
    private static readonly int[] LODGridSizes = { 65, 33, 17 };

    // How far beyond road half-width the terrain blends back to natural height.
    // Road Architect calls this "Heights match width". 4m works well for Nigerian
    // roads — increase for wider roads like motorways.
    public static float ShoulderWidth = 4f;

    // Road half-widths — must stay in sync with RoadMesher
    private static readonly Dictionary<string, float> HalfWidths
        = new Dictionary<string, float>
    {
        { "motorway",      8f    },
        { "trunk",         7f    },
        { "primary",       6f    },
        { "secondary",     5f    },
        { "tertiary",      4f    },
        { "residential",   3.5f  },
        { "living_street", 3f    },
        { "service",       2.5f  },
        { "unclassified",  3f    },
        { "track",         2f    },
        { "footway",       1f    },
        { "cycleway",      1f    },
        { "path",          0.8f  },
    };
    private const float DefaultHalfWidth = 3f;

    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds terrain mesh with road blending. Pass road ways so the terrain
    /// is carved flat under road corridors with smooth shoulder transitions.
    /// Safe to call on a background thread.
    /// </summary>
    public static MeshData Build(
        ChunkBounds     chunk,
        int             lodLevel,
        float           distanceFromPlayer,
        List<ParsedWay> roads = null)
    {
        if (SRTMHeightmap.Instance == null || !SRTMHeightmap.Instance.IsLoaded)
            return null;

        int gridSize = GetGridSize(lodLevel);
        float[,] elevations = SampleElevationGrid(chunk, gridSize, distanceFromPlayer);

        if (roads != null && roads.Count > 0)
            ApplyRoadBlending(elevations, roads, chunk, gridSize);

        return BuildMesh(chunk, elevations, gridSize);
    }

    /// <summary>
    /// Scaled version for prefab baking — applies scale factor to compress height range.
    /// SRTMHeightmap.SampleGrid already returns elevation relative to base (0=min, ElevRange=max)
    /// so we only multiply by scale — DO NOT subtract base elevation again or terrain goes underground.
    /// Pass roads so the baked prefab has correct road-terrain blending.
    /// Safe to call on a background thread.
    /// </summary>
    public static MeshData BuildScaled(
        ChunkBounds     chunk,
        int             lodLevel,
        float           distanceFromPlayer,
        float           baseElevation,
        float           scale,
        List<ParsedWay> roads = null)
    {
        if (SRTMHeightmap.Instance == null || !SRTMHeightmap.Instance.IsLoaded)
            return null;

        int gridSize = GetGridSize(lodLevel);
        float[,] elevations = SampleElevationGrid(chunk, gridSize, distanceFromPlayer);

        if (roads != null && roads.Count > 0)
            ApplyRoadBlending(elevations, roads, chunk, gridSize);

        // Only scale — SampleGrid returns 0-relative values so min terrain = Y=0 naturally
        for (int r = 0; r < gridSize; r++)
            for (int c = 0; c < gridSize; c++)
                elevations[r, c] = elevations[r, c] * scale;

        return BuildMesh(chunk, elevations, gridSize);
    }

    /// <summary>
    /// Uploads MeshData to a Unity Mesh. Must be called on the main thread.
    /// Normals are already pre-computed in MeshData so this is a pure
    /// data copy — no heavy computation on the main thread.
    /// </summary>
    public static Mesh Upload(MeshData data)
    {
        if (data == null) return null;

        var mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(data.Vertices);
        mesh.SetTriangles(data.Triangles, 0);
        mesh.SetUVs(0, data.UVs);
        if (data.UV1s    != null && data.UV1s.Length    > 0) mesh.SetUVs(1, data.UV1s);
        if (data.Normals != null && data.Normals.Length > 0) mesh.SetNormals(data.Normals);
        else mesh.RecalculateNormals(); // fallback for road meshes that don't pre-compute
        mesh.RecalculateBounds();
        return mesh;
    }

    // --- Private ---

    /// <summary>
    /// Applies three-zone road blending to the elevation grid.
    /// Mirrors Road Architect's GSDTerraforming approach adapted for our mesh system.
    ///
    ///   Zone 1 (road bed)  dist ≤ halfWidth              → roadElev (flat)
    ///   Zone 2 (shoulder)  halfWidth < dist ≤ halfWidth+ShoulderWidth → lerp(roadElev, srtmElev, t)
    ///   Zone 3 (natural)   dist > halfWidth+ShoulderWidth → srtmElev (unchanged)
    ///
    /// The elevation grid already contains raw SRTM values so we use those as
    /// the "natural" target when blending back in the shoulder zone.
    /// </summary>
    private static void ApplyRoadBlending(
        float[,]        elevations,
        List<ParsedWay> roads,
        ChunkBounds     chunk,
        int             gridSize)
    {
        // Pre-build road segment list — sample road elevation from SRTM at each endpoint
        var segs = new List<RoadSeg>();

        foreach (ParsedWay road in roads)
        {
            if (road.Points == null || road.Points.Count < 2) continue;

            float hw = GetHalfWidth(road.Tags);

            for (int i = 0; i < road.Points.Count - 1; i++)
            {
                Vector2 a = road.Points[i];
                Vector2 b = road.Points[i + 1];

                // Sample road elevation at both endpoints directly from SRTM
                // (same source as terrain, so units match perfectly)
                float elevA = SRTMHeightmap.Instance.GetElevation(a.x, a.y);
                float elevB = SRTMHeightmap.Instance.GetElevation(b.x, b.y);

                segs.Add(new RoadSeg
                {
                    A = a, B = b,
                    HalfWidth = hw,
                    TotalWidth = hw + ShoulderWidth,
                    ElevA = elevA, ElevB = elevB
                });
            }
        }

        if (segs.Count == 0) return;

        float stepX = chunk.WorldSize / (gridSize - 1);
        float stepZ = chunk.WorldSize / (gridSize - 1);

        for (int row = 0; row < gridSize; row++)
        {
            for (int col = 0; col < gridSize; col++)
            {
                // Skip border vertices — they must stay at raw SRTM to match
                // the identical border vertices on adjacent chunks.
                // Road blending on borders breaks the gap-free edge guarantee.
                if (row == 0 || row == gridSize - 1 ||
                    col == 0 || col == gridSize - 1)
                    continue;

                float worldX = chunk.WorldMin.x + col * stepX;
                float worldZ = chunk.WorldMin.y + row * stepZ;
                var   pt     = new Vector2(worldX, worldZ);

                float srtmElev = elevations[row, col]; // original SRTM — used for shoulder blend

                float bestDist     = float.MaxValue;
                float bestRoadElev = 0f;
                float bestHalfW    = 0f;
                float bestTotalW   = 0f;

                foreach (var seg in segs)
                {
                    // Only check segments within maximum influence range
                    if (!CouldInfluence(pt, seg)) continue;

                    float t       = ClosestT(pt, seg.A, seg.B);
                    Vector2 proj  = Vector2.Lerp(seg.A, seg.B, t);
                    float dist    = Vector2.Distance(pt, proj);

                    if (dist < seg.TotalWidth && dist < bestDist)
                    {
                        bestDist     = dist;
                        bestRoadElev = Mathf.Lerp(seg.ElevA, seg.ElevB, t);
                        bestHalfW    = seg.HalfWidth;
                        bestTotalW   = seg.TotalWidth;
                    }
                }

                if (bestDist >= float.MaxValue) continue; // not near any road

                if (bestDist <= bestHalfW)
                {
                    // Zone 1 — road bed: flat at road elevation
                    elevations[row, col] = bestRoadElev;
                }
                else
                {
                    // Zone 2 — shoulder: smooth lerp from road elevation back to SRTM
                    // t=0 at road edge, t=1 at shoulder outer edge
                    float t = (bestDist - bestHalfW) / ShoulderWidth;
                    t = Mathf.Clamp01(t);
                    // Use smoothstep for a more natural S-curve transition
                    t = t * t * (3f - 2f * t);
                    elevations[row, col] = Mathf.Lerp(bestRoadElev, srtmElev, t);
                }
            }
        }
    }

    private static bool CouldInfluence(Vector2 pt, RoadSeg seg)
    {
        // Quick AABB reject before expensive distance check
        float margin = seg.TotalWidth;
        float minX   = Mathf.Min(seg.A.x, seg.B.x) - margin;
        float maxX   = Mathf.Max(seg.A.x, seg.B.x) + margin;
        float minY   = Mathf.Min(seg.A.y, seg.B.y) - margin;
        float maxY   = Mathf.Max(seg.A.y, seg.B.y) + margin;
        return pt.x >= minX && pt.x <= maxX && pt.y >= minY && pt.y <= maxY;
    }

    private static float ClosestT(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab  = b - a;
        float   len = ab.sqrMagnitude;
        if (len < 0.0001f) return 0f;
        return Mathf.Clamp01(Vector2.Dot(p - a, ab) / len);
    }

    private static float GetHalfWidth(Dictionary<string, string> tags)
    {
        if (tags != null && tags.TryGetValue("highway", out string hw) &&
            HalfWidths.TryGetValue(hw, out float w))
            return w;
        return DefaultHalfWidth;
    }

    private struct RoadSeg
    {
        public Vector2 A, B;
        public float   HalfWidth;   // road surface only
        public float   TotalWidth;  // halfWidth + ShoulderWidth
        public float   ElevA, ElevB;
    }

    private static float[,] SampleElevationGrid(
        ChunkBounds chunk, int gridSize, float distanceFromPlayer)
    {
        var elevations = new float[gridSize, gridSize];

        // Get the heightmap LOD grid and its world coverage
        SRTMHeightmap.Instance.GetLODInfo(
            distanceFromPlayer,
            out float[,] heightGrid,
            out int      lodSize,
            out float    gridWorldMinX,
            out float    gridWorldMinZ,
            out float    gridWorldWidth,
            out float    gridWorldLength);

        // Compute exact heightmap texel step size in world units
        // This is what neighbouring chunks must agree on at their shared edge
        float texelStepX = gridWorldWidth  / (lodSize - 1);
        float texelStepZ = gridWorldLength / (lodSize - 1);

        for (int row = 0; row < gridSize; row++)
        {
            for (int col = 0; col < gridSize; col++)
            {
                // World position of this grid vertex
                float worldX = chunk.WorldMin.x + (col / (float)(gridSize - 1)) * chunk.WorldSize;
                float worldZ = chunk.WorldMin.y + (row / (float)(gridSize - 1)) * chunk.WorldSize;

                // Convert to fractional heightmap texel coordinate
                float texelX = (worldX - gridWorldMinX) / texelStepX;
                float texelZ = (worldZ - gridWorldMinZ) / texelStepZ;

                // For border vertices, snap to nearest integer texel
                // so adjacent chunks sample the identical texel
                bool isBorderCol = col == 0 || col == gridSize - 1;
                bool isBorderRow = row == 0 || row == gridSize - 1;

                float elev;
                if (isBorderCol && isBorderRow)
                {
                    // Corner — snap both axes
                    elev = SampleNearest(heightGrid, lodSize,
                                         Mathf.RoundToInt(texelX),
                                         Mathf.RoundToInt(texelZ));
                }
                else if (isBorderCol)
                {
                    // Left/right edge — snap X, interpolate Z
                    elev = SampleBilinearSnapX(heightGrid, lodSize,
                                                Mathf.RoundToInt(texelX), texelZ);
                }
                else if (isBorderRow)
                {
                    // Top/bottom edge — snap Z, interpolate X
                    elev = SampleBilinearSnapZ(heightGrid, lodSize,
                                                texelX, Mathf.RoundToInt(texelZ));
                }
                else
                {
                    // Interior — full bilinear interpolation
                    elev = SampleBilinear(heightGrid, lodSize, texelX, texelZ);
                }

                elevations[row, col] = elev * SRTMHeightmap.Instance.ElevRange;
            }
        }

        return elevations;
    }

    private static float SampleNearest(float[,] grid, int size, int tx, int tz)
    {
        tx = Mathf.Clamp(tx, 0, size - 1);
        tz = Mathf.Clamp(tz, 0, size - 1);
        return grid[tz, tx];
    }

    private static float SampleBilinearSnapX(float[,] grid, int size, int tx, float tz)
    {
        tx = Mathf.Clamp(tx, 0, size - 1);
        int   tz0 = Mathf.Clamp(Mathf.FloorToInt(tz), 0, size - 2);
        int   tz1 = tz0 + 1;
        float t   = tz - tz0;
        return Mathf.Lerp(grid[tz0, tx], grid[tz1, tx], t);
    }

    private static float SampleBilinearSnapZ(float[,] grid, int size, float tx, int tz)
    {
        tz = Mathf.Clamp(tz, 0, size - 1);
        int   tx0 = Mathf.Clamp(Mathf.FloorToInt(tx), 0, size - 2);
        int   tx1 = tx0 + 1;
        float t   = tx - tx0;
        return Mathf.Lerp(grid[tz, tx0], grid[tz, tx1], t);
    }

    private static float SampleBilinear(float[,] grid, int size, float tx, float tz)
    {
        int   tx0 = Mathf.Clamp(Mathf.FloorToInt(tx), 0, size - 2);
        int   tx1 = tx0 + 1;
        int   tz0 = Mathf.Clamp(Mathf.FloorToInt(tz), 0, size - 2);
        int   tz1 = tz0 + 1;
        float tu  = tx - tx0;
        float tv  = tz - tz0;

        return Mathf.Lerp(
            Mathf.Lerp(grid[tz0, tx0], grid[tz0, tx1], tu),
            Mathf.Lerp(grid[tz1, tx0], grid[tz1, tx1], tu),
            tv);
    }

    private static MeshData BuildMesh(ChunkBounds chunk, float[,] elevations, int gridSize)
    {
        int vertCount = gridSize * gridSize;
        int triCount  = (gridSize - 1) * (gridSize - 1) * 6;

        var verts = new Vector3[vertCount];
        var uv0s  = new Vector2[vertCount]; // tiling UVs for texture
        var uv1s  = new Vector2[vertCount]; // normalized chunk UVs for splatmap
        var tris  = new int[triCount];

        for (int row = 0; row < gridSize; row++)
        {
            for (int col = 0; col < gridSize; col++)
            {
                int   idx      = row * gridSize + col;
                float normalX  = col / (float)(gridSize - 1);
                float normalZ  = row / (float)(gridSize - 1);
                float worldX   = chunk.WorldMin.x + normalX * chunk.WorldSize;
                float worldZ   = chunk.WorldMin.y + normalZ * chunk.WorldSize;
                float worldY   = elevations[row, col];

                verts[idx] = new Vector3(worldX, worldY, worldZ);
                uv0s[idx]  = new Vector2(normalX * chunk.WorldSize / 20f,
                                          normalZ * chunk.WorldSize / 20f); // tiles every 20m
                uv1s[idx]  = new Vector2(normalX, normalZ); // splatmap lookup
            }
        }

        int triIdx = 0;
        for (int row = 0; row < gridSize - 1; row++)
        {
            for (int col = 0; col < gridSize - 1; col++)
            {
                int bl = row       * gridSize + col;
                int br = row       * gridSize + col + 1;
                int tl = (row + 1) * gridSize + col;
                int tr = (row + 1) * gridSize + col + 1;

                // Clockwise winding — faces up in Unity
                tris[triIdx++] = bl; tris[triIdx++] = tl; tris[triIdx++] = br;
                tris[triIdx++] = br; tris[triIdx++] = tl; tris[triIdx++] = tr;
            }
        }

        return new MeshData
        {
            Vertices  = verts,
            Triangles = tris,
            UVs       = uv0s,
            UV1s      = uv1s,
            Normals   = ComputeNormalsFromHeightmap(verts, chunk, gridSize)
        };
    }

    /// <summary>
    /// Computes vertex normals analytically from the SRTM heightmap gradient
    /// using central differences. This is the key fix for visible chunk seams
    /// and sharp hilltops.
    ///
    /// Triangle-based normals (old approach) fail at chunk borders because
    /// border vertices only know about triangles inside their own chunk —
    /// the slope of the neighbouring chunk is ignored, causing a lighting
    /// discontinuity at every chunk edge.
    ///
    /// Gradient-based normals sample the heightmap one step either side of
    /// each vertex. Since both adjacent chunks sample the same underlying
    /// heightmap data the gradient — and therefore the normal — is identical
    /// at shared border points. Seams disappear completely.
    /// </summary>
    private static Vector3[] ComputeNormalsFromHeightmap(
        Vector3[]   verts,
        ChunkBounds chunk,
        int         gridSize)
    {
        var normals = new Vector3[verts.Length];

        // Step size for central difference sampling — one mesh polygon width
        float step = chunk.WorldSize / (gridSize - 1);

        for (int row = 0; row < gridSize; row++)
        {
            for (int col = 0; col < gridSize; col++)
            {
                int     idx    = row * gridSize + col;
                Vector3 v      = verts[idx];

                // Sample elevation one step in each direction
                // SRTMHeightmap.GetElevation handles out-of-bounds via clamping
                // so this works correctly at chunk borders — the sample outside
                // the chunk uses the same heightmap data the neighbour chunk uses
                float hL = SRTMHeightmap.Instance.GetElevation(v.x - step, v.z);
                float hR = SRTMHeightmap.Instance.GetElevation(v.x + step, v.z);
                float hD = SRTMHeightmap.Instance.GetElevation(v.x, v.z - step);
                float hU = SRTMHeightmap.Instance.GetElevation(v.x, v.z + step);

                // Central difference gradient → surface normal
                // The cross product of the two tangent vectors gives the normal
                var normal = new Vector3(hL - hR, 2f * step, hD - hU);
                normals[idx] = normal.normalized;
            }
        }

        return normals;
    }

    public static int GetGridSize(int lodLevel)
    {
        if (lodLevel < 0 || lodLevel >= LODGridSizes.Length)
            return LODGridSizes[LODGridSizes.Length - 1];
        return LODGridSizes[lodLevel];
    }
}
