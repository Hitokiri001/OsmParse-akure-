"""
WorldCover Splatmap Generator
==============================
Reads the ESA WorldCover GeoTIFF and generates one splatmap PNG per chunk
for use with the Terrain URP shader.

Each splatmap is a 33x33 RGBA PNG:
  R = Grassland / Cropland      → _GrassTexture  in shader
  G = Tree cover / Shrubland    → _ForestTexture in shader
  B = Built-up / Bare           → _UrbanTexture  in shader
  A = Water                     → _WaterTexture  in shader

Performance notes vs old version:
  - Reads the entire raster band ONCE into RAM as a numpy array
    (old version read it from disk 33x33 times per chunk = millions of reads)
  - Uses numpy vectorised indexing per chunk instead of Python loops
  - Only generates splatmaps for chunks that have a matching .pbf tile file
    (same logic as the updated ChunkBaker - no wasted work)
  - Skips chunks that already have a PNG on disk so reruns are fast

Requirements:
    pip install rasterio numpy pillow

ESA WorldCover land cover class values:
    10  = Tree cover
    20  = Shrubland
    30  = Grassland
    40  = Cropland
    50  = Built-up
    60  = Bare / sparse vegetation
    70  = Snow and ice
    80  = Permanent water bodies
    90  = Herbaceous wetland
    95  = Mangroves
    100 = Moss and lichen
"""

import os
import sys
import json
import math

import numpy as np

try:
    import rasterio
    from PIL import Image
except ImportError:
    print("Missing dependencies. Run:")
    print("  pip install rasterio numpy pillow")
    sys.exit(1)

# ---------------------------------------------------------------------------
# CONFIGURATION
# Must match ORIGIN_LAT/LON and CHUNK_SIZE in splitter.py and srtm_download.py
# ---------------------------------------------------------------------------

WORLDCOVER_TIF = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\WorldCover\ESA_WorldCover_N06E003.tif"
TILES_DIR      = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\Tiles"    # folder containing tile_X_Y.pbf files
OUTPUT_DIR     = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\Splatmaps"     # copy this whole folder to Assets/StreamingAssets/Splatmaps/

ORIGIN_LAT = 7.275
ORIGIN_LON = 5.215
CHUNK_SIZE = 1000.0   # meters — must match Unity WorldStreamer ChunkSize

# Resolution of each splatmap — 33x33 matches the LOD2 terrain mesh density
# so each texel aligns with one terrain vertex at maximum LOD distance
SPLAT_SIZE = 33

# ---------------------------------------------------------------------------
# WorldCover class → RGBA weight mapping
# Weights do not need to sum to 1 — the shader normalises them
# ---------------------------------------------------------------------------

# Lookup table indexed by WorldCover class value → (R, G, B, A) as uint8
# Built once at startup, used for all chunks
WC_CLASS_TO_RGBA = {
    10:  (  0, 255,   0,   0),  # Tree cover      → Forest
    20:  ( 51, 204,   0,   0),  # Shrubland       → Mostly forest
    30:  (255,   0,   0,   0),  # Grassland       → Open land
    40:  (204,  51,   0,   0),  # Cropland        → Mostly open
    50:  (  0,   0, 255,   0),  # Built-up        → Urban
    60:  ( 25,   0, 230,   0),  # Bare/sparse     → Mostly bare
    70:  (128,   0,   0,   0),  # Snow/ice        → Default open (no snow in Akure)
    80:  (  0,   0,   0, 255),  # Permanent water → Water
    90:  (128,   0,   0, 128),  # Wetland         → Water + open
    95:  (  0, 204,   0,  51),  # Mangroves       → Forest + water
    100: ( 76,   0,   0,   0),  # Moss/lichen     → Open
}
WC_DEFAULT_RGBA = (128, 0, 0, 0)  # Unknown / outside bounds → default open land

# ---------------------------------------------------------------------------
# Coordinate helpers
# ---------------------------------------------------------------------------

def meters_per_degree():
    """Returns (mpdLat, mpdLon) in meters per degree at Akure's latitude."""
    lat_mid = math.radians(ORIGIN_LAT)
    mpdLat  = math.pi * 6378137.0 / 180.0
    mpdLon  = math.pi * 6378137.0 * math.cos(lat_mid) / 180.0
    return mpdLat, mpdLon

def world_to_latlon(world_x, world_z, mpdLat, mpdLon):
    """Convert world XZ (meters from origin) to lat/lon."""
    lat = ORIGIN_LAT + world_z / mpdLat
    lon = ORIGIN_LON + world_x / mpdLon
    return lat, lon

def chunk_world_bounds(cx, cy):
    """Returns (wx_min, wz_min, wx_max, wz_max) in world space for a chunk."""
    return (
        cx       * CHUNK_SIZE,
        cy       * CHUNK_SIZE,
        (cx + 1) * CHUNK_SIZE,
        (cy + 1) * CHUNK_SIZE,
    )

# ---------------------------------------------------------------------------
# Tile discovery — only process chunks that have a .pbf file
# Same logic as the updated ChunkBaker
# ---------------------------------------------------------------------------

def discover_valid_tiles(tiles_dir):
    """
    Returns a list of (cx, cy) tuples for every tile_X_Y.pbf in tiles_dir.
    Only these coordinates get a splatmap generated.
    """
    if not os.path.isdir(tiles_dir):
        print(f"ERROR: Tiles folder not found: {tiles_dir}")
        print("Update TILES_DIR to point to your tile_X_Y.pbf folder")
        sys.exit(1)

    tiles = []
    for fname in os.listdir(tiles_dir):
        if not fname.endswith(".pbf"):
            continue
        parts = os.path.splitext(fname)[0].split("_")
        if len(parts) != 3:
            continue
        try:
            cx, cy = int(parts[1]), int(parts[2])
            tiles.append((cx, cy))
        except ValueError:
            continue

    tiles.sort()  # consistent ordering
    return tiles

# ---------------------------------------------------------------------------
# Splatmap generation — vectorised with numpy
# ---------------------------------------------------------------------------

def build_lookup_table():
    """
    Builds a uint8 numpy array of shape (256, 4) mapping WorldCover class
    values to RGBA. Allows vectorised lookup via fancy indexing.
    """
    table = np.zeros((256, 4), dtype=np.uint8)
    # Fill default
    table[:] = WC_DEFAULT_RGBA
    # Fill known classes
    for wc_class, rgba in WC_CLASS_TO_RGBA.items():
        if 0 <= wc_class < 256:
            table[wc_class] = rgba
    return table

def generate_splatmap(band, transform, inv_transform, lookup, cx, cy, mpdLat, mpdLon):
    """
    Generates a SPLAT_SIZE x SPLAT_SIZE RGBA numpy array for one chunk.

    band         — the full WorldCover raster already loaded into RAM as uint8 numpy array
    transform    — rasterio Affine transform (world coords → pixel coords)
    inv_transform — inverse transform (pixel coords → world coords) - not used but kept for clarity
    lookup       — (256, 4) uint8 lookup table built by build_lookup_table()
    """
    wx_min, wz_min, wx_max, wz_max = chunk_world_bounds(cx, cy)

    # Build grids of world X and Z positions for every texel in the splatmap
    # Shape: (SPLAT_SIZE,) each
    col_frac = np.linspace(0.0, 1.0, SPLAT_SIZE)
    row_frac = np.linspace(0.0, 1.0, SPLAT_SIZE)

    world_xs = wx_min + col_frac * (wx_max - wx_min)  # shape (SPLAT_SIZE,)
    world_zs = wz_min + row_frac * (wz_max - wz_min)  # shape (SPLAT_SIZE,)

    # Convert world XZ to lat/lon grids
    # world_xs varies by column, world_zs varies by row
    lons = ORIGIN_LON + world_xs / mpdLon   # shape (SPLAT_SIZE,) — one per column
    lats = ORIGIN_LAT + world_zs / mpdLat   # shape (SPLAT_SIZE,) — one per row

    # Convert lat/lon to raster pixel coordinates using the affine transform
    # transform maps (lon, lat) → (col_px, row_px) in rasterio convention
    # ~ operator inverts the transform
    col_px = np.floor((lons - transform.c) / transform.a).astype(np.int32)
    row_px = np.floor((lats - transform.f) / transform.e).astype(np.int32)

    raster_h, raster_w = band.shape

    # Clamp to raster bounds to avoid index errors at edges
    col_px = np.clip(col_px, 0, raster_w - 1)  # shape (SPLAT_SIZE,)
    row_px = np.clip(row_px, 0, raster_h - 1)  # shape (SPLAT_SIZE,)

    # Build full 2D pixel coordinate grids
    # cols grid: same col_px for every row → tile across rows
    # rows grid: same row_px for every col → tile across cols
    col_grid = np.tile(col_px[np.newaxis, :], (SPLAT_SIZE, 1))  # (SPLAT_SIZE, SPLAT_SIZE)
    row_grid = np.tile(row_px[:, np.newaxis], (1, SPLAT_SIZE))  # (SPLAT_SIZE, SPLAT_SIZE)

    # Index into the raster band — fully vectorised, no Python loop
    wc_classes = band[row_grid, col_grid]  # shape (SPLAT_SIZE, SPLAT_SIZE) uint8

    # Map WorldCover class values to RGBA via lookup table
    rgba = lookup[wc_classes]  # shape (SPLAT_SIZE, SPLAT_SIZE, 4) uint8

    return rgba

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    print("=== WorldCover Splatmap Generator ===")
    print(f"WorldCover TIF : {WORLDCOVER_TIF}")
    print(f"Tiles folder   : {TILES_DIR}")
    print(f"Output folder  : {OUTPUT_DIR}")
    print(f"Splatmap size  : {SPLAT_SIZE}x{SPLAT_SIZE} per chunk")
    print()

    # Validate input
    if not os.path.exists(WORLDCOVER_TIF):
        print(f"ERROR: WorldCover GeoTIFF not found:")
        print(f"  {WORLDCOVER_TIF}")
        print("Update WORLDCOVER_TIF to point to your .tif file")
        input("Press Enter to exit.")
        return

    # Discover valid tile coordinates from .pbf files
    print("Scanning tile files...")
    valid_tiles = discover_valid_tiles(TILES_DIR)
    print(f"Found {len(valid_tiles)} valid tiles to process")
    print()

    if len(valid_tiles) == 0:
        print("No tile_X_Y.pbf files found. Check TILES_DIR path.")
        input("Press Enter to exit.")
        return

    os.makedirs(OUTPUT_DIR, exist_ok=True)

    mpdLat, mpdLon = meters_per_degree()
    lookup = build_lookup_table()

    # Open the raster and read the ENTIRE band into RAM once
    # This is the key performance fix vs the old version which read it per pixel
    print("Loading WorldCover raster into RAM...")
    with rasterio.open(WORLDCOVER_TIF) as src:
        print(f"  CRS    : {src.crs}")
        print(f"  Bounds : {src.bounds}")
        print(f"  Size   : {src.width} x {src.height} pixels")
        print()

        # Read full band as uint8 — WorldCover classes are 0-100, fit in uint8
        band      = src.read(1).astype(np.uint8)
        transform = src.transform

    print(f"Raster loaded. Generating splatmaps...")
    print()

    written  = 0
    skipped  = 0
    errors   = 0
    total    = len(valid_tiles)

    for i, (cx, cy) in enumerate(valid_tiles):
        out_path = os.path.join(OUTPUT_DIR, f"splatmap_{cx}_{cy}.png")

        # Skip if already exists — reruns only process new chunks
        if os.path.exists(out_path):
            skipped += 1
            written += 1
            _print_progress(i + 1, total, skipped, errors)
            continue

        try:
            rgba = generate_splatmap(
                band, transform, None, lookup, cx, cy, mpdLat, mpdLon)

            img = Image.fromarray(rgba, mode="RGBA")
            img.save(out_path)
            written += 1

        except Exception as e:
            errors += 1
            print(f"\n  Error on chunk ({cx},{cy}): {e}")

        _print_progress(i + 1, total, skipped, errors)

    print()
    print()
    print(f"Done!")
    print(f"  Written  : {written - skipped} new splatmaps")
    print(f"  Skipped  : {skipped} already existed")
    print(f"  Errors   : {errors}")
    print(f"  Output   : {OUTPUT_DIR}")
    print()

    # Write channel metadata so Unity side knows what each channel means
    meta = {
        "splat_size" : SPLAT_SIZE,
        "chunk_size" : CHUNK_SIZE,
        "origin_lat" : ORIGIN_LAT,
        "origin_lon" : ORIGIN_LON,
        "channels"   : {
            "R": "Grassland / Cropland",
            "G": "Tree cover / Shrubland",
            "B": "Built-up / Bare",
            "A": "Water"
        }
    }

    meta_path = os.path.join(OUTPUT_DIR, "splatmap_meta.json")
    with open(meta_path, "w") as f:
        json.dump(meta, f, indent=2)

    print(f"Copy {OUTPUT_DIR} to Assets/StreamingAssets/Splatmaps/")
    print()
    input("Press Enter to exit.")


def _print_progress(done, total, skipped, errors):
    pct = done / total * 100
    print(
        f"\r  [{done:>4}/{total}]  {pct:5.1f}%  "
        f"skipped: {skipped}  errors: {errors}    ",
        end="", flush=True
    )


if __name__ == "__main__":
    main()
