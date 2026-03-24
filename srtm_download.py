"""
SRTM Heightmap Downloader — Multi-LOD Version
===============================================
Downloads SRTM elevation data for Akure bbox from Open-Elevation API,
samples it into a grid, and bakes three LOD RAW heightmap files for Unity.

LOD 0 — 129x129 vertices — used within 1000m of player
LOD 1 — 65x65  vertices — used within 2000m of player
LOD 2 — 33x33  vertices — used beyond 2000m

Each LOD is a separate .raw file loaded at startup.
Unity TerrainMesher picks the right LOD per chunk per frame.

No GeoTIFF needed — uses Open-Elevation API directly (free, no account).
For the full heightmap grid, samples are spaced evenly across Akure bbox.

REQUIREMENTS:
    pip install requests numpy

HOW TO RUN:
    python srtm_download.py

OUTPUT (copy all to Assets/StreamingAssets/SRTM/):
    akure_srtm_lod0.raw   — 129x129 high detail
    akure_srtm_lod1.raw   — 65x65  medium detail
    akure_srtm_lod2.raw   — 33x33  low detail
    akure_srtm_meta.json  — bounds, elevation range, origin
"""

import os
import sys
import json
import math
import time
import struct
import requests
import numpy as np

# -----------------------------------------------------------------------
# CONFIGURATION
# -----------------------------------------------------------------------

MIN_LAT =  7.15
MAX_LAT =  7.40
MIN_LON =  5.08
MAX_LON =  5.35

OUTPUT_DIR = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\SRTM"

# Master grid resolution — all LODs are downsampled from this
# 257x257 gives good accuracy while keeping API calls manageable
MASTER_SIZE = 257

# LOD sizes — must be (2^n + 1) for clean subdivision
LOD_SIZES = [129, 65, 33]

# Open-Elevation API
ELEVATION_SERVERS = [
    "https://api.open-elevation.com/api/v1/lookup",
    "https://open.elevation.au/api/v1/lookup",
]

BATCH_SIZE  = 500   # points per API request
BATCH_DELAY = 0.3   # seconds between batches

# Fallback elevation if API fails (Akure is ~370m above sea level)
FALLBACK_ELEVATION = 370.0

# -----------------------------------------------------------------------

def sample_grid(size):
    """Generates a uniform lat/lon grid of (size x size) points over the bbox."""
    lats = np.linspace(MIN_LAT, MAX_LAT, size)
    lons = np.linspace(MIN_LON, MAX_LON, size)
    grid_lons, grid_lats = np.meshgrid(lons, lats)
    return grid_lats, grid_lons

def fetch_elevations(lats_flat, lons_flat):
    """
    Queries Open-Elevation for a flat array of lat/lon points.
    Returns numpy array of elevation values in same order.
    """
    total      = len(lats_flat)
    elevations = np.full(total, FALLBACK_ELEVATION, dtype=np.float32)
    fetched    = 0
    failed     = 0
    start      = time.time()

    print(f"  Querying {total:,} elevation points in batches of {BATCH_SIZE}...")

    for i in range(0, total, BATCH_SIZE):
        batch_lats = lats_flat[i:i + BATCH_SIZE]
        batch_lons = lons_flat[i:i + BATCH_SIZE]

        payload = {
            "locations": [
                {"latitude": float(lat), "longitude": float(lon)}
                for lat, lon in zip(batch_lats, batch_lons)
            ]
        }

        batch_result = None
        for server in ELEVATION_SERVERS:
            try:
                response = requests.post(
                    server,
                    json    = payload,
                    timeout = 30,
                    headers = {"Content-Type": "application/json"}
                )
                if response.status_code == 200:
                    data    = response.json()
                    results = data.get("results", [])
                    if len(results) == len(batch_lats):
                        batch_result = [r["elevation"] for r in results]
                        break
            except Exception:
                continue

        if batch_result:
            for j, elev in enumerate(batch_result):
                elevations[i + j] = elev
            fetched += len(batch_lats)
        else:
            failed += len(batch_lats)

        done    = min(i + BATCH_SIZE, total)
        elapsed = time.time() - start
        rate    = done / elapsed if elapsed > 0 else 1
        remain  = (total - done) / rate if rate > 0 else 0
        print(f"\r  {done:,}/{total:,} — {elapsed:.0f}s elapsed — ~{remain:.0f}s remaining — "
              f"failed: {failed:,}   ", end="", flush=True)

        if i + BATCH_SIZE < total:
            time.sleep(BATCH_DELAY)

    print()
    return elevations, fetched, failed

def downsample(grid, target_size):
    """Downsamples a 2D numpy grid to target_size x target_size using bilinear interpolation."""
    from scipy.ndimage import zoom
    factor = target_size / grid.shape[0]
    return zoom(grid, factor, order=1).astype(np.float32)

def downsample_simple(grid, target_size):
    """Simple downsampling without scipy — picks evenly spaced indices."""
    src_size = grid.shape[0]
    indices  = np.round(np.linspace(0, src_size - 1, target_size)).astype(int)
    return grid[np.ix_(indices, indices)].astype(np.float32)

def write_raw(grid, output_path, min_elev, max_elev):
    """
    Writes a Unity RAW heightmap — 16-bit big-endian.
    Normalizes elevation to 0-65535 range.
    Flips vertically since Unity RAW expects top-to-bottom.
    """
    elev_range = max_elev - min_elev
    if elev_range < 1.0:
        elev_range = 1.0

    normalized = (grid - min_elev) / elev_range
    normalized = np.clip(normalized, 0.0, 1.0)
    flipped    = np.flipud(normalized)
    uint16     = (flipped * 65535).astype(np.uint16)

    with open(output_path, "wb") as f:
        for row in uint16:
            for val in row:
                f.write(struct.pack(">H", int(val)))

    size_kb = os.path.getsize(output_path) / 1024
    print(f"  Written: {output_path} ({size_kb:.0f} KB)")

def main():
    print("=== SRTM Multi-LOD Heightmap Generator ===")
    print(f"Bbox   : Lat [{MIN_LAT}, {MAX_LAT}]  Lon [{MIN_LON}, {MAX_LON}]")
    print(f"Master : {MASTER_SIZE}x{MASTER_SIZE}")
    print(f"LODs   : {LOD_SIZES}")
    print(f"Output : {OUTPUT_DIR}")
    print()

    os.makedirs(OUTPUT_DIR, exist_ok=True)

    # --- Step 1: Sample master elevation grid ---
    cached_npy = os.path.join(OUTPUT_DIR, "akure_srtm_master.npy")

    if os.path.exists(cached_npy):
        print("Step 1/3 — Loading cached master grid...")
        master_grid = np.load(cached_npy)
        print(f"  Loaded: {master_grid.shape} grid")
    else:
        print(f"Step 1/3 — Sampling {MASTER_SIZE}x{MASTER_SIZE} elevation grid...")
        grid_lats, grid_lons = sample_grid(MASTER_SIZE)
        lats_flat = grid_lats.flatten()
        lons_flat = grid_lons.flatten()

        elevations_flat, fetched, failed = fetch_elevations(lats_flat, lons_flat)
        master_grid = elevations_flat.reshape(MASTER_SIZE, MASTER_SIZE)

        np.save(cached_npy, master_grid)
        print(f"  Cached to: {cached_npy}")

    min_elev = float(np.min(master_grid))
    max_elev = float(np.max(master_grid))
    avg_elev = float(np.mean(master_grid))

    print(f"\n  Elevation stats:")
    print(f"    Min : {min_elev:.1f}m")
    print(f"    Max : {max_elev:.1f}m")
    print(f"    Avg : {avg_elev:.1f}m")
    print(f"    Range: {max_elev - min_elev:.1f}m")

    # --- Step 2: Downsample to LOD grids ---
    print(f"\nStep 2/3 — Generating LOD grids...")

    lod_grids = []
    for lod_size in LOD_SIZES:
        try:
            from scipy.ndimage import zoom
            factor = lod_size / MASTER_SIZE
            grid   = zoom(master_grid, factor, order=1).astype(np.float32)
        except ImportError:
            grid = downsample_simple(master_grid, lod_size)

        lod_grids.append(grid)
        print(f"  LOD {LOD_SIZES.index(lod_size)} — {lod_size}x{lod_size} generated")

    # --- Step 3: Write RAW files and metadata ---
    print(f"\nStep 3/3 — Writing RAW files...")

    raw_paths = []
    for i, (lod_size, grid) in enumerate(zip(LOD_SIZES, lod_grids)):
        path = os.path.join(OUTPUT_DIR, f"akure_srtm_lod{i}.raw")
        write_raw(grid, path, min_elev, max_elev)
        raw_paths.append(path)

    # Compute world dimensions
    mpdLat = math.pi * 6378137.0 / 180.0
    latRad = math.radians((MIN_LAT + MAX_LAT) / 2)
    mpdLon = math.pi * 6378137.0 * math.cos(latRad) / 180.0

    terrain_width  = (MAX_LON - MIN_LON) * mpdLon
    terrain_length = (MAX_LAT - MIN_LAT) * mpdLat
    elev_range     = max_elev - min_elev

    metadata = {
        "lod_sizes":      LOD_SIZES,
        "min_elev":       min_elev,
        "max_elev":       max_elev,
        "elev_range":     elev_range,
        "base_elevation": min_elev,
        "min_lat":        MIN_LAT,
        "max_lat":        MAX_LAT,
        "min_lon":        MIN_LON,
        "max_lon":        MAX_LON,
        "origin_lat":     7.275,
        "origin_lon":     5.215,
        "terrain_width":  terrain_width,
        "terrain_length": terrain_length,
        "terrain_height": elev_range
    }

    meta_path = os.path.join(OUTPUT_DIR, "akure_srtm_meta.json")
    with open(meta_path, "w") as f:
        json.dump(metadata, f, indent=2)

    print(f"  Written: {meta_path}")

    print(f"\nDone!")
    print(f"  Terrain world size: {terrain_width:.0f}m x {terrain_length:.0f}m")
    print(f"  Elevation range   : {min_elev:.1f}m — {max_elev:.1f}m ({elev_range:.1f}m range)")
    print(f"\n  Set in OsmParser.cs:")
    print(f"    BaseElevation = {min_elev:.1f}f")
    print()
    print("  Copy these to Assets/StreamingAssets/SRTM/:")
    for p in raw_paths:
        print(f"    {os.path.basename(p)}")
    print(f"    akure_srtm_meta.json")

    input("\nPress Enter to exit.")

if __name__ == "__main__":
    main()
