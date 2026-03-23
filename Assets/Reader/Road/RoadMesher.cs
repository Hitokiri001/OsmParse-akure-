using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds road ribbon meshes from clipped OSM polyline data.
/// Road vertex Y elevation is sampled directly from SRTMHeightmap.
/// Roads are completely separate from terrain — no z-fighting because
/// roads sit RoadYOffset metres above the terrain surface.
/// Safe to call on a background thread.
/// </summary>
public static class RoadMesher
{
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
        { "steps",         1f    },
    };

    private const float DefaultHalfWidth = 3f;

    // Road sits this far above terrain to prevent z-fighting
    // Road ribbon sits this far above the terrain surface.
    // Needs to be large enough to clear the largest terrain polygon slope —
    // at LOD2 (17x17 over 1000m) polygons are ~62m wide so even a shallow
    // slope causes several meters of height variation across one polygon.
    private const float RoadYOffset = 0.8f;

    /// <summary>
    /// Builds road meshes with elevation scaling to match scaled terrain.
    /// Must use the same baseElevation and scale as TerrainMesher.BuildScaled.
    /// Safe to call on a background thread.
    /// </summary>
    public static List<RoadMeshData> BuildScaled(
        List<ParsedWay> ways,
        ChunkBounds     chunk,
        int             lodLevel,
        float           baseElevation,
        float           scale)
    {
        var results = new List<RoadMeshData>();
        bool hasSRTM = SRTMHeightmap.Instance != null && SRTMHeightmap.Instance.IsLoaded;

        foreach (ParsedWay way in ways)
        {
            if (way.Points == null || way.Points.Count < 2) continue;

            List<List<Vector2>> segments = Clipper.ClipPolyline(way.Points, chunk);
            if (segments == null || segments.Count == 0) continue;

            float halfWidth = GetHalfWidth(way.Tags);
            int   step      = GetStep(lodLevel);

            foreach (List<Vector2> segment in segments)
            {
                if (segment.Count < 2) continue;
                List<Vector2> simplified = ApplyStep(segment, step);
                if (simplified.Count < 2) continue;

                var centerline3D = new List<Vector3>(simplified.Count);
                foreach (Vector2 p in simplified)
                {
                    // GetElevation returns 0-relative (0=min elevation, ElevRange=max)
                    // so only apply scale — same logic as TerrainMesher.BuildScaled
                    float rawY    = hasSRTM
                        ? SRTMHeightmap.Instance.GetElevation(p.x, p.y)
                        : 0f;
                    float scaledY = rawY * scale + RoadYOffset;
                    centerline3D.Add(new Vector3(p.x, scaledY, p.y));
                }

                MeshData meshData = BuildRibbon(centerline3D, halfWidth);
                if (meshData == null) continue;

                results.Add(new RoadMeshData
                {
                    OsmId      = way.Id,
                    Tags       = way.Tags,
                    MeshData   = meshData,
                    Centerline = centerline3D
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Builds road meshes at real-world SRTM elevation (no scaling).
    /// Safe to call on a background thread.
    /// </summary>
    public static List<RoadMeshData> Build(
        List<ParsedWay> ways,
        ChunkBounds     chunk,
        int             lodLevel)
    {
        var results = new List<RoadMeshData>();

        bool hasSRTM = SRTMHeightmap.Instance != null &&
                       SRTMHeightmap.Instance.IsLoaded;

        foreach (ParsedWay way in ways)
        {
            if (way.Points == null || way.Points.Count < 2) continue;

            // Clip polyline to chunk bounds
            List<List<Vector2>> segments = Clipper.ClipPolyline(way.Points, chunk);
            if (segments == null || segments.Count == 0) continue;

            float halfWidth = GetHalfWidth(way.Tags);
            int   step      = GetStep(lodLevel);

            foreach (List<Vector2> segment in segments)
            {
                if (segment.Count < 2) continue;

                // Apply LOD step reduction
                List<Vector2> simplified = ApplyStep(segment, step);
                if (simplified.Count < 2) continue;

                // Build 3D centerline with SRTM elevation
                var centerline3D = new List<Vector3>(simplified.Count);
                foreach (Vector2 p in simplified)
                {
                    float y = hasSRTM
                        ? SRTMHeightmap.Instance.GetElevation(p.x, p.y) + RoadYOffset
                        : RoadYOffset;
                    centerline3D.Add(new Vector3(p.x, y, p.y));
                }

                MeshData meshData = BuildRibbon(centerline3D, halfWidth);
                if (meshData == null) continue;

                results.Add(new RoadMeshData
                {
                    OsmId      = way.Id,
                    Tags       = way.Tags,
                    MeshData   = meshData,
                    Centerline = centerline3D
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Uploads MeshData to a Unity Mesh. Must be called on the main thread.
    /// </summary>
    public static Mesh Upload(MeshData data)
    {
        if (data == null) return null;

        var mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(data.Vertices);
        mesh.SetTriangles(data.Triangles, 0);
        mesh.SetUVs(0, data.UVs);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // --- Private ---

    // How many meters of road = one full texture tile along the road.
    // Set this to match your road texture's real-world scale.
    // If your texture shows ~6m of road (one set of lane markings),
    // set this to 6. Adjust until the dashes look the right length.
    private const float RoadTextureTileLength = 6f;

    private static MeshData BuildRibbon(List<Vector3> centerline, float halfWidth)
    {
        int n     = centerline.Count;
        var verts = new List<Vector3>(n * 2);
        var tris  = new List<int>((n - 1) * 6);
        var uvs   = new List<Vector2>(n * 2);

        // Cumulative distance along centerline for V tiling
        float cumulative = 0f;
        var   lengths    = new float[n];
        lengths[0]       = 0f;
        for (int i = 1; i < n; i++)
        {
            cumulative += Vector3.Distance(centerline[i - 1], centerline[i]);
            lengths[i]  = cumulative;
        }

        for (int i = 0; i < n; i++)
        {
            Vector3 fwd   = GetForwardXZ(centerline, i);
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x).normalized;

            Vector3 leftPos  = centerline[i] - right * halfWidth;
            Vector3 rightPos = centerline[i] + right * halfWidth;

            // U goes 0 on left edge to 1 on right edge — once across the road width
            // V tiles along the road length — matches portrait texture where road
            // runs top to bottom and dashes repeat vertically
            float v = lengths[i] / RoadTextureTileLength;

            verts.Add(leftPos);
            verts.Add(rightPos);
            uvs.Add(new Vector2(0f, v)); // left edge
            uvs.Add(new Vector2(1f, v)); // right edge
        }

        for (int i = 0; i < n - 1; i++)
        {
            int bl = i * 2,     br = i * 2 + 1;
            int tl = i * 2 + 2, tr = i * 2 + 3;

            tris.Add(bl); tris.Add(tl); tris.Add(br);
            tris.Add(br); tris.Add(tl); tris.Add(tr);
        }

        if (verts.Count < 4) return null;

        return new MeshData
        {
            Vertices  = verts.ToArray(),
            Triangles = tris.ToArray(),
            UVs       = uvs.ToArray()
        };
    }

    private static Vector3 GetForwardXZ(List<Vector3> pts, int i)
    {
        Vector3 dir;
        if (i == 0)
            dir = pts[1] - pts[0];
        else if (i == pts.Count - 1)
            dir = pts[i] - pts[i - 1];
        else
            dir = pts[i + 1] - pts[i - 1];

        dir.y = 0f;
        return dir == Vector3.zero ? Vector3.forward : dir.normalized;
    }

    private static float GetHalfWidth(Dictionary<string, string> tags)
    {
        if (tags != null && tags.TryGetValue("highway", out string hw) &&
            HalfWidths.TryGetValue(hw, out float w))
            return w;
        return DefaultHalfWidth;
    }

    private static int GetStep(int lod) => Mathf.Max(1, lod + 1);

    private static List<Vector2> ApplyStep(List<Vector2> pts, int step)
    {
        if (step <= 1) return pts;
        var result = new List<Vector2>();
        for (int i = 0; i < pts.Count; i += step)
            result.Add(pts[i]);
        if (result[result.Count - 1] != pts[pts.Count - 1])
            result.Add(pts[pts.Count - 1]);
        return result;
    }
}

public class RoadMeshData
{
    public long                       OsmId;
    public Dictionary<string, string> Tags;
    public MeshData                   MeshData;
    public List<Vector3>              Centerline;
}
