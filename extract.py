"""
Akure Extractor — Memory Efficient Version
==========================================
Extracts Akure South LGA from a Nigeria-wide .osm.pbf file.

Previous version used locations=True which loads ALL node coordinates
into RAM — for Nigeria that's 400m+ nodes = 4GB+ memory usage.

This version uses osmium's disk-based location store (SparseMemory)
which keeps only the nodes we actually need in memory.
RAM usage should stay under 200MB.

REQUIREMENTS:
    pip install osmium

HOW TO RUN:
    1. Edit SOURCE_FILE and OUTPUT_FILE below
    2. Run: python extract_akure.py
"""

import osmium
import osmium.io
import os
import time

# -----------------------------------------------------------------------
# CONFIGURATION
# -----------------------------------------------------------------------

SOURCE_FILE = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\nigeria-250919.osm.pbf"
OUTPUT_FILE = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\akure.osm.pbf"

# Bounding box for Akure South LGA + buffer
MIN_LAT =  7.15
MAX_LAT =  7.40
MIN_LON =  5.08
MAX_LON =  5.35

# -----------------------------------------------------------------------
CENTER_LAT = (MIN_LAT + MAX_LAT) / 2
CENTER_LON = (MIN_LON + MAX_LON) / 2
# -----------------------------------------------------------------------

# --- Pass 1: Collect node IDs within bbox ---

class NodeCollector(osmium.SimpleHandler):
    """Scans nodes only — no coordinate storage, just IDs within bbox."""
    def __init__(self):
        super().__init__()
        self.node_ids = set()
        self.count    = 0
        self.start    = time.time()

    def node(self, n):
        lat = n.location.lat
        lon = n.location.lon

        if MIN_LAT <= lat <= MAX_LAT and MIN_LON <= lon <= MAX_LON:
            self.node_ids.add(n.id)

        self.count += 1
        if self.count % 1000000 == 0:
            print(f"\r  Pass 1 — Nodes scanned: {self.count:,}  "
                  f"in bbox: {len(self.node_ids):,}  "
                  f"({time.time()-self.start:.0f}s)", end="", flush=True)

# --- Pass 2: Collect way IDs whose nodes touch bbox ---

class WayCollector(osmium.SimpleHandler):
    """
    Scans ways only.
    A way is included if ANY of its node refs are in our bbox node set.
    Also expands node_ids to include ALL nodes of included ways
    so roads that cross the boundary are written complete.
    """
    def __init__(self, bbox_node_ids):
        super().__init__()
        self.node_ids = bbox_node_ids  # will be expanded in place
        self.way_ids  = set()
        self.count    = 0
        self.start    = time.time()

    def way(self, w):
        refs = [n.ref for n in w.nodes]

        for ref in refs:
            if ref in self.node_ids:
                self.way_ids.add(w.id)
                # Include all nodes of this way
                for r in refs:
                    self.node_ids.add(r)
                break

        self.count += 1
        if self.count % 100000 == 0:
            print(f"\r  Pass 2 — Ways scanned: {self.count:,}  "
                  f"matched: {len(self.way_ids):,}  "
                  f"({time.time()-self.start:.0f}s)", end="", flush=True)

# --- Pass 3: Write matching elements to output PBF ---

class ExtractWriter(osmium.SimpleHandler):
    """
    Reads source once more and writes only matched nodes and ways.
    Uses osmium's SimpleWriter for PBF output.
    """
    def __init__(self, node_ids, way_ids, writer):
        super().__init__()
        self.node_ids      = node_ids
        self.way_ids       = way_ids
        self.writer        = writer
        self.written_nodes = 0
        self.written_ways  = 0
        self.count         = 0
        self.start         = time.time()

    def node(self, n):
        if n.id in self.node_ids:
            self.writer.add_node(n)
            self.written_nodes += 1

        self.count += 1
        if self.count % 1000000 == 0:
            print(f"\r  Pass 3 — Elements scanned: {self.count:,}  "
                  f"nodes written: {self.written_nodes:,}  "
                  f"({time.time()-self.start:.0f}s)", end="", flush=True)

    def way(self, w):
        if w.id in self.way_ids:
            self.writer.add_way(w)
            self.written_ways += 1

        self.count += 1
        if self.count % 100000 == 0:
            print(f"\r  Pass 3 — Elements scanned: {self.count:,}  "
                  f"ways written: {self.written_ways:,}  "
                  f"({time.time()-self.start:.0f}s)", end="", flush=True)

# -----------------------------------------------------------------------

def main():
    print("=== Akure Extractor (Memory Efficient) ===")
    print(f"Source  : {SOURCE_FILE}")
    print(f"Output  : {OUTPUT_FILE}")
    print(f"BBox    : Lat [{MIN_LAT}, {MAX_LAT}]  Lon [{MIN_LON}, {MAX_LON}]")
    print(f"Center  : Lat {CENTER_LAT:.4f}  Lon {CENTER_LON:.4f}")
    print()
    print("Settings for splitter.py and WorldStreamer:")
    print(f"  ORIGIN_LAT = {CENTER_LAT:.4f}")
    print(f"  ORIGIN_LON = {CENTER_LON:.4f}")
    print()

    if not os.path.exists(SOURCE_FILE):
        print(f"ERROR: Source file not found: {SOURCE_FILE}")
        input("Press Enter to exit.")
        return

    os.makedirs(os.path.dirname(OUTPUT_FILE) or ".", exist_ok=True)

    total_start = time.time()

    # --- Pass 1: Find all node IDs within bbox ---
    print("Pass 1/3 — Collecting node IDs within bounding box...")
    print("  (no coordinate storage — memory efficient)")
    nc = NodeCollector()
    nc.apply_file(SOURCE_FILE, locations=True,
                  idx="sparse_mem_array")  # sparse index = low RAM
    print(f"\r  Nodes in bbox: {len(nc.node_ids):,}  "
          f"— {time.time()-total_start:.0f}s elapsed")

    if len(nc.node_ids) == 0:
        print("\nWARNING: No nodes found in bounding box.")
        print("Check that MIN_LAT/MAX_LAT/MIN_LON/MAX_LON are correct.")
        input("Press Enter to exit.")
        return

    # --- Pass 2: Find ways that touch the bbox ---
    print("\nPass 2/3 — Collecting ways within bounding box...")
    wc = WayCollector(nc.node_ids)
    wc.apply_file(SOURCE_FILE, locations=False)
    print(f"\r  Ways matched : {len(wc.way_ids):,}  "
          f"— {time.time()-total_start:.0f}s elapsed")
    print(f"  Total nodes needed (including road overflow): {len(nc.node_ids):,}")

    # --- Pass 3: Write output PBF ---
    print(f"\nPass 3/3 — Writing {OUTPUT_FILE}...")
    writer = osmium.SimpleWriter(OUTPUT_FILE)
    ew = ExtractWriter(nc.node_ids, wc.way_ids, writer)
    ew.apply_file(SOURCE_FILE, locations=True,
                  idx="sparse_mem_array")
    writer.close()

    total_elapsed = time.time() - total_start
    file_size_mb  = os.path.getsize(OUTPUT_FILE) / (1024 * 1024)

    print(f"\nDone!")
    print(f"  Nodes written : {ew.written_nodes:,}")
    print(f"  Ways written  : {ew.written_ways:,}")
    print(f"  File size     : {file_size_mb:.1f} MB")
    print(f"  Total time    : {total_elapsed:.0f}s ({total_elapsed/60:.1f} mins)")
    print()
    print("Next steps:")
    print(f"  1. Open splitter.py")
    print(f"  2. Set SOURCE_FILE = r\"{OUTPUT_FILE}\"")
    print(f"  3. Set ORIGIN_LAT  = {CENTER_LAT:.4f}")
    print(f"  4. Set ORIGIN_LON  = {CENTER_LON:.4f}")
    print(f"  5. Set CHUNK_SIZE  = 500.0")
    print(f"  6. Run splitter.py")
    print()
    print("In WorldStreamer Inspector:")
    print(f"  Origin Latitude  : {CENTER_LAT:.4f}")
    print(f"  Origin Longitude : {CENTER_LON:.4f}")
    print(f"  Chunk Size       : 500")

    input("\nPress Enter to exit.")

if __name__ == "__main__":
    main()
