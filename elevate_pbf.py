"""
OSM PBF Node Elevation Tagger
==============================
Reads an OSM PBF file, queries the Open-Elevation API for the
real SRTM elevation of every node, and writes a new PBF file
with ele tags added to all nodes.

The output PBF feeds into splitter.py as normal. OsmParser.cs
then reads the ele tag to set correct Y world positions for
roads and building positions.

REQUIREMENTS:
    pip install requests osmium

HOW TO RUN:
    1. Run overpass_download.py first to get akure_full.osm.pbf
    2. Edit INPUT_PBF and OUTPUT_PBF below
    3. Run: python elevate_pbf.py

Open-Elevation API:
    Free, no account, no API key
    Based on NASA SRTM 30m data
    Batch endpoint: up to 1000 points per request
    https://api.open-elevation.com
"""

import os
import sys
import time
import json
import requests

try:
    import osmium
    import osmium.io
    import osmium.osm.mutable
except ImportError:
    print("Missing osmium. Run: pip install osmium")
    sys.exit(1)

# -----------------------------------------------------------------------
# CONFIGURATION
# -----------------------------------------------------------------------

INPUT_PBF  = r"C:\OSM\akure_full.osm.pbf"
OUTPUT_PBF = r"C:\OSM\akure_elevated.osm.pbf"

# Open-Elevation API endpoint
# Primary and fallback servers
ELEVATION_SERVERS = [
    "https://api.open-elevation.com/api/v1/lookup",
    "https://open.elevation.au/api/v1/lookup",
]

# Number of nodes to query per API request
# Open-Elevation handles up to 1000 per request reliably
BATCH_SIZE = 500

# Delay between batches in seconds — be polite to the free API
BATCH_DELAY = 0.5

# If API fails for a batch, use this fallback elevation (meters)
# Akure sits at roughly 370m above sea level
FALLBACK_ELEVATION = 370.0

# -----------------------------------------------------------------------

class NodeCollector(osmium.SimpleHandler):
    """Pass 1 — collect all node IDs and coordinates."""
    def __init__(self):
        super().__init__()
        self.nodes = {}  # id -> (lat, lon)
        self.count = 0
        self.start = time.time()

    def node(self, n):
        self.nodes[n.id] = (n.location.lat, n.location.lon)
        self.count += 1
        if self.count % 10000 == 0:
            print(f"\r  Collected: {self.count:,} nodes  "
                  f"({time.time()-self.start:.0f}s)", end="", flush=True)

def query_elevation_batch(locations, server):
    """
    Queries Open-Elevation for a batch of locations.
    locations: list of (lat, lon) tuples
    Returns list of elevation values in same order.
    """
    payload = {
        "locations": [
            {"latitude": lat, "longitude": lon}
            for lat, lon in locations
        ]
    }

    response = requests.post(
        server,
        json    = payload,
        timeout = 30,
        headers = {
            "Content-Type": "application/json",
            "Accept":       "application/json"
        }
    )

    if response.status_code != 200:
        raise Exception(f"HTTP {response.status_code}: {response.text[:100]}")

    data    = response.json()
    results = data.get("results", [])

    if len(results) != len(locations):
        raise Exception(f"Expected {len(locations)} results, got {len(results)}")

    return [r["elevation"] for r in results]

def get_elevations(node_ids, node_coords):
    """
    Queries Open-Elevation for all nodes in batches.
    Returns dict: node_id -> elevation_meters
    """
    elevations = {}
    ids_list   = list(node_ids)
    total      = len(ids_list)
    fetched    = 0
    failed     = 0
    start      = time.time()

    print(f"  Querying elevation for {total:,} nodes in batches of {BATCH_SIZE}...")
    print(f"  Estimated time: {total / BATCH_SIZE * (BATCH_DELAY + 2):.0f}s")
    print()

    for i in range(0, total, BATCH_SIZE):
        batch_ids    = ids_list[i:i + BATCH_SIZE]
        batch_coords = [node_coords[nid] for nid in batch_ids]

        batch_elevs = None
        last_error  = None

        for server in ELEVATION_SERVERS:
            try:
                batch_elevs = query_elevation_batch(batch_coords, server)
                break
            except Exception as e:
                last_error = str(e)
                continue

        if batch_elevs is None:
            # All servers failed for this batch — use fallback
            print(f"\n  Batch {i//BATCH_SIZE + 1} failed ({last_error}) — using fallback {FALLBACK_ELEVATION}m")
            batch_elevs = [FALLBACK_ELEVATION] * len(batch_ids)
            failed += len(batch_ids)
        else:
            fetched += len(batch_ids)

        for nid, elev in zip(batch_ids, batch_elevs):
            elevations[nid] = elev

        done    = min(i + BATCH_SIZE, total)
        elapsed = time.time() - start
        rate    = done / elapsed if elapsed > 0 else 1
        remain  = (total - done) / rate if rate > 0 else 0

        print(f"\r  Progress: {done:,}/{total:,} nodes | "
              f"Elapsed: {elapsed:.0f}s | "
              f"Remaining: ~{remain:.0f}s | "
              f"Failed: {failed:,}",
              end="", flush=True)

        if i + BATCH_SIZE < total:
            time.sleep(BATCH_DELAY)

    print()
    return elevations, fetched, failed

class ElevatedWriter(osmium.SimpleHandler):
    """Pass 2 — write all elements, adding ele tags to nodes."""
    def __init__(self, writer, elevations):
        super().__init__()
        self.writer     = writer
        self.elevations = elevations
        self.nodes_written    = 0
        self.nodes_elevated   = 0
        self.ways_written     = 0
        self.relations_written = 0
        self.start = time.time()

    def node(self, n):
        elev = self.elevations.get(n.id)

        if elev is not None:
            # Build new tags dict with ele added
            tags = {t.k: t.v for t in n.tags}
            tags["ele"] = f"{elev:.1f}"

            self.writer.add_node(
                osmium.osm.mutable.Node(
                    id       = n.id,
                    location = n.location,
                    tags     = tags,
                    version  = max(1, n.version),
                    visible  = True
                )
            )
            self.nodes_elevated += 1
        else:
            self.writer.add_node(n)

        self.nodes_written += 1
        if self.nodes_written % 10000 == 0:
            print(f"\r  Writing nodes: {self.nodes_written:,}  "
                  f"({time.time()-self.start:.0f}s)", end="", flush=True)

    def way(self, w):
        self.writer.add_way(w)
        self.ways_written += 1

    def relation(self, r):
        self.writer.add_relation(r)
        self.relations_written += 1

def main():
    print("=== OSM PBF Node Elevation Tagger ===")
    print(f"Input  : {INPUT_PBF}")
    print(f"Output : {OUTPUT_PBF}")
    print(f"API    : Open-Elevation (SRTM 30m)")
    print()

    if not os.path.exists(INPUT_PBF):
        print(f"ERROR: Input file not found: {INPUT_PBF}")
        print("Run overpass_download.py first.")
        input("Press Enter to exit.")
        return

    os.makedirs(os.path.dirname(OUTPUT_PBF) or ".", exist_ok=True)

    total_start = time.time()

    # --- Pass 1: Collect all node coordinates ---
    print("Pass 1/3 — Collecting node coordinates...")
    collector = NodeCollector()
    collector.apply_file(INPUT_PBF, locations=True, idx="sparse_mem_array")
    print(f"\r  Collected: {collector.count:,} nodes  "
          f"({time.time()-total_start:.0f}s)")

    # --- Query Open-Elevation ---
    print(f"\nPass 2/3 — Fetching elevations from Open-Elevation API...")
    elevations, fetched, failed = get_elevations(
        collector.nodes.keys(),
        collector.nodes
    )

    print(f"\n  Elevations fetched : {fetched:,}")
    print(f"  Used fallback      : {failed:,}")

    # Print elevation stats
    if elevations:
        elev_values = list(elevations.values())
        min_e = min(elev_values)
        max_e = max(elev_values)
        avg_e = sum(elev_values) / len(elev_values)
        print(f"  Min elevation      : {min_e:.1f}m")
        print(f"  Max elevation      : {max_e:.1f}m")
        print(f"  Avg elevation      : {avg_e:.1f}m")
        print(f"  Elevation range    : {max_e - min_e:.1f}m")

    # --- Pass 3: Write elevated PBF ---
    print(f"\nPass 3/3 — Writing elevated PBF...")
    writer  = osmium.SimpleWriter(OUTPUT_PBF)
    handler = ElevatedWriter(writer, elevations)
    handler.apply_file(INPUT_PBF, locations=True, idx="sparse_mem_array")
    writer.close()

    print(f"\r  Nodes written     : {handler.nodes_written:,}  "
          f"({time.time()-total_start:.0f}s)")
    print(f"  Nodes with ele    : {handler.nodes_elevated:,}")
    print(f"  Ways written      : {handler.ways_written:,}")
    print(f"  Relations written : {handler.relations_written:,}")

    total_elapsed = time.time() - total_start
    file_size_mb  = os.path.getsize(OUTPUT_PBF) / (1024 * 1024)

    print(f"\nDone!")
    print(f"  Output file : {OUTPUT_PBF}")
    print(f"  File size   : {file_size_mb:.2f} MB")
    print(f"  Total time  : {total_elapsed:.0f}s ({total_elapsed/60:.1f} mins)")
    print()
    print("Next steps:")
    print(f"  1. Open splitter.py and set:")
    print(f"     SOURCE_FILE = r\"{OUTPUT_PBF}\"")
    print(f"  2. Run splitter.py to generate tiles")
    print(f"  3. Update OsmParser.cs to read ele tags for Y positions")

    input("\nPress Enter to exit.")

if __name__ == "__main__":
    main()
