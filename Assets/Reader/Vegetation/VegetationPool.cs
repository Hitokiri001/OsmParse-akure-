// VegetationPool.cs
// Static object pool for vegetation GameObjects.
// Survives chunk load/unload cycles — lives for the lifetime of the app.
//
// Two pools per species:
//   MeshPool[species]  — full-mesh prefab instances
//   BillPool[species]  — procedural billboard quad instances
//
// When a chunk unloads:
//   Trees are unparented, deactivated, pushed back to the pool.
//   The chunk GameObject itself is destroyed, but the tree GOs live on.
//
// When a chunk loads:
//   BuildCoroutine pops from the pool first.
//   If pool is empty, a new GO is created (first-time cost only).
//
// Pool cap (MaxPerSpecies):
//   Prevents unbounded memory if the player never revisits an area.
//   Excess GOs beyond the cap are destroyed normally.

using System.Collections.Generic;
using UnityEngine;

public static class VegetationPool
{
    /// Maximum pooled GOs per (species, type) pair.
    /// With LoadRadius=2 (~25 active chunks, ~500 trees each) 800 covers
    /// roughly 3–4 chunks worth of each type without wasting memory.
    public static int MaxPerSpecies = 800;

    // Keyed by species index — grow on demand as species slots are used
    static readonly List<Stack<GameObject>> _meshPools = new();
    static readonly List<Stack<GameObject>> _billPools = new();

    // Single container — all dormant pooled GOs live here, not at scene root
    static Transform _container;
    static Transform GetContainer()
    {
        if (_container != null) return _container;
        var go = new GameObject("[VegetationPool]");
        Object.DontDestroyOnLoad(go);
        _container = go.transform;
        return _container;
    }

    // ── Mesh pool ─────────────────────────────────────────────────────────

    /// Return a mesh GO for this species, or null if pool is empty.
    public static GameObject RentMesh(int species)
    {
        var pool = GetOrCreate(_meshPools, species);
        return pool.Count > 0 ? pool.Pop() : null;
    }

    /// Return a mesh GO to the pool. Unparents and deactivates it.
    /// Call this instead of Destroy when a chunk unloads.
    public static void ReturnMesh(int species, GameObject go)
    {
        if (go == null) return;
        var pool = GetOrCreate(_meshPools, species);
        if (pool.Count >= MaxPerSpecies)
        {
            Object.Destroy(go);
            return;
        }
        go.transform.SetParent(GetContainer(), false);
        go.SetActive(false);
        pool.Push(go);
    }

    // ── Billboard pool ────────────────────────────────────────────────────

    /// Return a billboard GO for this species, or null if pool is empty.
    public static GameObject RentBill(int species)
    {
        var pool = GetOrCreate(_billPools, species);
        return pool.Count > 0 ? pool.Pop() : null;
    }

    /// Return a billboard GO to the pool. Unparents and deactivates it.
    public static void ReturnBill(int species, GameObject go)
    {
        if (go == null) return;
        var pool = GetOrCreate(_billPools, species);
        if (pool.Count >= MaxPerSpecies)
        {
            Object.Destroy(go);
            return;
        }
        go.transform.SetParent(GetContainer(), false);
        go.SetActive(false);
        pool.Push(go);
    }

    // ── Stats (for debugging) ─────────────────────────────────────────────

    public static string GetStats()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Mathf.Max(_meshPools.Count, _billPools.Count); i++)
        {
            int m = i < _meshPools.Count ? _meshPools[i].Count : 0;
            int b = i < _billPools.Count ? _billPools[i].Count : 0;
            sb.Append($"Species{i}: mesh={m} bill={b}  ");
        }
        return sb.ToString();
    }

    /// Destroys all pooled GOs and clears the pool. Call on scene unload.
    public static void Clear()
    {
        foreach (var pool in _meshPools)
            while (pool.Count > 0) Object.Destroy(pool.Pop());
        foreach (var pool in _billPools)
            while (pool.Count > 0) Object.Destroy(pool.Pop());
        _meshPools.Clear();
        _billPools.Clear();
        _container = null;
    }

    // ── Private ───────────────────────────────────────────────────────────

    static Stack<GameObject> GetOrCreate(List<Stack<GameObject>> pools, int species)
    {
        while (pools.Count <= species)
            pools.Add(new Stack<GameObject>());
        return pools[species];
    }
}
