// VegetationMesher.cs
// Pure C# — no UnityEngine MonoBehaviour API (safe to run on background thread).
// Outputs a flat list of TreePlacement structs consumed by VegetationChunk.
//
// Placement priority:
//   1. OSM natural=tree nodes   (from TileData.Trees — real surveyed positions)
//   2. WorldCover G-channel     (Forest/Shrubland density — fills unmapped areas)
//
// Exclusion rules (both sources):
//   - Splatmap A channel > WaterThreshold  → water, skip
//   - Within road half-width + RoadBuffer  → road/shoulder, skip
//   - Closer than MinTreeSpacing to any existing tree → skip

using System.Collections.Generic;
using UnityEngine;

public static class VegetationMesher
{
    // ── Constants ────────────────────────────────────────────────────────────

    public const float WaterThreshold  = 0.3f;  // splatmap A above this = water
    public const float ForestThreshold = 0.25f; // splatmap G above this = forest
    public const float CropThreshold   = 0.40f; // splatmap R above this = cropland
    public const float MinTreeSpacing  = 7f;     // metres — prevents overlap
    public const float RoadBuffer      = 5f;     // metres added beyond road half-width

    // Road half-widths by OSM highway tag value.
    // ParsedWay.Tags["highway"] contains the raw OSM string ("primary", "residential", etc.)
    static readonly Dictionary<string, float> HalfWidths = new()
    {
        { "motorway",    4.0f },
        { "trunk",       4.0f },
        { "primary",     3.0f },
        { "secondary",   2.5f },
        { "tertiary",    2.0f },
        { "residential", 1.75f },
        { "service",     1.5f },
        { "track",       1.5f },
        { "path",        0.75f },
        { "footway",     0.75f },
        { "cycleway",    0.75f },
    };

    const int SplatSize = 33; // worldcover_splatmap.py output size

    // ── Public API ───────────────────────────────────────────────────────────

    public struct TreePlacement
    {
        public Vector3 WorldPosition;
        /// <summary>0 = Oil Palm  1 = Tropical Hardwood  2 = Farmland Crop</summary>
        public int     SpeciesIndex;
        public bool    FromOSM;  // false = WorldCover density placement
    }

    /// <param name="tile">Parsed OSM tile — Trees list populated by OsmParser.</param>
    /// <param name="splatmap">33x33 Color32 array (row-major, origin = bottom-left).
    ///   Pass null to skip WorldCover density fallback.</param>
    /// <param name="worldBounds">Rect in world XZ: x=west edge, y=south edge.</param>
    /// <param name="getElevation">Thread-safe elevation callback (SRTMHeightmap.Instance.GetElevation).</param>
    /// <param name="densityScale">0 = no density trees, 1 = full density.</param>
    /// <param name="seed">Deterministic seed — use chunk coord hash for stable results on reload.</param>
    public static List<TreePlacement> Build(
        TileData                         tile,
        Color32[]                        splatmap,
        Rect                             worldBounds,
        System.Func<float, float, float> getElevation,
        float                            densityScale = 1f,
        uint                             seed         = 12345)
    {
        var result = new List<TreePlacement>(64);
        var rng    = new System.Random((int)seed);

        // ── Pass 1: OSM natural=tree nodes ───────────────────────────────────
        foreach (var node in tile.Trees)
        {
            // ParsedNode.WorldPos: x = east (world X), z = north (world Z)
            float wx = node.WorldPos.x;
            float wz = node.WorldPos.z;

            if (!InBounds(worldBounds, wx, wz))          continue;
            if (IsWater(splatmap, worldBounds, wx, wz))  continue;
            if (IsOnRoad(tile, wx, wz))                  continue;

            result.Add(new TreePlacement
            {
                WorldPosition = new Vector3(wx, getElevation(wx, wz), wz),
                SpeciesIndex  = 0,    // Oil Palm — dominant surveyed species in Akure
                FromOSM       = true
            });
        }

        // ── Pass 2: WorldCover density fallback ──────────────────────────────
        if (splatmap != null && densityScale > 0.01f)
        {
            float cellW = worldBounds.width  / SplatSize;
            float cellH = worldBounds.height / SplatSize;

            for (int py = 0; py < SplatSize; py++)
            for (int px = 0; px < SplatSize; px++)
            {
                Color32 c = splatmap[py * SplatSize + px];
                float   g = c.g / 255f;  // Forest / Shrubland
                float   r = c.r / 255f;  // Grass / Cropland
                float   a = c.a / 255f;  // Water

                if (a > WaterThreshold) continue;

                int   species = -1;
                float density = 0f;

                if (g > ForestThreshold)
                {
                    species = 1;                        // Hardwood
                    density = g * densityScale;
                }
                else if (r > CropThreshold)
                {
                    species = 2;                        // Farmland Crop
                    density = r * 0.5f * densityScale;
                }

                if (species < 0 || density < 0.05f) continue;

                // World centre of this splatmap cell
                float cx = worldBounds.x + (px + 0.5f) * cellW;
                float cz = worldBounds.y + (py + 0.5f) * cellH;

                // Up to 3 trees per cell, scaled by density
                int count = Mathf.RoundToInt(density * 3f);
                if (rng.NextDouble() < 0.3)
                    count += rng.NextDouble() < 0.5 ? 1 : -1;
                count = Mathf.Clamp(count, 0, 4);

                for (int t = 0; t < count; t++)
                {
                    float wx = cx + (float)(rng.NextDouble() - 0.5) * cellW;
                    float wz = cz + (float)(rng.NextDouble() - 0.5) * cellH;

                    if (!InBounds(worldBounds, wx, wz))          continue;
                    if (IsWater(splatmap, worldBounds, wx, wz))  continue;
                    if (IsOnRoad(tile, wx, wz))                  continue;
                    if (TooClose(result, wx, wz))                continue;

                    result.Add(new TreePlacement
                    {
                        WorldPosition = new Vector3(wx, getElevation(wx, wz), wz),
                        SpeciesIndex  = species,
                        FromOSM       = false
                    });
                }
            }
        }

        return result;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    static bool InBounds(Rect bounds, float wx, float wz) =>
        wx >= bounds.xMin && wx <= bounds.xMax &&
        wz >= bounds.yMin && wz <= bounds.yMax;

    static bool IsWater(Color32[] splatmap, Rect bounds, float wx, float wz)
    {
        if (splatmap == null) return false;
        int px = Mathf.Clamp(
            Mathf.FloorToInt((wx - bounds.x) / bounds.width  * SplatSize), 0, SplatSize - 1);
        int pz = Mathf.Clamp(
            Mathf.FloorToInt((wz - bounds.y) / bounds.height * SplatSize), 0, SplatSize - 1);
        return splatmap[pz * SplatSize + px].a / 255f > WaterThreshold;
    }

    static bool IsOnRoad(TileData tile, float wx, float wz)
    {
        foreach (var road in tile.Roads)
        {
            // Look up half-width from the highway tag string, not WayType enum
            float hw = 2f; // safe fallback for any unrecognised highway type
            if (road.Tags != null && road.Tags.TryGetValue("highway", out string hwTag))
                HalfWidths.TryGetValue(hwTag, out hw);

            float exclusion = hw + RoadBuffer;
            float excSq     = exclusion * exclusion;

            // ParsedWay.Points: Vector2 where .x = world X, .y = world Z
            var pts = road.Points;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                float dist = DistToSegmentSq(
                    wx,          wz,
                    pts[i  ].x,  pts[i  ].y,
                    pts[i+1].x,  pts[i+1].y);
                if (dist < excSq) return true;
            }
        }
        return false;
    }

    static bool TooClose(List<TreePlacement> placed, float wx, float wz)
    {
        float minSq = MinTreeSpacing * MinTreeSpacing;
        for (int i = placed.Count - 1; i >= 0; i--)
        {
            float dx = placed[i].WorldPosition.x - wx;
            float dz = placed[i].WorldPosition.z - wz;
            if (dx * dx + dz * dz < minSq) return true;
            // Early exit: once x-distance alone exceeds spacing, stop looking back
            if (Mathf.Abs(dx) > MinTreeSpacing) break;
        }
        return false;
    }

    /// Squared distance from point (px,pz) to segment (ax,az)→(bx,bz)
    static float DistToSegmentSq(
        float px, float pz,
        float ax, float az,
        float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        float lenSq = dx * dx + dz * dz;
        if (lenSq < 1e-6f)
        {
            float ex = px - ax, ez = pz - az;
            return ex * ex + ez * ez;
        }
        float t  = Mathf.Clamp01(((px - ax) * dx + (pz - az) * dz) / lenSq);
        float nx = ax + t * dx - px;
        float nz = az + t * dz - pz;
        return nx * nx + nz * nz;
    }
}
