using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

// Family Time procedural props fix.
//
// 1) Foliage generation: every foliage pass of the shipped tree kernel rewrites the
//    whole 2048^2 atlas, so the last layer (rocks) erases the earlier one (trees) and
//    marks the cells it collided with as 0xFF ("blocked"). Replayed here one layer at
//    a time with the game's own kernel, merging the results, which is what the game's
//    loop was trying to do.
// 2) Prop type counts: the shipped CountPropRegion kernel leaves _PropTypeCounts at
//    zero, so no instance is ever written. Counts are computed from the merged atlas
//    on the CPU instead.
public static class PropFix
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    static uint[] counts;
    static int seed;
    static ComputeBuffer countsBuffer;

    public static void Seed(int value) { seed = value; }

    public static void Rebuild(RenderTexture map)
    {
        try
        {
            if (map == null || !map.IsCreated() || map.width != 2048 || map.height != 2048) return;

            var pmmT = FindType("GAT.ProceduralMapManager");
            var worldT = FindType("GAT.World");
            if (pmmT == null || worldT == null) return;
            var managers = Resources.FindObjectsOfTypeAll(pmmT);
            if (managers == null || managers.Length == 0) { Debug.LogError("[PropFix] no ProceduralMapManager"); return; }
            object pmm = managers[0];

            var shader = (ComputeShader)Get(pmm, "generateComputeShader");
            object settings = Get(pmm, "settings");
            var foliage = settings == null ? null : (Array)Get(settings, "foliage");
            var heightfield = worldT.GetProperty("heightfield", F).GetValue(null);
            if (shader == null || foliage == null || heightfield == null)
            {
                Debug.LogError("[PropFix] generation inputs missing; leaving the map as generated");
                return;
            }
            int clearKernel = (int)Get(pmm, "clearLedgerKernel");
            int treeKernel = (int)Get(pmm, "generateTreesKernel");
            var pcm = worldT.GetProperty("propChunkManager", F).GetValue(null);
            var laneOf = pcm.GetType().GetMethod("GetProceduralPropLane", F);
            var bind = heightfield.GetType().GetMethod("BindToComputeShader", F);
            if (clearKernel < 0 || treeKernel < 0 || laneOf == null || bind == null) return;

            Prepare(shader, clearKernel, map);
            var merged = new Color32[map.width * map.height];
            int layers = 0;
            for (int i = 0; i < foliage.Length; i++)
            {
                object layer = foliage.GetValue(i);
                if (layer == null) continue;
                object tree = Get(layer, "tree");
                if (tree == null) continue;
                uint lane = (uint)laneOf.Invoke(pcm, new object[] { tree });
                if (lane == 0)
                {
                    Debug.LogWarning("[PropFix] foliage layer " + i + " is not registered by PropChunkManager");
                    continue;
                }

                bind.Invoke(heightfield, new object[] { shader, treeKernel });
                shader.SetTexture(treeKernel, "_ProceduralMap", map);
                shader.SetInt("_ProceduralMapSidePixels", 2048);
                shader.SetInt("_ProceduralMapMinX", -2048);
                shader.SetInt("_ProceduralMapMinZ", -2048);
                shader.SetInt("_ProceduralMapSeed", seed);
                shader.SetFloat("_ProceduralMapDensity", Mathf.Clamp01(Convert.ToSingle(Get(layer, "density"))));
                shader.SetInt("_ProceduralMapSparsity", Mathf.Clamp(Convert.ToInt32(Get(layer, "sparsity")), 1, 16));
                shader.SetInt("_ProceduralMapPerlinSize", Mathf.Clamp(Convert.ToInt32(Get(layer, "perlinSize")), 1, 256));
                shader.SetFloat("_ProceduralMapMaxSlope", Mathf.Clamp01(Convert.ToSingle(Get(layer, "maxSlope"))));
                shader.SetInt("_ProceduralMapPropLane", (int)lane);
                shader.Dispatch(treeKernel, 256, 256, 1);

                var req = AsyncGPUReadback.Request(map, 0, TextureFormat.RGBA32);
                req.WaitForCompletion();
                if (req.hasError) { Debug.LogError("[PropFix] readback failed for layer " + i); continue; }
                var src = req.GetData<Color32>();
                for (int p = 0; p < merged.Length; p++)
                {
                    Color32 s = src[p], d = merged[p];
                    if (d.r == 0 && s.r != 0 && s.r != 255) d.r = s.r;
                    if (d.g == 0 && s.g != 0 && s.g != 255) d.g = s.g;
                    if (d.b == 0 && s.b != 0 && s.b != 255) d.b = s.b;
                    if (d.a == 0 && s.a != 0 && s.a != 255) d.a = s.a;
                    merged[p] = d;
                }
                layers++;
            }
            if (layers == 0) { Debug.LogError("[PropFix] no foliage layer generated"); return; }

            var tex = new Texture2D(map.width, map.height, TextureFormat.RGBA32, false, false);
            tex.SetPixels32(merged);
            tex.Apply(false);
            Graphics.CopyTexture(tex, map);
            UnityEngine.Object.Destroy(tex);

            var c = new uint[64];
            for (int p = 0; p < merged.Length; p++)
            {
                Count(c, merged[p].r); Count(c, merged[p].g); Count(c, merged[p].b); Count(c, merged[p].a);
            }
            counts = c;
            Debug.Log("[PropFix] merged " + layers + " foliage layers: " + Cells(c) + " props");
        }
        catch (Exception e) { Debug.LogError("[PropFix] rebuild failed: " + e.GetType().Name + " " + e.Message); }
    }

    // The count kernel writes nothing in this build, so keep the CPU counts uploaded.
    public static void Tick(object mgr)
    {
        if (counts == null) return;
        try
        {
            if (countsBuffer == null) countsBuffer = (ComputeBuffer)mgr.GetType().GetField("propTypeCountsBuffer", F).GetValue(mgr);
            if (countsBuffer == null || countsBuffer.count != 64 || !countsBuffer.IsValid()) { countsBuffer = null; return; }
            countsBuffer.SetData(counts);
        }
        catch (Exception e) { Debug.LogError("[PropFix] counts upload failed: " + e.GetType().Name + " " + e.Message); countsBuffer = null; }
    }

    static void Prepare(ComputeShader shader, int clearKernel, RenderTexture map)
    {
        shader.SetTexture(clearKernel, "_ProceduralMap", map);
        shader.SetInt("_ProceduralMapSidePixels", 2048);
        shader.Dispatch(clearKernel, 256, 256, 1);
    }

    static void Count(uint[] c, byte lane)
    {
        int type = lane & 0x3F;
        if (type == 0 || type > 62) return;
        c[type]++;
    }

    static int Cells(uint[] c)
    {
        int n = 0;
        for (int i = 0; i < c.Length; i++) n += (int)c[i];
        return n;
    }

    static Type FindType(string full)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType(full);
            if (t != null) return t;
        }
        return null;
    }

    static object Get(object o, string n)
    {
        var t = o.GetType();
        var f = t.GetField(n, F);
        if (f != null) return f.GetValue(o);
        var p = t.GetProperty(n, F);
        return p == null ? null : p.GetValue(o);
    }
}
