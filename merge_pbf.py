"""
OSM PBF Merger
==============
Merges akure.osm.pbf (roads + buildings from OSM) with
akure_terrain.osm.pbf (land cover polygons from ESA WorldCover)
into a single merged_akure.osm.pbf ready for splitter.py

REQUIREMENTS:
    pip install osmium

HOW TO RUN:
    python merge_pbf.py
"""

import osmium
import osmium.io
import os
import time

# -----------------------------------------------------------------------
# CONFIGURATION
# -----------------------------------------------------------------------

FILE_A    = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\akure.osm.pbf"           # OSM roads and features
FILE_B    = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\akure_terrain.osm.pbf"   # ESA WorldCover terrain
OUTPUT    = r"C:\Users\Hitokiri\Downloads\OSM\OsmSplitter\merged_akure.osm.pbf"    # Combined output for splitter

# -----------------------------------------------------------------------

class MergeWriter(osmium.SimpleHandler):
    def __init__(self, writer, id_offset_nodes=0, id_offset_ways=0):
        super().__init__()
        self.writer           = writer
        self.id_offset_nodes  = id_offset_nodes
        self.id_offset_ways   = id_offset_ways
        self.node_count       = 0
        self.way_count        = 0

    def node(self, n):
        if self.id_offset_nodes == 0:
            self.writer.add_node(n)
        else:
            # Offset IDs to avoid collisions between the two files
            self.writer.add_node(
                osmium.osm.mutable.Node(
                    id       = n.id + self.id_offset_nodes,
                    location = n.location,
                    tags     = {t.k: t.v for t in n.tags},
                    version  = 1,
                    visible  = True
                )
            )
        self.node_count += 1

    def way(self, w):
        if self.id_offset_ways == 0:
            self.writer.add_way(w)
        else:
            # Offset node refs and way ID
            new_refs = [r.ref + self.id_offset_nodes for r in w.nodes]
            self.writer.add_way(
                osmium.osm.mutable.Way(
                    id      = w.id + self.id_offset_ways,
                    nodes   = new_refs,
                    tags    = {t.k: t.v for t in w.tags},
                    version = 1,
                    visible = True
                )
            )
        self.way_count += 1

def get_max_ids(filepath):
    """Scans a PBF and returns the max node ID and max way ID."""
    class IdScanner(osmium.SimpleHandler):
        def __init__(self):
            super().__init__()
            self.max_node_id = 0
            self.max_way_id  = 0

        def node(self, n):
            if n.id > self.max_node_id:
                self.max_node_id = n.id

        def way(self, w):
            if w.id > self.max_way_id:
                self.max_way_id = w.id

    scanner = IdScanner()
    scanner.apply_file(filepath, locations=False)
    return scanner.max_node_id, scanner.max_way_id

def main():
    print("=== OSM PBF Merger ===")
    print(f"File A : {FILE_A}")
    print(f"File B : {FILE_B}")
    print(f"Output : {OUTPUT}")
    print()

    for f in [FILE_A, FILE_B]:
        if not os.path.exists(f):
            print(f"ERROR: File not found: {f}")
            input("Press Enter to exit.")
            return

    os.makedirs(os.path.dirname(OUTPUT) or ".", exist_ok=True)

    total_start = time.time()

    # --- Scan File A for max IDs so File B can be offset ---
    print("Step 1/3 — Scanning File A for max IDs...")
    max_node_a, max_way_a = get_max_ids(FILE_A)
    print(f"  Max node ID: {max_node_a:,}")
    print(f"  Max way ID : {max_way_a:,}")

    # Add buffer to avoid any edge case collisions
    node_offset = max_node_a + 1000000
    way_offset  = max_way_a  + 1000000

    print(f"  File B node ID offset: +{node_offset:,}")
    print(f"  File B way ID offset : +{way_offset:,}")

    writer = osmium.SimpleWriter(OUTPUT)

    # --- Write File A ---
    print("\nStep 2/3 — Writing File A (OSM roads and features)...")
    writer_a = MergeWriter(writer, id_offset_nodes=0, id_offset_ways=0)
    writer_a.apply_file(FILE_A, locations=True, idx="sparse_mem_array")
    print(f"  Nodes written: {writer_a.node_count:,}")
    print(f"  Ways written : {writer_a.way_count:,}")

    # --- Write File B with offset IDs ---
    print("\nStep 3/3 — Writing File B (ESA WorldCover terrain)...")
    writer_b = MergeWriter(writer,
                           id_offset_nodes=node_offset,
                           id_offset_ways=way_offset)
    writer_b.apply_file(FILE_B, locations=True, idx="sparse_mem_array")
    print(f"  Nodes written: {writer_b.node_count:,}")
    print(f"  Ways written : {writer_b.way_count:,}")

    writer.close()

    total_elapsed = time.time() - total_start
    file_size_mb  = os.path.getsize(OUTPUT) / (1024 * 1024)

    print(f"\nDone!")
    print(f"  Total nodes  : {writer_a.node_count + writer_b.node_count:,}")
    print(f"  Total ways   : {writer_a.way_count + writer_b.way_count:,}")
    print(f"  File size    : {file_size_mb:.2f} MB")
    print(f"  Total time   : {total_elapsed:.0f}s")
    print()
    print("Next step:")
    print(f"  Open splitter.py and set:")
    print(f"  SOURCE_FILE = r\"{OUTPUT}\"")
    print(f"  Then run splitter.py to generate Unity tiles")

    input("\nPress Enter to exit.")

if __name__ == "__main__":
    main()
