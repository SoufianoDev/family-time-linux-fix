# Procedural props fix — trees now spawn and render

**Date:** 2026-11-10 (session 4)
**Target:** Family Time (DX11 Patreon build), closed Windows Unity build, run under Proton on Intel UHD 620
**Scope:** why the world generated terrain and grass but no trees, and what was changed in the game's own code
**Result:** ~150,000 props (≈140k trees, ≈10k rocks) merged into the prop atlas at generation, rendered at every LOD

---

## 1. Starting point

Earlier sessions had established:

- the managed prop render path (`DispatchRenderForCamera → DrawRenderResults → DrawMeshInstancedIndirect`) runs every frame and every gate in it is open;
- the prop atlas (`propLedger`, 2048² `R8G8B8A8_UNorm`) holds real prop cells, one byte per cell, `typeId & 63 | collision << 6`;
- `propDrawArgs.instanceCount` was nevertheless 0, so nothing drew.

That made the renderer look guilty. It was not. The two defects are both upstream, in generation and counting.

## 2. Instrumentation used

A companion assembly (`PropFix.dll`, netstandard2.1) with Mono.Cecil-injected calls into `Assembly-CSharp.dll`, plus a probe path that drives the game's own compute kernels through reflection:

- `Resources.FindObjectsOfTypeAll` → the live `ProceduralMapManager` and `PropChunkManager`;
- private `generateComputeShader`, `clearLedgerKernel`, `generateTreesKernel`, `generatedMap`, `settings`, `propMeshes`, `propLedger`, `propTypeCountsBuffer`;
- `World.heightfield.BindToComputeShader(shader, kernel)`;
- `AsyncGPUReadback` to read the atlas back, `Texture2D.SetPixels32` + `Graphics.CopyTexture` to write it.

No shader was disassembled or modified. Every conclusion below comes from observing what the game's own kernels write, not from reading DXBC.

## 3. Root cause A — each foliage pass rewrites the whole atlas

`ProceduralMapManager.GenerateTrees` clears `generatedMap` once, then dispatches `generateTreesKernel` once per foliage layer. Replaying exactly that, with the game's settings, seed and bindings, one layer at a time:

| Replay | Cells written | Lane value |
|---|---|---|
| tree layer alone (`prop_tree01`, density 0.1, sparsity 5, perlin 80) | 142,463 | `65` (type 1, collision 1) — correct |
| rock layer alone (`prop_rock01`, density 0.01, sparsity 7, perlin 4) | 10,100 | `130` (type 2, collision 2) — correct |
| tree layer, then rock layer (the game's order) | 0 trees, 10,077 rocks | 35,626 cells stamped `0xFF` |

The kernel **rewrites every cell on every pass**: cells the new layer does not claim are cleared to 0, and cells it collides with are stamped `0xFF`. Consequences:

- only the last foliage layer can ever survive, so the shipped build can never show trees;
- controlling lane values, seeds, `propMeshes` and dispatch parameters were all verified correct first (`[SEED] generationSeed`, `propMeshes.Count=2 [prop_tree01, prop_rock01]`, `GetProceduralPropLane` → tree `65`, rock `130`), so the bug was never in the lane arithmetic;
- `0xFF` is type 63, i.e. the same value that makes the save loader throw `InvalidDataException: Props section contains an unregistered type` — the two long-standing bugs share one cause.

## 4. Root cause B — the type counts are never written

`CountPropRegion` executes (a probe-invoked `ClearLedger` zeroed the atlas, proving the shader runs) but `propTypeCountsBuffer` stays all zeros, even when recounted over the whole ledger. `BuildPropRanges` therefore allocates no instances and `propDrawArgs.instanceCount` stays 0, no matter how correct the atlas is.

Proven by substitution: uploading CPU counts made draw args non-zero (`[4] n=1`, `[5] n=57`), i.e. the rest of the GPU chain — `BuildPropRanges → WriteProps → BuildPropDrawArgs → DrawMeshInstancedIndirect` — is functional.

## 5. Fix applied

`linux-fix/` builds `PropFix.dll` and injects three calls into `Assembly-CSharp.dll`:

| Hook | Method | What it does |
|---|---|---|
| 1 | `ProceduralMapManager.GenerateTrees` entry | captures the generation seed (`ldarg.1`) |
| 2 | `PropChunkManager.LoadProceduralMap` entry | replays each foliage layer with the game's own kernel onto `generatedMap`, reads each layer back (`AsyncGPUReadback … WaitForCompletion`), merges them (first layer wins, `0xFF` treated as empty), uploads the merged atlas, counts lanes per type on the CPU |
| 3 | `PropChunkManager.LateUpdate` | keeps the CPU type counts uploaded, because the shipped count kernel writes nothing |

Ordering detail: `GenerateTrees` calls `LoadProceduralMap` exactly once, after its own loop, so the hook replaces the atlas before the game copies it into `propLedger`. Save loading goes through `UploadLedger`/`pendingLedgerData` instead and does not regenerate, so existing saves are untouched.

Files:

```
linux-fix/
├── build-propfix.sh            build + install, or --restore
├── props/PropFix.cs            companion assembly (170 lines)
├── props/PropFix.csproj
├── patchtool/Program.cs        Mono.Cecil injector (47 lines)
├── patchtool/PatchTool.csproj
└── pristine/Assembly-CSharp.dll  md5 a399271e0f0cdfad3df5c5526e526c45
```

```sh
linux-fix/build-propfix.sh            # verify pristine, build, patch, install
linux-fix/build-propfix.sh --restore  # put the pristine DLL back, remove PropFix.dll
```

Both directions were run: restore yields `a399271e0f0cdfad3df5c5526e526c45`, install yields the patched `cee7425c291b204b56d56b6eafc9c21e`, and the installed build starts cleanly with no exceptions in the log.

## 6. Verification

Log, at world generation:

```
[LOG] [PropFix] merged 2 foliage layers: 150223 props
[LOG] [ProceduralMapManager] Generated terrain and 2 foliage layers with seed 2654.
```

Draw arguments measured in view after the fix (range = type × 2 + LOD):

| Range | Type | LOD | `instanceCount` |
|---|---|---|---|
| 2 | tree | 0 | 10 |
| 3 | tree | 1 | 135 → 44 |
| 4 | rock | 0 | 5 |
| 5 | rock | 1 | 52 |

Visual: trees at the intended density, near and far, with trunks, canopies and shadows, together with rocks — several screenshots across different random seeds and camera angles.

## 7. Limits

- The merge is CPU work over two 16 MB readbacks plus one upload, once per world generation; measured well under a second on the UHD 620. Placement still happens on the GPU — only the per-layer combine is on the CPU, because the kernel cannot be told to accumulate and no shader tooling is available here.
- Type counts are computed at load time only. Props added or removed during play are not recounted (`shortcut: load-time counts only; upgrade if gameplay prop edits visibly break draw ranges`).
- The kernel still stamps `0xFF` where a later layer collides with an earlier one. The merge ignores those cells rather than trusting them; that is what makes layering work.
- The walkable-spawn failure (`Failed to find a walkable player spawn location` → player at `Vector3.zero`) is a separate, untouched bug; it is why the camera sometimes starts inside terrain or a treetop.

## 8. Confidence

**Confirmed** (measured, reproducible): lane values, seed, prop registry and kernel parameters were correct; each foliage pass rewrites the whole atlas; `0xFF` appears only when a second pass runs over an occupied cell; `_PropTypeCounts` is never written; per-layer replay + merge + CPU counts restores trees and rocks and produces non-zero draw arguments.

**Inferred:** `0xFF` is the kernel's "already occupied" marker, matching the save format's invalid-type value; the game's original intent was per-layer accumulation.

**Unknown:** where that marker was meant to be consumed — nothing in the shipped C# decodes it; and why this shipped build's count kernel writes nothing (whether it is a shader compile/optimisation casualty or a bad binding).

## 9. Reproduce the investigation

1. Launch with the companion assembly present, hook `GenerateTrees` entry to log the seed and `propMeshes`, and hook `LoadProceduralMap` entry to read the atlas back.
2. From a probe, dispatch the game's own kernel after `clearLedgerKernel` with the tree layer's parameters and the tree lane, read back → `65`.
3. Dispatch the tree layer, then the rock layer, read back → no trees, 35,626 `0xFF`.
4. Dispatch tree with lane `130` → `130` everywhere it places: the lane input is not the variable.
5. Dispatch rock parameters with lane `65` (second pass) → `0xFF`: the trigger is the second pass, not the values.
