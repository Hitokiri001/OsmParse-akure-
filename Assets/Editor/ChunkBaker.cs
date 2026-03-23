#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using OsmSharp;

/// <summary>
/// Editor tool — bakes Akure SRTM terrain + OSM roads into chunk prefabs.
/// Runs on background threads with a progress bar and cancel button so
/// the editor never freezes.
/// Mesh uploads are batched on the main thread
/// between frames so Unity stays responsive throughout.
///
/// Terrain Y scaling:
///   Base elevation is subtracted so terrain sits near Y=0.
///   ElevationScale compresses the height range — 0.1 makes a 388m range
///   appear as 38.8m which is more manageable for a game.
///
/// Open via: Tools → Akure → Bake Chunk Prefabs
/// </summary>
public class ChunkBaker : EditorWindow
{
    // --- Settings ---
    private double _originLat    = 7.275;
    private double _originLon    = 5.215;
    private float  _chunkSize    = 1000f;
    private string _tilesFolder  = "Assets/StreamingAssets/OsmTiles";
    private string _outputFolder = "Assets/ChunkPrefabs";
    private string _meshFolder   = "Assets/ChunkPrefabs/Meshes";
    private Material _landMaterial;
    private Material _roadMaterial;

    // Terrain Y controls
    private float _elevationScale   = 0.1f;
    private float _baseElevation    = 264f;
    private bool  _autoBaseFromSRTM = true;

    // Chunk range (Used for UI display mostly now)
    private int _minX = -5, _maxX = 5;
    private int _minY = -5, _maxY = 5;
    
    // Valid tiles mapping
    private List<Vector2Int> _validTiles = new List<Vector2Int>();
    private int _testBakeCount = 50;

    // Bake state
    private bool               _baking;
    private string             _status = "Ready.";
    private int                _done;
    private int                _total;
    private CancellationTokenSource _cts;
    private Vector2            _scroll;

    // Editor-local SRTM state
    private bool    _editorSRTMLoaded;
    private float   _editorMinElev;
    private float   _editorMaxElev;
    private float   _editorElevRange;

    // Temp scene object hosting SRTMHeightmap during baking
    private GameObject _tempSRTMObject;

    [MenuItem("Tools/Akure/Bake Chunk Prefabs")]
    public static void Open() => GetWindow<ChunkBaker>("Chunk Baker");

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.LabelField("Akure Chunk Prefab Baker", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        // Origin
        EditorGUILayout.LabelField("World Origin", EditorStyles.boldLabel);
        _originLat = EditorGUILayout.DoubleField("Origin Latitude",  _originLat);
        _originLon = EditorGUILayout.DoubleField("Origin Longitude", _originLon);
        _chunkSize = EditorGUILayout.FloatField ("Chunk Size (m)",   _chunkSize);
        EditorGUILayout.Space(4);

        // Paths
        EditorGUILayout.LabelField("Paths", EditorStyles.boldLabel);
        _tilesFolder  = EditorGUILayout.TextField("OSM Tiles Folder",  _tilesFolder);
        _outputFolder = EditorGUILayout.TextField("Prefab Output",     _outputFolder);
        _meshFolder   = EditorGUILayout.TextField("Mesh Assets",       _meshFolder);
        EditorGUILayout.Space(4);

        // Materials
        EditorGUILayout.LabelField("Materials", EditorStyles.boldLabel);
        _landMaterial = (Material)EditorGUILayout.ObjectField(
            "Land Material", _landMaterial, typeof(Material), false);
        _roadMaterial = (Material)EditorGUILayout.ObjectField(
            "Road Material", _roadMaterial, typeof(Material), false);
        EditorGUILayout.Space(4);

        // Terrain Y
        EditorGUILayout.LabelField("Terrain Height Settings", EditorStyles.boldLabel);
        _autoBaseFromSRTM = EditorGUILayout.Toggle(
            "Auto Base from SRTM", _autoBaseFromSRTM);

        GUI.enabled = !_autoBaseFromSRTM;
        _baseElevation = EditorGUILayout.FloatField(
            "Base Elevation (m)", _baseElevation);

        GUI.enabled = true;
        _elevationScale = EditorGUILayout.Slider(
            "Elevation Scale", _elevationScale, 0.01f, 1f);

        if (_editorSRTMLoaded)
        {
            float scaledRange = _editorElevRange * _elevationScale;
            EditorGUILayout.HelpBox(
                $"SRTM loaded ✓ — {_editorMinElev:F0}m to {_editorMaxElev:F0}m\n" +
                $"Raw range: {_editorElevRange:F0}m → Scaled: {scaledRange:F1}m " +
                $"(terrain Y=0 to Y={scaledRange:F1})",
                MessageType.Info);
            
            if (_autoBaseFromSRTM)
                _baseElevation = _editorMinElev;
        }
        else
        {
            EditorGUILayout.HelpBox(
                "SRTM not loaded. Files should be in Assets/StreamingAssets/SRTM/\n" +
                "Click Load SRTM — no Play mode needed.",
                MessageType.Warning);
        }

        if (GUILayout.Button(_editorSRTMLoaded ? "Reload SRTM" : "Load SRTM"))
            LoadSRTM();

        EditorGUILayout.Space(4);

        // Chunk range
        EditorGUILayout.LabelField("Chunk Range", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        _minX = EditorGUILayout.IntField("X Min", _minX);
        _maxX = EditorGUILayout.IntField("X Max", _maxX);
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        _minY = EditorGUILayout.IntField("Y Min", _minY);
        _maxY = EditorGUILayout.IntField("Y Max", _maxY);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox(
            $"Currently targeting {_validTiles.Count} valid chunks based on OSM data.\n" +
            $"Baking runs on background threads — editor stays responsive.",
            MessageType.Info);

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Auto-Detect Range from Tile Files"))
            AutoDetect();

        EditorGUILayout.Space(4);

        // Bake / Cancel
        bool srtmReady = _editorSRTMLoaded;

        if (!_baking)
        {
            bool canBake = _landMaterial != null && _roadMaterial != null && srtmReady;
            GUI.enabled = canBake;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Bake All Chunks", GUILayout.Height(36)))
                StartBake(_validTiles.Count);

            _testBakeCount = EditorGUILayout.IntField(_testBakeCount, GUILayout.Width(50), GUILayout.Height(36));

            if (GUILayout.Button($"Bake Test ({_testBakeCount})", GUILayout.Height(36)))
                StartBake(_testBakeCount);
            EditorGUILayout.EndHorizontal();

            GUI.enabled = true;
        }
        else
        {
            if (GUILayout.Button("Cancel Bake", GUILayout.Height(36)))
                CancelBake();
        }

        // Progress bar
        EditorGUILayout.Space(4);
        
        if (_total > 0)
        {
            float pct = (float)_done / _total;
            EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 22),
                pct, $"{_done} / {_total}  ({pct * 100f:F0}%)");
        }

        EditorGUILayout.HelpBox(_status, MessageType.None);
        EditorGUILayout.EndScrollView();
    }

    private void AutoDetect()
    {
        string full = FullPath(_tilesFolder);
        if (!Directory.Exists(full)) { _status = "Tiles folder not found"; return; }

        string[] files = Directory.GetFiles(full, "tile_*.pbf");
        if (files.Length == 0) { _status = "No .pbf files found"; return; }

        _validTiles.Clear();
        int x0 = int.MaxValue, x1 = int.MinValue;
        int y0 = int.MaxValue, y1 = int.MinValue;

        foreach (string f in files)
        {
            string[] p = Path.GetFileNameWithoutExtension(f).Split('_');
            if (p.Length != 3) continue;
            if (!int.TryParse(p[1], out int x) || !int.TryParse(p[2], out int y)) continue;

            _validTiles.Add(new Vector2Int(x, y));

            x0 = Mathf.Min(x0, x); x1 = Mathf.Max(x1, x);
            y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y);
        }

        _minX = x0; _maxX = x1; _minY = y0; _maxY = y1;
        _status = $"Detected {files.Length} valid tiles in range X[{x0},{x1}] Y[{y0},{y1}]";
        Repaint();
    }

    /// <summary>
    /// Reads SRTM files from Application.streamingAssetsPath/SRTM/ — 
    /// same path SRTMHeightmap uses at runtime.
    /// Works in editor without Play mode.
    /// </summary>
    private void LoadSRTM()
    {
        try
        {
            string dir  = Path.Combine(Application.streamingAssetsPath, "SRTM");
            string meta = Path.Combine(dir, "akure_srtm_meta.json");
            string r0   = Path.Combine(dir, "akure_srtm_lod0.raw");
            string r1   = Path.Combine(dir, "akure_srtm_lod1.raw");
            string r2   = Path.Combine(dir, "akure_srtm_lod2.raw");

            foreach (var (p, n) in new[]{(meta,"meta.json"),(r0,"lod0.raw"),(r1,"lod1.raw"),(r2,"lod2.raw")})
            {
                if (!File.Exists(p))
                {
                    _status = $"Missing: {n} — expected in {dir}";
                    Debug.LogError($"[ChunkBaker] Not found: {p}");
                    return;
                }
            }

            string metaJson = File.ReadAllText(meta);
            var    parsed   = JsonUtility.FromJson<SRTMMetadata>(metaJson);
            
            _editorMinElev   = (float)parsed.min_elev;
            _editorMaxElev   = (float)parsed.max_elev;
            _editorElevRange = (float)parsed.elev_range;

            if (_tempSRTMObject != null) DestroyImmediate(_tempSRTMObject);
            _tempSRTMObject = new GameObject("__SRTMBaker__") { hideFlags = HideFlags.HideAndDontSave };
            
            var srtm = _tempSRTMObject.AddComponent<SRTMHeightmap>();
            srtm.LoadFromBytes(metaJson,
                File.ReadAllBytes(r0),
                File.ReadAllBytes(r1),
                File.ReadAllBytes(r2));
                
            if (!srtm.IsLoaded)
            {
                DestroyImmediate(_tempSRTMObject);
                _status = "LoadFromBytes failed — check console";
                return;
            }

            _editorSRTMLoaded = true;
            if (_autoBaseFromSRTM) _baseElevation = _editorMinElev;
            
            _status = $"SRTM loaded ✓ — {_editorMinElev:F0}m to {_editorMaxElev:F0}m";
            Repaint();
        }
        catch (System.Exception e)
        {
            _status = $"SRTM load failed: {e.Message}";
            Debug.LogError($"[ChunkBaker] {e}");
        }
    }

    private void StartBake(int chunkLimit)
    {
        if (_validTiles.Count == 0) AutoDetect();
        if (_validTiles.Count == 0) return;

        if (_autoBaseFromSRTM)
        {
            if (_editorSRTMLoaded)
                _baseElevation = _editorMinElev;
            else if (SRTMHeightmap.Instance?.IsLoaded ?? false)
                _baseElevation = SRTMHeightmap.Instance.MinElevation;
        }

        Mercator.SetOrigin(_originLat, _originLon);
        EnsureFolders();

        _done   = 0;
        _total  = Mathf.Min(chunkLimit, _validTiles.Count);
        _baking = true;
        _status = _total < _validTiles.Count
            ? $"Test bake — {_total} of {_validTiles.Count} chunks..."
            : "Baking all chunks...";

        _cts = new CancellationTokenSource();
        EditorApplication.update += BakeStep;
        _bakeEnumerator           = BakeCoroutine(_cts.Token, _total);
    }

    private void CancelBake()
    {
        _cts?.Cancel();
        _baking = false;
        _status = "Cancelled.";
        EditorApplication.update -= BakeStep;
        EditorUtility.ClearProgressBar();
        if (_tempSRTMObject != null) DestroyImmediate(_tempSRTMObject);
        Repaint();
    }

    private IEnumerator _bakeEnumerator;

    private void BakeStep()
    {
        if (_bakeEnumerator == null || !_bakeEnumerator.MoveNext())
        {
            EditorApplication.update -= BakeStep;
            _baking = false;
            
            if (!(_cts?.IsCancellationRequested ?? true))
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                _status = $"Done — {_done} chunks baked to {_outputFolder}";
            }
            
            EditorUtility.ClearProgressBar();
            if (_tempSRTMObject != null) DestroyImmediate(_tempSRTMObject);
            Repaint();
        }
    }

    private IEnumerator BakeCoroutine(CancellationToken token, int limit)
    {
        string tilesFullPath = FullPath(_tilesFolder);
        var    osmLoader     = new OsmLoader(tilesFullPath);
        
        float  baseElev      = _baseElevation;
        float  scale         = _elevationScale;

        // Iterate only up to limit — allows test bakes of N chunks
        int count = Mathf.Min(limit, _validTiles.Count);
        for (int i = 0; i < count; i++)
        {
            if (token.IsCancellationRequested) yield break;

            var coord = _validTiles[i];

            _status = $"Baking chunk ({coord.x},{coord.y})...";
            EditorUtility.DisplayProgressBar(
                "Baking Chunk Prefabs", _status, (float)_done / _total);
            Repaint();

            // Bake on background thread — returns mesh data, NOT Unity objects
            ChunkBakeData bakeData = null;
            bool          done     = false;

            Task.Run(() =>
            {
                bakeData = BakeChunkData(coord, osmLoader, baseElev, scale, token);
                done     = true;
            }, token);

            // Yield until background thread finishes
            // Editor stays responsive — other editor operations still work
            while (!done) yield return null;
            if (token.IsCancellationRequested) yield break;

            // Upload meshes and create prefab on main thread
            if (bakeData != null)
                CommitChunk(bakeData, coord);
                
            _done++;
            yield return null; // breathe between chunks
        }
    }

    // -----------------------------------------------------------------------
    // Background thread — builds raw mesh data only (no Unity API calls)
    // -----------------------------------------------------------------------

    private ChunkBakeData BakeChunkData(
        Vector2Int coord, OsmLoader osmLoader,
        float baseElev, float scale,
        CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;

        ChunkBounds bounds = ChunkBounds.FromGrid(coord.x, coord.y, _chunkSize);
        var data           = new ChunkBakeData { Coord = coord, Bounds = bounds };

        // Build 3 LOD terrain meshes
        for (int lod = 0; lod < 3; lod++)
        {
            if (token.IsCancellationRequested) return null;
            MeshData md = TerrainMesher.BuildScaled(bounds, lod, 0f, baseElev, scale);
            data.LodMeshData[lod] = md;
        }

        // Load OSM roads
        string tilePath = osmLoader.GetTilePath(coord);
        if (File.Exists(tilePath))
        {
            try
            {
                List<OsmGeo>    elements  = osmLoader.LoadTileSync(coord);
                TileData        tileData  = OsmParser.Parse(elements);
                data.RoadMeshDatas        =
                    RoadMesher.BuildScaled(tileData.Roads, bounds, 0, baseElev, scale);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ChunkBaker] Roads failed ({coord}): {e.Message}");
                data.RoadMeshDatas = new List<RoadMeshData>();
            }
        }
        else
        {
            data.RoadMeshDatas = new List<RoadMeshData>();
        }

        return data;
    }

    // -----------------------------------------------------------------------
    // Main thread — uploads meshes to GPU and saves prefab
    // -----------------------------------------------------------------------

    private void CommitChunk(ChunkBakeData data, Vector2Int coord)
    {
        int  cx = coord.x, cy = coord.y;
        var  root       = new GameObject($"Chunk_{cx}_{cy}");
        var  lodGroup   = root.AddComponent<LODGroup>();
        var  lods       = new LOD[3];

        // Terrain LODs
        for (int lod = 0; lod < 3; lod++)
        {
            var lodGO = new GameObject($"LOD{lod}");
            lodGO.transform.SetParent(root.transform, false);

            if (data.LodMeshData[lod] != null)
            {
                Mesh mesh = UploadTerrain(data.LodMeshData[lod],
                                          $"Terrain_{cx}_{cy}_LOD{lod}");
                string meshPath = $"{_meshFolder}/Terrain_{cx}_{cy}_LOD{lod}.asset";
                AssetDatabase.CreateAsset(mesh, meshPath);

                var mf = lodGO.AddComponent<MeshFilter>();
                var mr = lodGO.AddComponent<MeshRenderer>();
                
                mf.sharedMesh     = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                mr.sharedMaterial = _landMaterial;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows    = true;

                if (lod == 0)
                {
                    var mc = lodGO.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                }

                lods[lod] = new LOD(LODScreenHeight(lod), new Renderer[] { mr });
            }
            else
            {
                lods[lod] = new LOD(LODScreenHeight(lod), new Renderer[0]);
            }
        }

        lodGroup.SetLODs(lods);
        lodGroup.RecalculateBounds();

        // Roads
        if (data.RoadMeshDatas != null && data.RoadMeshDatas.Count > 0)
        {
            var roadsGO = new GameObject("Roads");
            roadsGO.transform.SetParent(root.transform, false);

            for (int i = 0; i < data.RoadMeshDatas.Count; i++)
            {
                var rd = data.RoadMeshDatas[i];
                if (rd.MeshData == null) continue;

                Mesh roadMesh = RoadMesher.Upload(rd.MeshData);
                roadMesh.name = $"Road_{cx}_{cy}_{i}";
                string rPath  = $"{_meshFolder}/Road_{cx}_{cy}_{i}.asset";
                AssetDatabase.CreateAsset(roadMesh, rPath);
                
                var rGO = new GameObject($"Road_{rd.OsmId}");
                rGO.transform.SetParent(roadsGO.transform, false);

                var mf = rGO.AddComponent<MeshFilter>();
                var mr = rGO.AddComponent<MeshRenderer>();
                
                mf.sharedMesh     = AssetDatabase.LoadAssetAtPath<Mesh>(rPath);
                mr.sharedMaterial = _roadMaterial;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        // Save prefab
        string prefabPath = $"{_outputFolder}/Chunk_{cx}_{cy}.prefab";
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        DestroyImmediate(root);
    }

    private static Mesh UploadTerrain(MeshData data, string name)
    {
        var mesh = new Mesh();
        mesh.name        = name;
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(data.Vertices);
        mesh.SetTriangles(data.Triangles, 0);
        mesh.SetUVs(0, data.UVs);
        
        if (data.UV1s != null) mesh.SetUVs(1, data.UV1s);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.Optimize();
        return mesh;
    }

        private static float LODScreenHeight(int lod)
    {
        switch (lod)
        {
            case 0: return 0.15f;
            case 1: return 0.05f;
            case 2: return 0.01f;
            default: return 0f;
        }
    }

    private void EnsureFolders()
    {
        CreateAssetFolder(_outputFolder);
        CreateAssetFolder(_meshFolder);
        AssetDatabase.Refresh();
    }

    private static void CreateAssetFolder(string assetPath)
    {
        string[] parts = assetPath.Split('/');
        string   built = parts[0];
        
        for (int i = 1; i < parts.Length; i++)
        {
            string next = built + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(built, parts[i]);
                
            built = next;
        }
    }

    private static string FullPath(string assetPath)
        => Path.Combine(
            Application.dataPath.Replace("Assets", ""),
            assetPath.TrimStart('/').Replace("Assets/", "Assets" + Path.DirectorySeparatorChar));
}

// -----------------------------------------------------------------------
// Data container passed between background and main thread
// -----------------------------------------------------------------------

public class ChunkBakeData
{
    public Vector2Int         Coord;
    public ChunkBounds        Bounds;
    public MeshData[]         LodMeshData    = new MeshData[3];
    public List<RoadMeshData> RoadMeshDatas  = new List<RoadMeshData>();
}
#endif
