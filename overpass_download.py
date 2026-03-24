"""
Overpass OSM Downloader for Akure
===================================
Step 0: Checks if OSM elevation (ele) tags exist in Akure bbox
        Reports how many nodes have elevation data
        Asks whether to continue with full download

Step 1: Downloads ALL OSM data for Akure bbox from Overpass API
Step 2: Converts .osm XML to .osm.pbf for splitter.py

REQUIREMENTS:
    pip install requests osmium

HOW TO RUN:
    python overpass_download.py
"""

import os
import sys
import time
import json
import requests

try:
    import osmium
    import osmium.io
except ImportError:
    print("Missing osmium. Run: pip install osmium")
    sys.exit(1)

# -----------------------------------------------------------------------
# CONFIGURATION
# -----------------------------------------------------------------------

MIN_LAT =  7.15
MAX_LAT =  7.40
MIN_LON =  5.08
MAX_LON =  5.35

OUTPUT_DIR  = r"C:\OSM"
OUTPUT_OSM  = os.path.join(OUTPUT_DIR, "akure_full.osm")
OUTPUT_PBF  = os.path.join(OUTPUT_DIR, "akure_full.osm.pbf")

OVERPASS_SERVERS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
    "https://overpass.openstreetmap.ru/api/interpreter",
]

TIMEOUT = 300

# -----------------------------------------------------------------------

def query_overpass(query, timeout=30):
    """Sends a query to Overpass API, tries each server in order."""
    last_error = None

    for server in OVERPASS_SERVERS:
        try:
            response = requests.post(
                server,
                data    = {"data": query},
                timeout = timeout,
                headers = {"User-Agent": "AkureGameExtractor/1.0"}
            )

            if response.status_code == 200:
                if b"<remark>" in response.content and b"error" in response.content.lower():
                    last_error = "Server error in response"
                    continue
                return response
            elif response.status_code == 429:
                print(f"  Rate limited on {server} — waiting 30s...")
                time.sleep(30)
                continue
            else:
                last_error = f"HTTP {response.status_code}"
                continue

        except requests.exceptions.Timeout:
            last_error = "Timeout"
            continue
        except requests.exceptions.ConnectionError as e:
            last_error = str(e)
            continue

    raise Exception(f"All servers failed. Last error: {last_error}")

# -----------------------------------------------------------------------
# Step 0: Elevation check
# -----------------------------------------------------------------------

def check_elevation():
    """
    Queries Overpass for nodes with 'ele' tags in the bbox.
    Returns (found_count, sample_values).
    """
    bbox  = f"{MIN_LAT},{MIN_LON},{MAX_LAT},{MAX_LON}"
    query = f"""
[out:json][timeout:30];
node({bbox})["ele"];
out 100;
"""
    print("  Querying Overpass for nodes with elevation (ele) tags...")

    try:
        response = query_overpass(query, timeout=30)
        data     = response.json()
        elements = data.get("elements", [])

        if not elements:
            return 0, []

        # Extract sample elevation values
        samples = []
        for el in elements[:10]:
            ele = el.get("tags", {}).get("ele")
            if ele:
                samples.append(ele)

        return len(elements), samples

    except Exception as e:
        print(f"  Elevation check failed: {e}")
        return -1, []

def count_total_ele_nodes():
    """Counts total nodes with ele tags — uses count output."""
    bbox  = f"{MIN_LAT},{MIN_LON},{MAX_LAT},{MAX_LON}"
    query = f"""
[out:json][timeout:30];
node({bbox})["ele"];
out count;
"""
    try:
        response = query_overpass(query, timeout=30)
        data     = response.json()
        elements = data.get("elements", [])
        for el in elements:
            if el.get("type") == "count":
                return int(el.get("tags", {}).get("nodes", 0))
        return len(data.get("elements", []))
    except:
        return -1

# -----------------------------------------------------------------------
# Step 1: Full download
# -----------------------------------------------------------------------

def build_full_query(min_lat, max_lat, min_lon, max_lon):
    bbox = f"{min_lat},{min_lon},{max_lat},{max_lon}"
    return f"""
[out:xml][timeout:{TIMEOUT}];
(
  node({bbox});
  way({bbox});
  relation({bbox});
);
out body;
>;
out skel qt;
"""

def download_full(query, output_path):
    """Downloads full OSM XML from Overpass."""
    last_error = None

    for server in OVERPASS_SERVERS:
        print(f"  Trying: {server}")
        try:
            start    = time.time()
            response = requests.post(
                server,
                data    = {"data": query},
                timeout = TIMEOUT,
                stream  = True,
                headers = {"User-Agent": "AkureGameExtractor/1.0"}
            )

            if response.status_code == 200:
                total      = int(response.headers.get("content-length", 0))
                downloaded = 0

                with open(output_path, "wb") as f:
                    for chunk in response.iter_content(chunk_size=65536):
                        if chunk:
                            f.write(chunk)
                            downloaded += len(chunk)
                            if total > 0:
                                pct = downloaded / total * 100
                                print(f"\r  {pct:.1f}% — {downloaded/(1024*1024):.2f} MB "
                                      f"({time.time()-start:.0f}s)", end="", flush=True)
                            else:
                                print(f"\r  {downloaded/(1024*1024):.2f} MB downloaded "
                                      f"({time.time()-start:.0f}s)", end="", flush=True)

                print(f"\n  Download complete: {downloaded/(1024*1024):.2f} MB "
                      f"in {time.time()-start:.0f}s")
                return True

            elif response.status_code == 429:
                print(f"\n  Rate limited — waiting 60s...")
                time.sleep(60)
                continue
            else:
                last_error = f"HTTP {response.status_code}"
                continue

        except requests.exceptions.Timeout:
            last_error = "Timeout"
            print(f"\n  Timeout — trying next server")
            continue
        except requests.exceptions.ConnectionError as e:
            last_error = str(e)
            print(f"\n  Connection error — trying next server")
            continue

    raise Exception(f"All servers failed. Last error: {last_error}")

# -----------------------------------------------------------------------
# Step 2: Convert to PBF
# -----------------------------------------------------------------------

class OsmCopyHandler(osmium.SimpleHandler):
    def __init__(self, writer):
        super().__init__()
        self.writer    = writer
        self.nodes     = 0
        self.ways      = 0
        self.relations = 0

    def node(self, n):
        self.writer.add_node(n)
        self.nodes += 1

    def way(self, w):
        self.writer.add_way(w)
        self.ways += 1

    def relation(self, r):
        self.writer.add_relation(r)
        self.relations += 1

def convert_to_pbf(osm_path, pbf_path):
    print(f"  Converting to PBF...")
    writer  = osmium.SimpleWriter(pbf_path)
    handler = OsmCopyHandler(writer)
    handler.apply_file(osm_path, locations=True, idx="sparse_mem_array")
    writer.close()

    print(f"  Nodes     : {handler.nodes:,}")
    print(f"  Ways      : {handler.ways:,}")
    print(f"  Relations : {handler.relations:,}")
    print(f"  PBF size  : {os.path.getsize(pbf_path)/(1024*1024):.2f} MB")

# -----------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------

def main():
    print("=== Overpass OSM Downloader for Akure ===")
    print(f"BBox : Lat [{MIN_LAT}, {MAX_LAT}]  Lon [{MIN_LON}, {MAX_LON}]")
    print()

    os.makedirs(OUTPUT_DIR, exist_ok=True)

    # -----------------------------------------------------------------------
    # Step 0: Elevation check
    # -----------------------------------------------------------------------
    print("Step 0/2 — Checking for elevation (ele) data in Akure OSM...")
    found, samples = check_elevation()

    if found == -1:
        print("  Could not reach Overpass API to check elevation.")
        print("  Check your internet connection.")
        choice = input("\n  Continue with download anyway? (y/n): ").strip().lower()
        if choice != "y":
            print("Cancelled.")
            input("Press Enter to exit.")
            return

    elif found == 0:
        print()
        print("  ✗ NO elevation (ele) tags found in Akure OSM data.")
        print("  This means road nodes and buildings won't have height values.")
        print("  Terrain elevation will need to come from SRTM/Open-Elevation instead.")
        print()
        print("  The download will still contain:")
        print("  — All roads, buildings, landuse, amenities, POIs")
        print("  — Building positions for placing external meshes")
        print("  — Road network for streaming")
        print()
        choice = input("  Continue with download anyway? (y/n): ").strip().lower()
        if choice != "y":
            print("Cancelled.")
            input("Press Enter to exit.")
            return

    else:
        total = count_total_ele_nodes()
        print()
        print(f"  ✓ Elevation data found!")
        print(f"  Nodes with ele tags : {total if total > 0 else f'at least {found}'}")
        if samples:
            print(f"  Sample values (m)   : {', '.join(samples[:5])}")
        print()

        # Assess quality
        if total > 0 and total < 100:
            print("  ⚠ Very sparse elevation coverage — only a few tagged nodes.")
            print("  OSM elevation data alone won't give full terrain coverage.")
            print("  SRTM/Open-Elevation would give better results for a heightmap.")
        elif total >= 100:
            print("  Good elevation coverage found.")
            print("  These values will be available in your OSM tiles.")

        print()
        choice = input("  Continue with full download? (y/n): ").strip().lower()
        if choice != "y":
            print("Cancelled.")
            input("Press Enter to exit.")
            return

    # -----------------------------------------------------------------------
    # Step 1: Full download
    # -----------------------------------------------------------------------
    print()
    print("Step 1/2 — Downloading full OSM data from Overpass...")

    if os.path.exists(OUTPUT_OSM):
        size_mb = os.path.getsize(OUTPUT_OSM) / (1024 * 1024)
        print(f"  Cached .osm found ({size_mb:.2f} MB) — skipping download")
        print(f"  Delete {OUTPUT_OSM} to re-download")
    else:
        query = build_full_query(MIN_LAT, MAX_LAT, MIN_LON, MAX_LON)
        download_full(query, OUTPUT_OSM)

    # -----------------------------------------------------------------------
    # Step 2: Convert to PBF
    # -----------------------------------------------------------------------
    print()
    print("Step 2/2 — Converting to PBF...")
    convert_to_pbf(OUTPUT_OSM, OUTPUT_PBF)

    pbf_mb = os.path.getsize(OUTPUT_PBF) / (1024 * 1024)
    osm_mb = os.path.getsize(OUTPUT_OSM) / (1024 * 1024)

    print(f"\nDone!")
    print(f"  OSM : {OUTPUT_OSM} ({osm_mb:.2f} MB)")
    print(f"  PBF : {OUTPUT_PBF} ({pbf_mb:.2f} MB)")
    print()
    print("Next steps:")
    print(f"  1. Open splitter.py and set:")
    print(f"     SOURCE_FILE = r\"{OUTPUT_PBF}\"")
    print(f"     ORIGIN_LAT  = 7.275")
    print(f"     ORIGIN_LON  = 5.215")
    print(f"     CHUNK_SIZE  = 1000.0")
    print(f"  2. Run splitter.py")

    input("\nPress Enter to exit.")

if __name__ == "__main__":
    main()
