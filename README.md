# family-time-linux-fix

Running the closed Windows DX11 Patreon build of **Family Time** on Ubuntu / Intel UHD 620 under Proton: the diagnosis and the patches that make it playable.

Three separate defects, all fixed in place and all reversible:

| # | Symptom | Cause | Fix |
|---|---|---|---|
| 1 | Game dies ~60–90 s in, "nothing happens" | GPU hang: i915 compute preemption timeout (640 ms) shorter than the game's heaviest compute batch | raised `preempt_timeout_ms` / `heartbeat_interval_ms` via `i915-long-compute.service` |
| 2 | Fullscreen never works | Exclusive fullscreen requested a mode the panel does not have | in-game Borderless (`GAT.Graphics.FullscreenMode=2`), 1280×720 backbuffer |
| 3 | No trees, almost no rocks | each foliage pass of the shipped tree kernel rewrites the whole 2048² prop atlas, so the rock layer erases the tree layer; and `_PropTypeCounts` is never written, so no instance is ever drawn | per-layer replay + readback merge + CPU type counts (`linux-fix/`) |

Plus a Steam-free launcher (`proton run` against the existing Steam prefix).

## Disclaimer

Unofficial and independent. Not affiliated with, authorised, sponsored or endorsed by the developer or publisher of Family Time. This repository contains **no game files** — no executables, assets, shaders, managed assemblies or save files — and distributes nothing that could substitute for owning the game. The patches modify the copy of the game you already have, on your own machine, at your own risk. Report problems with the patch here, not to the developer.

Only this repository's own code and text are licensed, under the [MIT license](LICENSE). "Family Time" and all game content remain the property of their respective owners.

## Documents

- [`FAMILY_TIME_LINUX_FIX.md`](FAMILY_TIME_LINUX_FIX.md) — full report for defects 1, 2 and the launcher, with evidence, commands and revert steps.
- [`PROP_FIX_REPORT.md`](PROP_FIX_REPORT.md) — full report for the procedural props fix: replay measurements, root cause, verification, limits.
- [`LEARNINGS.md`](LEARNINGS.md) — the traps, the IL/replay toolkit, and what did not work.

## The props fix

`linux-fix/` builds a companion assembly (`PropFix.dll`) and injects three calls into the shipped `Assembly-CSharp.dll` with Mono.Cecil:

1. `ProceduralMapManager.GenerateTrees` entry — capture the generation seed.
2. `PropChunkManager.LoadProceduralMap` entry — replay each foliage layer with the game's own compute kernel onto `generatedMap`, read each layer back, merge them (first layer wins, `0xFF` treated as empty), upload the merged atlas, and count lanes per type on the CPU.
3. `PropChunkManager.LateUpdate` — keep the CPU type counts uploaded, because the shipped `CountPropRegion` kernel writes nothing in this build.

Requires the .NET 8 SDK and a copy of the game (a .NET 8 SDK path is expected in `~/.dotnet` or `DOTNET_ROOT`).

```sh
# 1. put your own pristine backup where the build script can verify it
cp /path/to/untouched/Assembly-CSharp.dll linux-fix/pristine/Assembly-CSharp.dll
#    its md5 must be a399271e0f0cdfad3df5c5526e526c45

# 2. build and install into FamilyTime_Data/Managed
./linux-fix/build-propfix.sh

# 3. revert to the shipped assembly
./linux-fix/build-propfix.sh --restore
```

Expected log line at world generation, and trees at the intended density at every LOD:

```
[LOG] [PropFix] merged 2 foliage layers: ~150000 props
```

## What is not in this repository

No game content: no `FamilyTime.exe`, no `UnityPlayer.dll`, no `FamilyTime_Data/` (assets, shaders, managed assemblies), no `sharedassets*.assets`, no save files. All binaries stay in the local install; the pristine `Assembly-CSharp.dll` is not committed either, and the build script verifies its md5 before patching anything.

## Limits and open items

- Type counts are computed at load time only; props added or removed during play are not recounted.
- The merge costs two 16 MB readbacks and one upload per world generation (sub-second on the UHD 620); placement still happens on the GPU.
- Untouched: `[Singleplayer] Failed to find a walkable player spawn location` → the player starts at `Vector3.zero`, which occasionally puts the camera inside terrain or a treetop.
- Untouched: `FloodPathRequest` / `CoarseSearch` warnings; behaviour impact over long sessions is unmeasured.

This is interoperability work on software the author owns: no DRM is bypassed, nothing is distributed, and every change is a documented, reversible modification of a local install.
