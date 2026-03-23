using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OsmSharp;
using OsmSharp.Streams;
using UnityEngine;

/// <summary>
/// Loads individual .pbf tile files by chunk coordinate on a background thread.
/// Uses OsmSharp's PBFOsmStreamSource — requires protobuf-net v2.4.6 in Plugins.
/// Tiles are named tile_X_Y.pbf and located in a configurable folder.
/// Each chunk requests its own tile — nothing is loaded upfront.
/// </summary>
public class OsmLoader
{
    public string TilesFolder { get; private set; }

    public OsmLoader(string tilesFolder)
    {
        TilesFolder = tilesFolder;
    }

    /// <summary>
    /// Loads the .pbf tile for a given chunk coordinate asynchronously.
    /// Returns an empty list if the tile file does not exist.
    /// Safe to call from a background thread via Task.Run().
    /// </summary>
    public async Task<List<OsmGeo>> LoadTileAsync(
        Vector2Int chunkCoord,
        CancellationToken cancellationToken = default)
    {
        string path = GetTilePath(chunkCoord);

        if (!File.Exists(path))
            return new List<OsmGeo>();

        try
        {
            return await Task.Run(() => ReadPBF(path, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new List<OsmGeo>();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[OsmLoader] Failed to read tile {chunkCoord}: {e.Message}");
            return new List<OsmGeo>();
        }
    }

    /// <summary>
    /// Synchronous version — use only when already on a background thread.
    /// </summary>
    public List<OsmGeo> LoadTile(
        Vector2Int chunkCoord,
        CancellationToken cancellationToken = default)
    {
        string path = GetTilePath(chunkCoord);

        if (!File.Exists(path))
            return new List<OsmGeo>();

        try
        {
            return ReadPBF(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new List<OsmGeo>();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[OsmLoader] Failed to read tile {chunkCoord}: {e.Message}");
            return new List<OsmGeo>();
        }
    }

    /// <summary>
    /// Editor-friendly synchronous load. Alias for LoadTile().
    /// Use in editor tools where async/await is not available.
    /// </summary>
    public List<OsmGeo> LoadTileSync(Vector2Int chunkCoord)
        => LoadTile(chunkCoord);

    /// <summary>
    /// Returns true if a tile file exists for the given chunk coordinate.
    /// </summary>
    public bool TileExists(Vector2Int chunkCoord)
    {
        return File.Exists(GetTilePath(chunkCoord));
    }

    /// <summary>
    /// Returns the full file path for a tile at the given chunk coordinate.
    /// </summary>
    public string GetTilePath(Vector2Int chunkCoord)
    {
        return Path.Combine(TilesFolder, $"tile_{chunkCoord.x}_{chunkCoord.y}.pbf");
    }

    // --- Private ---

    private static List<OsmGeo> ReadPBF(string path, CancellationToken token)
    {
        var results = new List<OsmGeo>();

        using (var stream = File.OpenRead(path))
        {
            var source = new PBFOsmStreamSource(stream);
            foreach (OsmGeo element in source)
            {
                token.ThrowIfCancellationRequested();
                results.Add(element);
            }
        }

        return results;
    }
}
