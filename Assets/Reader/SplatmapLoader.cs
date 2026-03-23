using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Loads per-chunk WorldCover splatmap textures and blends chunk borders
/// so land cover transitions smoothly across chunk boundaries.
///
/// How it works:
///   Raw pixels for every requested chunk AND its four neighbours are loaded
///   from disk immediately. When a chunk's splatmap is built, all four
///   neighbours are available. When a new chunk loads it also invalidates
///   the cached blended textures of its four neighbours so they get reblended
///   on next access with the newly available pixel data.
///
/// Each splatmap is 33x33 RGBA:
///   R = Grassland/Cropland  G = Forest/Shrubland
///   B = Urban/Bare          A = Water
/// </summary>
public class SplatmapLoader : MonoBehaviour
{
    public static SplatmapLoader Instance { get; private set; }

    [Header("Settings")]
    public string SplatmapFolder    = "Splatmaps";
    public int    MaxCachedTextures = 200;

    [Tooltip("Blend zone width in pixels at each chunk edge. " +
             "4-5 works well for 33x33 splatmaps (~120m blend zone).")]
    [Range(1, 8)]
    public int BlendBorderSize = 4;

    private string _splatDir;
    private int    _splatSize = 33;

    // Raw pixel arrays read from disk — kept permanently for neighbour blending
    private readonly Dictionary<Vector2Int, Color[]>   _rawPixels = new();

    // Final blended GPU textures
    private readonly Dictionary<Vector2Int, Texture2D> _cache     = new();
    private readonly LinkedList<Vector2Int>             _lruOrder  = new();

    private Texture2D _fallback;

    private static readonly Vector2Int[] _neighbours =
    {
        new Vector2Int( 0,  1), // north
        new Vector2Int( 0, -1), // south
        new Vector2Int( 1,  0), // east
        new Vector2Int(-1,  0), // west
    };

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        _splatDir = Path.Combine(Application.streamingAssetsPath, SplatmapFolder);

        _fallback = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        _fallback.SetPixels(new[]
        {
            new Color(0.5f,0,0,0), new Color(0.5f,0,0,0),
            new Color(0.5f,0,0,0), new Color(0.5f,0,0,0)
        });
        _fallback.Apply();
        _fallback.wrapMode   = TextureWrapMode.Clamp;
        _fallback.filterMode = FilterMode.Bilinear;

        if (!Directory.Exists(_splatDir))
            Debug.LogWarning($"[SplatmapLoader] Folder not found: {_splatDir}");
        else
            Debug.Log($"[SplatmapLoader] Ready — " +
                      $"{Directory.GetFiles(_splatDir, "*.png").Length} splatmaps in {_splatDir}");
    }

    /// <summary>
    /// Returns a blended splatmap for the chunk. Thread-safe to call from main thread.
    /// Loads this chunk's raw pixels, pre-fetches all four neighbours from disk,
    /// then invalidates any neighbour cached textures so they reblend on next access.
    /// </summary>
    public Texture2D GetSplatmap(Vector2Int coord)
    {
        // Load raw pixels for this chunk if not already loaded
        bool isNew = !_rawPixels.ContainsKey(coord);
        LoadRawIfNeeded(coord);

        if (!_rawPixels.ContainsKey(coord)) return _fallback; // file missing

        // Pre-fetch all four neighbours from disk so they're available for blending
        foreach (var offset in _neighbours)
            LoadRawIfNeeded(coord + offset);

        // If this is a new raw pixel load, invalidate neighbours' cached textures
        // so they get reblended with this chunk's data on their next access
        if (isNew)
        {
            foreach (var offset in _neighbours)
                InvalidateBlended(coord + offset);
        }

        // Return cached blended texture if still valid
        if (_cache.TryGetValue(coord, out Texture2D cached))
        {
            TouchLRU(coord);
            return cached;
        }

        // Build blended texture
        return BuildBlended(coord);
    }

    public void Release(Vector2Int coord)
    {
        InvalidateBlended(coord);
        // Keep raw pixels — neighbours may still need them for blending
    }

    private void OnDestroy()
    {
        foreach (var tex in _cache.Values)
            if (tex != null) Destroy(tex);
        _cache.Clear();
    }

    // -----------------------------------------------------------------------
    // Build blended texture
    // -----------------------------------------------------------------------

    private Texture2D BuildBlended(Vector2Int coord)
    {
        Color[] center = _rawPixels[coord];
        int     size   = _splatSize;

        _rawPixels.TryGetValue(coord + new Vector2Int( 0,  1), out Color[] north);
        _rawPixels.TryGetValue(coord + new Vector2Int( 0, -1), out Color[] south);
        _rawPixels.TryGetValue(coord + new Vector2Int( 1,  0), out Color[] east);
        _rawPixels.TryGetValue(coord + new Vector2Int(-1,  0), out Color[] west);

        Color[] blended = (Color[])center.Clone();

        int border = Mathf.Clamp(BlendBorderSize, 1, size / 2 - 1);

        for (int b = 0; b < border; b++)
        {
            // t=0 at the outermost pixel → full neighbour value
            // t=1 at the inner edge of blend zone → full own value
            float t             = (float)b / border;
            float smooth        = t * t * (3f - 2f * t); // smoothstep
            float neighbourW    = 1f - smooth;            // strong at edge, zero inside

            for (int i = 0; i < size; i++)
            {
                // North edge — row (size-1-b) blends toward north's row b
                if (north != null)
                {
                    int mine  = Idx(i, size - 1 - b, size);
                    int theirs = Idx(i, b, size);
                    blended[mine] = Color.Lerp(blended[mine], north[theirs], neighbourW);
                }

                // South edge — row b blends toward south's row (size-1-b)
                if (south != null)
                {
                    int mine   = Idx(i, b, size);
                    int theirs = Idx(i, size - 1 - b, size);
                    blended[mine] = Color.Lerp(blended[mine], south[theirs], neighbourW);
                }

                // East edge — column (size-1-b) blends toward east's column b
                if (east != null)
                {
                    int mine   = Idx(size - 1 - b, i, size);
                    int theirs = Idx(b, i, size);
                    blended[mine] = Color.Lerp(blended[mine], east[theirs], neighbourW);
                }

                // West edge — column b blends toward west's column (size-1-b)
                if (west != null)
                {
                    int mine   = Idx(b, i, size);
                    int theirs = Idx(size - 1 - b, i, size);
                    blended[mine] = Color.Lerp(blended[mine], west[theirs], neighbourW);
                }
            }
        }

        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.SetPixels(blended);
        tex.wrapMode   = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.Apply();

        StoreInCache(coord, tex);
        return tex;
    }

    // col + row * size — Unity stores pixels left-to-right, bottom-to-top
    private static int Idx(int col, int row, int size) => row * size + col;

    // -----------------------------------------------------------------------
    // Raw pixel loading
    // -----------------------------------------------------------------------

    private void LoadRawIfNeeded(Vector2Int coord)
    {
        if (_rawPixels.ContainsKey(coord)) return;

        string path = Path.Combine(_splatDir, $"splatmap_{coord.x}_{coord.y}.png");
        if (!File.Exists(path)) return;

        byte[]    bytes = File.ReadAllBytes(path);
        Texture2D tmp   = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tmp.LoadImage(bytes);
        _splatSize        = tmp.width;
        _rawPixels[coord] = tmp.GetPixels();
        Destroy(tmp);
    }

    // -----------------------------------------------------------------------
    // Cache management
    // -----------------------------------------------------------------------

    private void InvalidateBlended(Vector2Int coord)
    {
        if (_cache.TryGetValue(coord, out Texture2D tex))
        {
            Destroy(tex);
            _cache.Remove(coord);
            _lruOrder.Remove(coord);
        }
    }

    private void StoreInCache(Vector2Int coord, Texture2D tex)
    {
        _cache[coord] = tex;
        _lruOrder.AddFirst(coord);

        while (_cache.Count > MaxCachedTextures && _lruOrder.Count > 0)
        {
            Vector2Int oldest = _lruOrder.Last.Value;
            _lruOrder.RemoveLast();
            if (_cache.TryGetValue(oldest, out Texture2D old))
            {
                Destroy(old);
                _cache.Remove(oldest);
            }
        }
    }

    private void TouchLRU(Vector2Int coord)
    {
        _lruOrder.Remove(coord);
        _lruOrder.AddFirst(coord);
    }
}
