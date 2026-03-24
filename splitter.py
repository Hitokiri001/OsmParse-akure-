"""
OSM Tile Splitter
=================
Splits a large .osm or .osm.pbf file into small chunk-sized .pbf tile files.
Outputs tile_X_Y.pbf files ready for Unity's OsmLoader.

REQUIREMENTS:
    pip install osmium

HOW TO RUN:
    1. Edit the CONFIGURATION section below
    2. Open terminal in this script's folder
    3. Run: python splitter.py
"""

import osmium
import osmium.io
import os
import math
import time

# -----------------------------------------------------------------------
# CONFIGURATION — edit these before running
# -----------------------------------------------------------------------

SOURCE_FILE  = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\akure_full.osm.pbf"
ORIGIN_LAT   = 7.275
ORIGIN_LON   = 5.215
CHUNK_SIZE   = 1000.0   # meters, must match WorldStreamer ChunkSize
OUTPUT_DIR   = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\Tiles"
OVERWRITE    = True     # set False to skip existing tiles (useful for resuming)

# -----------------------------------------------------------------------

EARTH_RADIUS = 6378137.0

def meters_per_degree_lat():
    return math.pi * EARTH_RADIUS / 180.0

def meters_per_degree_lon(lat):
    return math.pi * EARTH_RADIUS * math.cos(math.radians(lat)) / 180.0

def lat_to_chunk(lat):
    return math.floor((lat - ORIGIN_LAT) * meters_per_degree_lat() / CHUNK_SIZE)

def lon_to_chunk(lon):
    return math.floor((lon - ORIGIN_LON) * meters_per_degree_lon(ORIGIN_LAT) / CHUNK_SIZE)

# -----------------------------------------------------------------------
# Pass 1 — Index all nodes
# -----------------------------------------------------------------------

class NodeIndexer(osmium.SimpleHandler):
    def __init__(self):
        super().__init__()
        self.nodes   = {}
        self.min_lat =  90.0
        self.max_lat = -90.0
        self.min_lon =  180.0
        self.max_lon = -180.0
        self.count   = 0
        self.start   = time.time()

    def node(self, n):
        lat = n.location.lat
        lon = n.location.lon
        self.nodes[n.id] = (lat, lon)

        if lat < self.min_lat: self.min_lat = lat
        if lat > self.max_lat: self.max_lat = lat
        if lon < self.min_lon: self.min_lon = lon
        if lon > self.max_lon: self.max_lon = lon

        self.count += 1
        if self.count % 500000 == 0:
            print(f"\r  Nodes indexed: {self.count:,}  ({time.time()-self.start:.0f}s)", end="", flush=True)

# -----------------------------------------------------------------------
# Pass 2 — Assign ways to tiles
# -----------------------------------------------------------------------

class WayAssigner(osmium.SimpleHandler):
    def __init__(self, node_index):
        super().__init__()
        self.node_index = node_index
        self.tile_ways  = {}   # (cx, cy) -> list of way dicts
        self.tile_nodes = {}   # (cx, cy) -> set of node ids needed
        self.count      = 0
        self.start      = time.time()

        # Tag sampling
        self.tag_keys     = set()
        self.highway_vals = set()
        self.natural_vals = set()
        self.landuse_vals = set()

    def way(self, w):
        tags = {t.k: t.v for t in w.tags}

        for k, v in tags.items():
            if len(self.tag_keys)     < 60: self.tag_keys.add(k)
            if k == "highway" and len(self.highway_vals) < 20: self.highway_vals.add(v)
            if k == "natural" and len(self.natural_vals) < 20: self.natural_vals.add(v)
            if k == "landuse" and len(self.landuse_vals) < 20: self.landuse_vals.add(v)

        node_ids = [n.ref for n in w.nodes]
        if len(node_ids) < 2:
            return

        touched_tiles = set()
        for nid in node_ids:
            if nid not in self.node_index:
                continue
            lat, lon = self.node_index[nid]
            touched_tiles.add((lon_to_chunk(lon), lat_to_chunk(lat)))

        way_data = {
            "id":       w.id,
            "tags":     tags,
            "node_ids": node_ids
        }

        for tile in touched_tiles:
            if tile not in self.tile_ways:
                self.tile_ways[tile]  = []
                self.tile_nodes[tile] = set()
            self.tile_ways[tile].append(way_data)
            for nid in node_ids:
                self.tile_nodes[tile].add(nid)

        self.count += 1
        if self.count % 100000 == 0:
            print(f"\r  Ways processed: {self.count:,} — tiles: {len(self.tile_ways):,}  ({time.time()-self.start:.0f}s)", end="", flush=True)

# -----------------------------------------------------------------------
# Write a single tile as .pbf using osmium
# -----------------------------------------------------------------------

def write_tile_pbf(path, tile_node_ids, tile_ways, node_index):
    writer = osmium.SimpleWriter(path)

    try:
        # Write nodes first
        for nid in tile_node_ids:
            if nid not in node_index:
                continue
            lat, lon = node_index[nid]
            writer.add_node(
                osmium.osm.mutable.Node(
                    id       = nid,
                    location = osmium.osm.Location(lon, lat),
                    version  = 1,
                    visible  = True
                )
            )

        # Write ways
        for way in tile_ways:
            writer.add_way(
                osmium.osm.mutable.Way(
                    id      = way["id"],
                    nodes   = way["node_ids"],
                    tags    = way["tags"],
                    version = 1,
                    visible = True
                )
            )
    finally:
        writer.close()

# -----------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------

def main():
    print("=== OSM Tile Splitter (Python — PBF output) ===")
    print(f"Source : {SOURCE_FILE}")
    print(f"Origin : {ORIGIN_LAT}, {ORIGIN_LON}")
    print(f"Chunk  : {CHUNK_SIZE}m")
    print(f"Output : {OUTPUT_DIR}")
    print()

    if not os.path.exists(SOURCE_FILE):
        print(f"ERROR: Source file not found: {SOURCE_FILE}")
        input("Press Enter to exit.")
        return

    os.makedirs(OUTPUT_DIR, exist_ok=True)

    total_start = time.time()

    # --- Pass 1 ---
    print("Pass 1/2 — Indexing nodes...")
    indexer = NodeIndexer()
    indexer.apply_file(SOURCE_FILE, locations=False)
    print(f"\r  Nodes indexed: {indexer.count:,} — {time.time()-total_start:.0f}s elapsed")

    center_lat = (indexer.min_lat + indexer.max_lat) / 2
    center_lon = (indexer.min_lon + indexer.max_lon) / 2
    print(f"  Data center — Lat: {center_lat:.6f}  Lon: {center_lon:.6f}")

    min_cx = lon_to_chunk(indexer.min_lon)
    max_cx = lon_to_chunk(indexer.max_lon)
    min_cy = lat_to_chunk(indexer.min_lat)
    max_cy = lat_to_chunk(indexer.max_lat)
    potential = (max_cx - min_cx + 1) * (max_cy - min_cy + 1)
    print(f"  Grid: ({min_cx},{min_cy}) to ({max_cx},{max_cy}) — {potential:,} potential tiles")

    # --- Pass 2 ---
    print("\nPass 2/2 — Assigning ways to tiles...")
    assigner = WayAssigner(indexer.nodes)
    assigner.apply_file(SOURCE_FILE, locations=False)
    print(f"\r  Ways processed: {assigner.count:,} — {len(assigner.tile_ways):,} tiles — {time.time()-total_start:.0f}s elapsed")

    print(f"\n  [DEBUG] Tag keys found  : {', '.join(sorted(assigner.tag_keys))}")
    print(f"  [DEBUG] highway values  : {', '.join(sorted(assigner.highway_vals))}")
    print(f"  [DEBUG] natural values  : {', '.join(sorted(assigner.natural_vals))}")
    print(f"  [DEBUG] landuse values  : {', '.join(sorted(assigner.landuse_vals))}")

    # --- Write tiles ---
    print(f"\nWriting {len(assigner.tile_ways):,} tile files as .pbf...")

    written  = 0
    skipped  = 0
    write_start = time.time()

    for (cx, cy), ways in assigner.tile_ways.items():
        tile_path = os.path.join(OUTPUT_DIR, f"tile_{cx}_{cy}.pbf")

        if not OVERWRITE and os.path.exists(tile_path):
            skipped += 1
            continue

        node_ids = assigner.tile_nodes.get((cx, cy), set())

        try:
            write_tile_pbf(tile_path, node_ids, ways, indexer.nodes)
        except Exception as e:
            print(f"\n  WARNING: Failed to write tile ({cx},{cy}): {e}")
            continue

        written += 1
        if written % 100 == 0 or written == len(assigner.tile_ways):
            elapsed = time.time() - write_start
            rate    = written / elapsed if elapsed > 0 else 0
            remaining = (len(assigner.tile_ways) - written) / rate if rate > 0 else 0
            print(f"\r  Written: {written}/{len(assigner.tile_ways)} — {elapsed:.0f}s elapsed — ~{remaining/60:.1f} min remaining   ", end="", flush=True)

    total_elapsed = time.time() - total_start
    print(f"\n\nDone!")
    print(f"  Tiles written : {written}")
    print(f"  Tiles skipped : {skipped}")
    print(f"  Total time    : {total_elapsed:.0f}s ({total_elapsed/60:.1f} mins)")
    print(f"\nNext steps:")
    print(f"  1. Copy contents of: {OUTPUT_DIR}")
    print(f"     to Assets/StreamingAssets/OsmTiles/ in Unity")
    print(f"  2. OsmLoader reads .pbf — make sure you have the updated OsmLoader.cs")

    input("\nPress Enter to exit.")

if __name__ == "__main__":
    main()
