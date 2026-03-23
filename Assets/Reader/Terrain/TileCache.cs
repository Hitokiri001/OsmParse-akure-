using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OsmSharp;
using UnityEngine;

/// <summary>
/// Thread-safe tile data cache shared between LandLoader and RoadLoader.
/// Each PBF file is read from disk and parsed exactly once.
///
/// Uses ConcurrentDictionary + Lazy<Task> pattern — no semaphores,
/// no locks on the hot path, no deadlock risk.
///
/// How it works:
///   First request for a coord creates a Lazy<Task<TileData>> and stores it.
///   All subsequent requests for the same coord get the same Lazy and await
///   the same Task — so loading only happens once regardless of how many
///   callers are waiting.
/// </summary>
public class TileCache
{
    public static readonly TileCache Instance = new TileCache();

    public int MaxEntries { get; set; } = 200;

    // Stores one loading task per coord — Lazy ensures it's only created once
    private readonly ConcurrentDictionary<Vector2Int, Lazy<Task<TileData>>> _tasks
        = new ConcurrentDictionary<Vector2Int, Lazy<Task<TileData>>>();

    // Simple insertion-order eviction list — protected by its own lock
    private readonly LinkedList<Vector2Int> _insertOrder   = new LinkedList<Vector2Int>();
    private readonly object                 _evictionLock  = new object();

    private TileCache() { }

    /// <summary>
    /// Returns TileData for a coord. Thread-safe. The first caller triggers
    /// a disk read; all subsequent callers for the same coord await the same
    /// Task and get the result for free when it completes.
    /// </summary>
    public Task<TileData> GetAsync(
        Vector2Int coord,
        OsmLoader  osmLoader,
        CancellationToken token = default)
    {
        var lazy = _tasks.GetOrAdd(coord,
            c => new Lazy<Task<TileData>>(
                () => LoadAsync(c, osmLoader),
                LazyThreadSafetyMode.ExecutionAndPublication));

        // Track insertion order for eviction
        lock (_evictionLock)
        {
            if (!_insertOrder.Contains(coord))
            {
                _insertOrder.AddLast(coord);

                // Evict oldest if over capacity
                while (_insertOrder.Count > MaxEntries)
                {
                    var oldest = _insertOrder.First.Value;
                    _insertOrder.RemoveFirst();
                    _tasks.TryRemove(oldest, out _);
                }
            }
        }

        return lazy.Value;
    }

    /// <summary>
    /// Removes a coord from the cache — call when unloading a chunk.
    /// </summary>
    public void Evict(Vector2Int coord)
    {
        _tasks.TryRemove(coord, out _);
        lock (_evictionLock)
            _insertOrder.Remove(coord);
    }

    public void Clear()
    {
        _tasks.Clear();
        lock (_evictionLock)
            _insertOrder.Clear();
    }

    // -----------------------------------------------------------------------
    // Private
    // -----------------------------------------------------------------------

    private static async Task<TileData> LoadAsync(Vector2Int coord, OsmLoader osmLoader)
    {
        try
        {
            List<OsmGeo> elements = await osmLoader.LoadTileAsync(coord);
            if (elements != null && elements.Count > 0)
                return OsmParser.Parse(elements);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[TileCache] Failed to load tile {coord}: {e.Message}");
        }
        return new TileData();
    }
}
