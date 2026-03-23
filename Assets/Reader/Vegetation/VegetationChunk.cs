// VegetationChunk.cs  (v3 — object pooling)
// One vegetation chunk — parent GameObject holding all trees for one 1000m×1000m tile.
//
// All tree GameObjects are rented from VegetationPool and returned on Remove().
// Instantiate is only called when the pool has no spare GOs for a species
// (first pass through an area). On revisit the GOs are reused with zero alloc.
//
// LOD levels:
//   dist < MeshDistance          → full mesh prefab active
//   MeshDistance .. BillDistance → billboard quad active
//   dist > BillDistance          → both culled (inactive)

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Transform))]
public class VegetationChunk : MonoBehaviour
{
    // ── Shared settings ───────────────────────────────────────────────────
    public static float MeshDistance = 200f;
    public static float BillDistance = 800f;

    /// Trees instantiated per frame during BuildCoroutine.
    public static int TreesPerFrame = 30;

    // ── Shared billboard mesh ─────────────────────────────────────────────
    static Mesh _billboardMesh;
    static Mesh GetBillboardMesh()
    {
        if (_billboardMesh != null) return _billboardMesh;
        _billboardMesh = new Mesh { name = "BillboardQuad" };
        _billboardMesh.vertices  = new[] {
            new Vector3(-0.5f,0,0), new Vector3(0.5f,0,0),
            new Vector3(-0.5f,1,0), new Vector3(0.5f,1,0),
        };
        _billboardMesh.uv        = new[] { new Vector2(0,0), new Vector2(1,0),
                                           new Vector2(0,1), new Vector2(1,1) };
        _billboardMesh.triangles = new[] { 0,2,1, 1,2,3 };
        _billboardMesh.RecalculateBounds();
        return _billboardMesh;
    }

    // ── LOD state ─────────────────────────────────────────────────────────
    const byte LOD_NONE = 0;
    const byte LOD_MESH = 1;
    const byte LOD_BILL = 2;

    struct TreeEntry
    {
        public GameObject MeshGO;
        public GameObject BillGO;
        public Vector3    Position;
        public int        Species;   // needed so Remove() can return to correct pool
        public byte       LastLOD;
    }

    readonly List<TreeEntry> _trees = new();

    // ── BuildCoroutine ────────────────────────────────────────────────────
    // Rents from VegetationPool first; only calls Instantiate on cache miss.

    public IEnumerator BuildCoroutine(
        List<VegetationMesher.TreePlacement> placements,
        GameObject[]                          meshPrefabs,
        Material[]                            billMaterials,
        Vector2[]                             billSizes)
    {
        var billMesh  = GetBillboardMesh();
        int thisFrame = 0;

        for (int i = 0; i < placements.Count; i++)
        {
            var p  = placements[i];
            int si = Mathf.Clamp(p.SpeciesIndex,
                0, Mathf.Min(meshPrefabs.Length, billMaterials.Length) - 1);

            // ── Full-mesh GO — rent or instantiate ───────────────────────
            GameObject meshGO = null;
            if (si < meshPrefabs.Length && meshPrefabs[si] != null)
            {
                meshGO = VegetationPool.RentMesh(si);

                if (meshGO == null)
                {
                    // Pool miss — first time seeing this species in this area
                    meshGO = Object.Instantiate(meshPrefabs[si]);
                }

                // Reset transform and parent regardless of origin
                meshGO.transform.SetParent(transform, false);
                meshGO.transform.SetPositionAndRotation(
                    p.WorldPosition,
                    Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
                meshGO.SetActive(false);
            }

            // ── Billboard GO — rent or create ────────────────────────────
            GameObject billGO = null;
            if (si < billMaterials.Length && billMaterials[si] != null)
            {
                billGO = VegetationPool.RentBill(si);

                if (billGO == null)
                {
                    // Pool miss — build the GO from scratch
                    billGO = new GameObject($"Bill_s{si}");
                    var mf  = billGO.AddComponent<MeshFilter>();
                    mf.sharedMesh = billMesh;
                    var mr  = billGO.AddComponent<MeshRenderer>();
                    // Per-instance material so _Width/_Height can differ per species.
                    // This material stays on the GO for its lifetime — no re-create on reuse.
                    var mat = new Material(billMaterials[si]);
                    if (si < billSizes.Length)
                    {
                        mat.SetFloat("_Width",  billSizes[si].x);
                        mat.SetFloat("_Height", billSizes[si].y);
                    }
                    mr.material = mat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

                // Reset parent and position regardless of origin
                billGO.transform.SetParent(transform, false);
                billGO.transform.position = p.WorldPosition;
                billGO.SetActive(false);
            }

            _trees.Add(new TreeEntry
            {
                MeshGO   = meshGO,
                BillGO   = billGO,
                Position = p.WorldPosition,
                Species  = si,
                LastLOD  = 255  // sentinel — forces correct SetActive on first UpdateLOD
            });

            if (++thisFrame >= TreesPerFrame)
            {
                thisFrame = 0;
                yield return null;
            }
        }
    }

    // ── LOD Update ────────────────────────────────────────────────────────
    // SetActive only called on state change — no redundant calls per frame.

    public void UpdateLOD(Vector3 playerPos)
    {
        float sqrMesh = MeshDistance * MeshDistance;
        float sqrBill = BillDistance * BillDistance;

        for (int i = 0; i < _trees.Count; i++)
        {
            var entry = _trees[i];

            float dx  = entry.Position.x - playerPos.x;
            float dz  = entry.Position.z - playerPos.z;
            float sqr = dx * dx + dz * dz;

            byte newLOD = sqr < sqrMesh ? LOD_MESH
                        : sqr < sqrBill ? LOD_BILL
                        : LOD_NONE;

            if (newLOD == entry.LastLOD) continue;

            switch (newLOD)
            {
                case LOD_MESH:
                    entry.MeshGO?.SetActive(true);
                    entry.BillGO?.SetActive(false);
                    break;
                case LOD_BILL:
                    entry.MeshGO?.SetActive(false);
                    entry.BillGO?.SetActive(true);
                    break;
                default:
                    entry.MeshGO?.SetActive(false);
                    entry.BillGO?.SetActive(false);
                    break;
            }

            var updated = entry;
            updated.LastLOD = newLOD;
            _trees[i] = updated;
        }
    }

    // ── Remove ────────────────────────────────────────────────────────────
    // Returns all tree GOs to the pool before destroying this chunk GO.

    public void Remove()
    {
        for (int i = 0; i < _trees.Count; i++)
        {
            var entry = _trees[i];
            VegetationPool.ReturnMesh(entry.Species, entry.MeshGO);
            VegetationPool.ReturnBill(entry.Species, entry.BillGO);
        }
        _trees.Clear();
        Destroy(gameObject);
    }
}
