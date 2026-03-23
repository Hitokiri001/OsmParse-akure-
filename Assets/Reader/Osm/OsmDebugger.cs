using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Attach to the same GameObject as WorldStreamer.
/// Runs an initial diagnostic then prints a periodic report.
///
/// Checks:
///   - SRTMHeightmap loaded + elevation range + sample at player
///   - SplatmapLoader present + PNG count
///   - OSM tile folder exists + player tile present
///   - Tile parse: road / landmass / building counts + highway type breakdown
///   - TerrainMesher.Build() test on player chunk — reports Y range
///   - RoadMesher.Build() test on player chunk — reports mesh count
///   - Periodic: LandChunks / RoadChunks child counts + pending
/// </summary>
public class OsmDebugger : MonoBehaviour
{
    [Header("Settings")]
    public float ReportInterval    = 5f;
    public bool  LogTileContents   = true;
    public bool  TestTerrainMesher = true;
    public bool  TestRoadMesher    = true;

    private WorldStreamer _streamer;
    private float         _timer;

    private void Start()
    {
        _streamer = GetComponent<WorldStreamer>();
        if (_streamer == null)
        {
            Debug.LogError("[OsmDebugger] No WorldStreamer on this GameObject.");
            enabled = false;
            return;
        }

        StartCoroutine(InitialCheck());
    }

    private void Update()
    {
        _timer += Time.deltaTime;
        if (_timer >= ReportInterval)
        {
            _timer = 0f;
            PrintReport();
        }
    }

    // -----------------------------------------------------------------------
    // Initial Check
    // -----------------------------------------------------------------------

    private IEnumerator InitialCheck()
    {
        yield return new WaitForSeconds(1f);

        Debug.Log("=== [OsmDebugger] Initial Check ===");
        Debug.Log($"[OsmDebugger] Player position  : {_streamer.PlayerTransform?.position}");
        Debug.Log($"[OsmDebugger] Origin           : {_streamer.OriginLatitude}, {_streamer.OriginLongitude}");
        Debug.Log($"[OsmDebugger] Chunk size        : {_streamer.ChunkSize}");

        // --- SRTM ---
        Debug.Log("--- [OsmDebugger] SRTM Heightmap ---");
        if (SRTMHeightmap.Instance == null)
            Debug.LogError("[OsmDebugger] SRTMHeightmap.Instance is NULL — add SRTMHeightmap component");
        else if (!SRTMHeightmap.Instance.IsLoaded)
            Debug.LogError("[OsmDebugger] SRTMHeightmap NOT loaded — check StreamingAssets/SRTM/ files exist");
        else
        {
            Debug.Log($"[OsmDebugger] SRTM loaded ✓ — " +
                      $"Elevation: {SRTMHeightmap.Instance.MinElevation:F0}m " +
                      $"to {SRTMHeightmap.Instance.MaxElevation:F0}m | " +
                      $"Range: {SRTMHeightmap.Instance.ElevRange:F0}m");

            Vector3 pos  = _streamer.PlayerTransform?.position ?? Vector3.zero;
            float   elev = SRTMHeightmap.Instance.GetElevation(pos.x, pos.z);
            Debug.Log($"[OsmDebugger] SRTM elevation at player XZ ({pos.x:F0},{pos.z:F0}): {elev:F1}m");
        }

        // --- Splatmap ---
        Debug.Log("--- [OsmDebugger] Splatmap ---");
        if (SplatmapLoader.Instance == null)
        {
            Debug.LogWarning("[OsmDebugger] SplatmapLoader.Instance is NULL — add SplatmapLoader component");
        }
        else
        {
            string splatDir = Path.Combine(Application.streamingAssetsPath, "Splatmaps");
            if (!Directory.Exists(splatDir))
                Debug.LogWarning($"[OsmDebugger] Splatmaps folder missing: {splatDir}\n" +
                                 "Run worldcover_splatmap.py and copy to StreamingAssets/Splatmaps/");
            else
                Debug.Log($"[OsmDebugger] Splatmaps ✓ — " +
                          $"{Directory.GetFiles(splatDir, "*.png").Length} PNG files found");
        }

        // --- OSM Tiles ---
        Debug.Log("--- [OsmDebugger] OSM Tiles ---");
        string tilesPath = ResolveTilesPath(_streamer.OsmTilesFolder);
        if (!Directory.Exists(tilesPath))
        {
            Debug.LogError($"[OsmDebugger] OSM tiles folder NOT FOUND: {tilesPath}");
            yield break;
        }

        string[] tileFiles = Directory.GetFiles(tilesPath, "*.pbf");
        Debug.Log($"[OsmDebugger] Tiles ✓ — {tileFiles.Length} .pbf files");

        if (_streamer.PlayerTransform != null)
        {
            Vector3    pos   = _streamer.PlayerTransform.position;
            Vector2Int chunk = ChunkBounds.WorldToGrid(
                new Vector2(pos.x, pos.z), _streamer.ChunkSize);

            Debug.Log($"[OsmDebugger] Player chunk: ({chunk.x}, {chunk.y})");

            string tileFile = Path.Combine(tilesPath, $"tile_{chunk.x}_{chunk.y}.pbf");
            if (File.Exists(tileFile))
                Debug.Log($"[OsmDebugger] Player tile EXISTS ✓: tile_{chunk.x}_{chunk.y}.pbf");
            else
            {
                Debug.LogWarning($"[OsmDebugger] Player tile MISSING: tile_{chunk.x}_{chunk.y}.pbf");
                SuggestNearestTile(tileFiles, chunk);
            }
        }

        if (LogTileContents)
            yield return StartCoroutine(SampleTile(tilesPath));
    }

    // -----------------------------------------------------------------------
    // Tile Sampling
    // -----------------------------------------------------------------------

    private IEnumerator SampleTile(string tilesPath)
    {
        yield return new WaitForSeconds(0.5f);

        string[] files  = Directory.GetFiles(tilesPath, "*.pbf");
        if (files.Length == 0) yield break;

        string sample = FindPlayerTile(tilesPath) ?? files[0];
        Debug.Log($"--- [OsmDebugger] Tile contents: {Path.GetFileName(sample)} ---");

        List<OsmSharp.OsmGeo> elements = null;
        bool                  done     = false;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                elements = new List<OsmSharp.OsmGeo>();
                using var stream = File.OpenRead(sample);
                var src = new OsmSharp.Streams.PBFOsmStreamSource(stream);
                foreach (var el in src) elements.Add(el);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[OsmDebugger] Tile read failed: {e.Message}");
            }
            done = true;
        });

        yield return new WaitUntil(() => done);

        if (elements == null || elements.Count == 0)
        {
            Debug.LogWarning("[OsmDebugger] Tile empty or failed to read");
            yield break;
        }

        int nodes = 0, ways = 0;
        foreach (var el in elements)
        {
            if      (el is OsmSharp.Node) nodes++;
            else if (el is OsmSharp.Way)  ways++;
        }
        Debug.Log($"[OsmDebugger] Raw — Nodes: {nodes}  Ways: {ways}");

        TileData td = OsmParser.Parse(elements);
        Debug.Log($"[OsmDebugger] Parsed — " +
                  $"Roads: {td.Roads.Count} | " +
                  $"Landmass: {td.Landmass.Count} | " +
                  $"Water: {td.Water.Count} | " +
                  $"Buildings: {td.Buildings.Count} | " +
                  $"Unknown: {td.Unknown.Count}");

        // Highway type breakdown
        if (td.Roads.Count > 0)
        {
            var hwTypes = new Dictionary<string, int>();
            foreach (var r in td.Roads)
                if (r.Tags.TryGetValue("highway", out string hw))
                    hwTypes[hw] = hwTypes.GetValueOrDefault(hw, 0) + 1;

            var parts = new List<string>();
            foreach (var kv in hwTypes) parts.Add($"{kv.Key}×{kv.Value}");
            Debug.Log($"[OsmDebugger] Road types: {string.Join(", ", parts)}");
        }
        else
        {
            Debug.LogWarning("[OsmDebugger] Zero roads in tile — " +
                             "check OsmParser highway classification or tile origin/chunk size");
        }

        // Unknown tag inspection
        if (td.Unknown.Count > 0)
        {
            var unknownKeys = new HashSet<string>();
            foreach (var w in td.Unknown)
                foreach (var kv in w.Tags)
                    if (unknownKeys.Count < 20) unknownKeys.Add(kv.Key);
            Debug.Log($"[OsmDebugger] Unknown way tag keys: {string.Join(", ", unknownKeys)}");
        }

        // --- TerrainMesher test ---
        if (TestTerrainMesher)
        {
            if (SRTMHeightmap.Instance == null || !SRTMHeightmap.Instance.IsLoaded)
            {
                Debug.LogWarning("[OsmDebugger] Skipping TerrainMesher test — SRTM not loaded");
            }
            else
            {
                Vector3     pos    = _streamer.PlayerTransform?.position ?? Vector3.zero;
                Vector2Int  coord  = ChunkBounds.WorldToGrid(
                    new Vector2(pos.x, pos.z), _streamer.ChunkSize);
                ChunkBounds bounds = ChunkBounds.FromGrid(coord.x, coord.y, _streamer.ChunkSize);
                float       dist   = Vector3.Distance(pos,
                    new Vector3(bounds.WorldCenter.x, 0f, bounds.WorldCenter.y));

                Debug.Log($"[OsmDebugger] Testing TerrainMesher chunk ({coord.x},{coord.y})...");

                MeshData terrainData = null;
                bool     terrainDone = false;
                System.Threading.Tasks.Task.Run(() =>
                {
                    terrainData = TerrainMesher.Build(bounds, 0, dist);
                    terrainDone = true;
                });
                yield return new WaitUntil(() => terrainDone);

                if (terrainData == null)
                    Debug.LogWarning("[OsmDebugger] TerrainMesher.Build() returned NULL — " +
                                     "chunk may be outside SRTM coverage area");
                else
                    Debug.Log($"[OsmDebugger] TerrainMesher ✓ — " +
                              $"{terrainData.Vertices.Length} verts | " +
                              $"{terrainData.Triangles.Length / 3} tris | " +
                              $"{GetYRange(terrainData.Vertices)}");
            }
        }

        // --- RoadMesher test ---
        if (TestRoadMesher)
        {
            if (td.Roads.Count == 0)
            {
                Debug.LogWarning("[OsmDebugger] Skipping RoadMesher test — no roads in tile");
            }
            else
            {
                Vector3     pos    = _streamer.PlayerTransform?.position ?? Vector3.zero;
                Vector2Int  coord  = ChunkBounds.WorldToGrid(
                    new Vector2(pos.x, pos.z), _streamer.ChunkSize);
                ChunkBounds bounds = ChunkBounds.FromGrid(coord.x, coord.y, _streamer.ChunkSize);

                Debug.Log($"[OsmDebugger] Testing RoadMesher chunk ({coord.x},{coord.y}) " +
                          $"with {td.Roads.Count} road ways...");

                List<RoadMeshData> roadData = null;
                bool               roadDone = false;
                System.Threading.Tasks.Task.Run(() =>
                {
                    roadData = RoadMesher.Build(td.Roads, bounds, 0);
                    roadDone = true;
                });
                yield return new WaitUntil(() => roadDone);

                if (roadData == null || roadData.Count == 0)
                    Debug.LogWarning("[OsmDebugger] RoadMesher returned no meshes — " +
                                     "roads may be outside chunk bounds after clipping");
                else
                {
                    int totalVerts = 0;
                    foreach (var rd in roadData)
                        if (rd.MeshData != null) totalVerts += rd.MeshData.Vertices.Length;
                    Debug.Log($"[OsmDebugger] RoadMesher ✓ — " +
                              $"{roadData.Count} roads | {totalVerts} total verts");
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Periodic Report
    // -----------------------------------------------------------------------

    private void PrintReport()
    {
        Transform landRoot = _streamer.transform.Find("LandChunks");
        Transform roadRoot = _streamer.transform.Find("RoadChunks");

        int landTotal  = landRoot != null ? landRoot.childCount : 0;
        int roadTotal  = roadRoot != null ? roadRoot.childCount : 0;
        int landMeshed = 0;
        int landEmpty  = 0;

        if (landRoot != null)
        {
            for (int i = 0; i < landRoot.childCount; i++)
            {
                var mf = landRoot.GetChild(i).GetComponentInChildren<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 0)
                    landMeshed++;
                else
                    landEmpty++;
            }
        }

        Debug.Log($"[OsmDebugger] t={Time.time:F0}s | " +
                  $"Land: {landTotal} chunks ({landMeshed} meshed, {landEmpty} empty) | " +
                  $"Roads: {roadTotal} chunks | " +
                  $"Pending: {_streamer.PendingChunks}");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string GetYRange(Vector3[] verts)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in verts)
        {
            if (v.y < min) min = v.y;
            if (v.y > max) max = v.y;
        }
        return $"Y[{min:F1}m → {max:F1}m]";
    }

    private void SuggestNearestTile(string[] files, Vector2Int playerChunk)
    {
        Vector2Int best = Vector2Int.zero;
        int        bestDist = int.MaxValue;

        foreach (string f in files)
        {
            string[] p = Path.GetFileNameWithoutExtension(f).Split('_');
            if (p.Length != 3) continue;
            if (!int.TryParse(p[1], out int tx) || !int.TryParse(p[2], out int ty)) continue;
            int d = Mathf.Abs(tx - playerChunk.x) + Mathf.Abs(ty - playerChunk.y);
            if (d < bestDist) { bestDist = d; best = new Vector2Int(tx, ty); }
        }

        Debug.LogWarning($"[OsmDebugger] Nearest tile: tile_{best.x}_{best.y} " +
                         $"({bestDist} chunks away)" +
                         (bestDist > 5
                             ? " — large distance suggests origin/chunk size mismatch"
                             : " — likely an empty chunk near player"));
    }

    private string FindPlayerTile(string tilesPath)
    {
        if (_streamer.PlayerTransform == null) return null;
        Vector3    pos   = _streamer.PlayerTransform.position;
        Vector2Int chunk = ChunkBounds.WorldToGrid(
            new Vector2(pos.x, pos.z), _streamer.ChunkSize);
        string path = Path.Combine(tilesPath, $"tile_{chunk.x}_{chunk.y}.pbf");
        return File.Exists(path) ? path : null;
    }

    private string ResolveTilesPath(string folder)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return System.IO.Path.Combine(Application.streamingAssetsPath, "OsmTiles");
#else
        return folder;
#endif
    }
}
