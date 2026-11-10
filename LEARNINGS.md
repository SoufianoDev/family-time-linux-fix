# LEARNINGS.md

Durable notes from the Family Time Linux / missing-trees investigation.
Rules distilled from this file live in `AGENTS.md` → "Working rules learned".

---

## 1. Where the trees investigation stands

**Objective:** procedural props (trees, rocks) never appear in the world.

### Confirmed

- **Not settings.** Max quality + max view distance (`QualityNames` = `normal` / `potato` / `epic`, index clamped to length−1): no props.
- **Not the renderer translation layer.** `PROTON_USE_WINED3D=1` (OpenGL path): no props. DXVK: no props.
- **Not the Proton/Wine version.** Proton 11.0 and Proton Experimental: no props.
- **The spawn data is present and sane.** `sharedassets0.assets` offset `13403684`, count = 2:
  - `prop_tree01` (pathID 1684) — perlinSize 80, density 0.1, sparsity 5, maxSlope 0.8
  - `prop_rock01` (pathID 1683) — perlinSize 4, density 0.01, sparsity 7, maxSlope 0.8
  Both PPtrs non-null; no `Foliage layer … is not registered by PropChunkManager` warning.
- **The patch target was the live asset.** The settings MonoBehaviour is `ProceduralMapSettings_default` (pathID 1588), referenced exactly once from `level0`. Patching `density=1.0` there gave an identical empty world.
- **The pipeline runs.** `DXVK_HUD` shows ~65 draw calls / 46 compute dispatches / 16 render passes per frame, zero errors in the log.
- **`GenerateTrees` cannot fail loudly.** It returns `true` unless the compute shader is missing or `EnsureGeneratedMap()` fails; a skipped layer just `continue`s; the summary log prints `foliage.Length`, not the count generated.
- **Generation input contract (from IL):** `SetInt(seed)`, `SetInt(sidePixels=2048)`, `SetInt(minX=-2048)`, `SetInt(minZ=-2048)`, `SetFloat(Clamp01(density))`, `SetInt(Clamp(sparsity,1,16))`, `SetInt(Clamp(perlinSize,1,256))`, `SetFloat(Clamp01(maxSlope))`, `SetInt(propLane)` → `Dispatch(256,256,1)` → `LoadProceduralMap(generatedMap)`.
- **Heightfield binding** (`Heightfield.SetHeightfieldUniforms`): `_HeightfieldTex`, `mapWidth`, `mapDepth`, `minX=-2048`, `minZ=-2048`, `maxY=127.5`.
- **Ledger contract:** `LoadProceduralMap` requires 2048×2048 and `graphicsFormat == 8` (R8G8B8A8_UNorm), then `Graphics.CopyTexture(generatedMap → propLedger)`, sets `ledgerReady`, then `ResetWindowState()`. `DispatchCountRegion` clamps the window to `[-2048, +2048]`, consistent with a 4096-unit world at 2 units per ledger pixel.

### Later findings (session 2)

- **Prop templates are healthy.** `prop_tree01` (1684) and `prop_rock01` (1683) are `GAT.EnvironmentMesh` ScriptableObjects (confirmed by resolving `m_Script` → MonoScript 2175 in `globalgamemanagers.assets`, class `EnvironmentMesh`). LODs decode clean: tree → `tree01_LOD0` (Mesh 198, 1314 indices, ~5 m tall) + Material 16 `tree01` (shader 218), second LOD `tree01_Card` (Mesh 160, billboard) at maxDistance 1000; rock → `rock01_lod0` (Mesh 177) + Material 15.
- **F5 = QuickSave** (`PlayerController.OnQuickSavePerformed`, map name hardcoded `test`), writing `<prefix>/drive_c/.../LocalLow/sgthale/Family Time/test.world`. The `props` section holds the **entire 2048² ledger**: `"PRP" + int32 typeCount + BinaryReader strings + int32 4194304 + 16777216 raw bytes`. This is the only way to read the ledger without a debugger.
- **The serialized ledger was 100 % zeros** at `density=1.0, sparsity=1` for both layers (16 777 216 zero bytes, 0/4 194 304 non-zero words).
- **At `density=1.0, sparsity=1` the game logs `[Singleplayer] Failed to find a walkable player spawn location.`** and then creates the player at `Vector3.zero` — the player cannot move at all. This is the "can't move" failure mode. The same warning appears in 2 of 20 older unpatched logs, so the spawn-finder failure is pre-existing; max density makes it deterministic.
- `Singleplayer.TryGetRandomWalkableSpawnPosition` samples terrain height and calls `NavManager.IsWalkableSurface`; on failure it uses `Vector3.zero`, so the player is placed at the origin inside the world.
- Compute shader `ProceduralMapManager_procedural` (pathID 247) kernels: `ClearLedger` (binds `_ProceduralMap`), `GenerateTrees` (binds `_HeightfieldTex`, `_ProceduralMap`), `SetWater`/`SettleWater`/`PruneWater` (`_ProceduralWaterLedger`). Its `$Globals` cbuffer members are exactly the ones the C# sets (`_ProceduralMapSidePixels`, `MinX`, `MinZ`, `Seed`, `Density`, `Sparsity`, `PerlinSize`, `MaxSlope`, `PropLane`), so no unset uniform is the cause.

### Contradiction to resolve

The save says the ledger is empty, while max density makes the spawn finder declare the whole map unwalkable (which requires props in the ledger/collision path). Either the save's GPU→CPU copy (`copyLedgerToBufferKernel` + `AsyncGPUReadback` in `EnsureLedgerForSerialization`) is broken, or the spawn failure is terrain-driven and unrelated to props. Test that separates them: compare a dense-props run's `heightfield` section against its `props` section, or read `propTypeCountsBuffer` via the same readback that serialization uses.

### Inferred

- The prop **ledger ends up empty** (or holds no props in the counted window). Both the visual and the mobility signal agree: at `density=1.0` the player still walks freely.
- The break is inside the `generateTreesKernel` dispatch result, not in the draw path — counts, ranges, args and draws are all downstream of the ledger.

### Unknown

- Whether the kernel writes nothing, writes at a bad height (buried), or the copy to the ledger doesn't land.
- Whether the same build has trees on native Windows. The shipped logs are the author's own (`C:/Users/Master-Donut/…`, RTX 2070 SUPER) and share the same message set as ours, so they prove nothing either way.

### Next step (not done)

Read the actual `GenerateTrees` shader. Routes checked: no `dxc`, no `vkd3d-compiler`, no `apitrace`, no `renderdoc` available; `spirv-tools` is the only installable option. Viable path is `PROTON_USE_WINED3D=1` + `MESA_GLSL=dump` / `MESA_SHADER_DUMP_PATH` to capture the GLSL that wined3d generates.

---

## 2. Toolchain: reading IL with no .NET runtime

No `dotnet`, `mono`, `mcs`, `csc`, `ilspycmd` on this machine, and Proton ships no wine-mono. `dnfile` + `dncil` (pure Python, `pip install dnfile dncil`) are enough.

ICSharpCode-style tools would be nicer, but the two-library route works and patches are still possible byte-for-byte: `pe.get_offset_from_rva(row.Rva)` gives the method body file offset.

### ilkit.py — restore this file if `/tmp` is gone

```python
#!/usr/bin/env python3
"""Inspect Assembly-CSharp.dll IL. No .NET runtime required.

  ILKIT_DLL=path/to/Assembly-CSharp.dll ilkit.py dump    "Type::Method" [more...]
  ILKIT_DLL=...                            ilkit.py callers "Type::Method"
  ILKIT_DLL=...                            ilkit.py findstr "literal text"

Requires: pip install dnfile dncil
"""
import os
import sys

import dnfile
from dncil.cil.body import CilMethodBody
from dncil.cil.body.reader import CilMethodBodyReaderBase

DLL = os.environ.get("ILKIT_DLL", "FamilyTime_Data/Managed/Assembly-CSharp.dll")


class Reader(CilMethodBodyReaderBase):
    def __init__(self, data):
        self.data, self.offset = data, 0

    def read(self, n):
        b = self.data[self.offset:self.offset + n]
        self.offset += n
        return b

    def tell(self):
        return self.offset

    def seek(self, off):
        self.offset = off
        return self.offset


def hstr(x):
    """HeapItemString | bytes | str -> str."""
    v = getattr(x, "value", x)
    if isinstance(v, bytes):
        return v.decode("utf-8", "replace")
    return v or ""


class Meta:
    def __init__(self, pe):
        self.pe = pe
        t = pe.net.mdtables
        self.types = list(t.TypeDef.rows)
        self.methods = list(t.MethodDef.rows)
        self.fields = list(t.Field.rows)
        self.mrefs = list(t.MemberRef.rows)
        self.trefs = list(t.TypeRef.rows)
        self.mowner, self.fowner = {}, {}
        for row in self.types:
            tn = self.typename(row)
            for m in (row.MethodList or []):
                self.mowner[m.row_index] = tn      # MDTableIndex, not a row
            for f in (row.FieldList or []):
                self.fowner[f.row_index] = tn

    def typename(self, row):
        ns, nm = hstr(row.TypeNamespace), hstr(row.TypeName)
        return f"{ns}.{nm}" if ns else nm

    def method(self, rid):
        return f"{self.mowner.get(rid, '?')}::{hstr(self.methods[rid - 1].Name)}"

    def field(self, rid):
        return f"{self.fowner.get(rid, '?')}::{hstr(self.fields[rid - 1].Name)}"

    def sym(self, tok):
        tbl, rid = (tok >> 24) & 0xFF, tok & 0xFFFFFF
        if tbl == 0x06:            # MethodDef
            return self.method(rid)
        if tbl == 0x04:            # Field
            return self.field(rid)
        if tbl == 0x0A:            # MemberRef
            r = self.mrefs[rid - 1]
            return f"{self.typename(r.Class.row)}::{hstr(r.Name)}"
        if tbl == 0x01:            # TypeRef
            return self.typename(self.trefs[rid - 1])
        if tbl == 0x02:            # TypeDef
            return self.typename(self.types[rid - 1])
        if tbl == 0x70:            # UserString
            try:
                v = self.pe.net.user_strings.get(rid)
            except Exception:
                return f"<us {tok:06x}>"
            return repr(v.decode("utf-16-le", "replace")) if isinstance(v, bytes) else repr(str(v))
        return f"<tok {tok:06x}>"


def bodies(pe):
    """Yield (method_rid, CilMethodBody) for every method with a body."""
    for rid, row in enumerate(pe.net.mdtables.MethodDef.rows, 1):
        if not row.Rva:
            continue
        try:
            off = pe.get_offset_from_rva(row.Rva)
            yield rid, CilMethodBody(Reader(pe.__data__[off:off + 65536]))
        except Exception:
            continue


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return
    mode, args = sys.argv[1], sys.argv[2:]
    pe = dnfile.dnPE(DLL)
    meta = Meta(pe)
    names = {rid: meta.method(rid) for rid in range(1, len(meta.methods) + 1)}

    if mode == "dump":
        for rid, body in bodies(pe):
            full = names[rid]
            if not any(p in full for p in args):
                continue
            print(f"\n===== {full}  (rva=0x{meta.methods[rid - 1].Rva:x} rid={rid})")
            for ins in body.instructions:
                op, oper = ins.opcode.name, ins.operand
                iv = oper if isinstance(oper, int) else getattr(oper, "value", None)
                if isinstance(iv, int) and op not in ("ldc.i4", "ldc.i4.s", "ldc.i4.m1"):
                    oper = meta.sym(iv) if (iv >> 24) else iv
                # dncil offsets include the header but are self-consistent; do not
                # subtract 12 blind (tiny header = 1 byte, fat header = 12 bytes).
                print(f"  IL_{ins.offset:04x}: {op:<12} {oper if oper is not None else ''}")
        return

    if mode == "callers":
        want = {rid for rid, n in names.items() if args[0] in n}
        print("targets:", [(rid, names[rid]) for rid in sorted(want)])
        for rid, body in bodies(pe):
            for ins in body.instructions:
                if ins.opcode.name not in ("call", "callvirt", "newobj", "ldftn"):
                    continue
                tok = ins.operand if isinstance(ins.operand, int) else getattr(ins.operand, "value", None)
                if isinstance(tok, int) and ((tok >> 24) & 0xFF) in (0x06, 0x2B) and (tok & 0xFFFFFF) in want:
                    print(f"  caller {names[rid]}  (IL_{ins.offset:04x})")
        return

    if mode == "findstr":
        for rid, body in bodies(pe):
            for ins in body.instructions:
                if ins.opcode.name != "ldstr":
                    continue
                tok = ins.operand if isinstance(ins.operand, int) else getattr(ins.operand, "value", None)
                if not isinstance(tok, int):
                    continue
                txt = meta.sym(tok)
                if args[0] in txt:
                    print(f"{names[rid]}  IL_{ins.offset:04x}  {txt}")
        return

    print(__doc__)


if __name__ == "__main__":
    main()
```

---

## 3. dnfile / dncil traps

| Symptom | Cause | Fix |
|---|---|---|
| `AttributeError: rid` | `MDTableRow` has no `.rid` | rid = `enumerate(rows, 1)` position |
| `AttributeError: 'MDTableIndex' object has no attribute 'Name'` | `TypeDef.MethodList` / `.FieldList` return `MDTableIndex` | use `f.row_index` / `f.row.Name` |
| `TypeError: '>=' not supported between HeapItemString and int` | `row.Name` is already heap text | `getattr(x, "value", x)` before `strings.get()` |
| Symbols resolve to plausible but wrong names | wrong dtok table mapping | see table below |
| Offsets look shifted (`IL_-00b`) | dncil offsets are body-absolute and self-consistent: tiny header = 1 byte, fat header = 12 | do **not** blanket-subtract 12; for a file offset use `pe.get_offset_from_rva(row.Rva) + ins.offset` |
| `%d format: a real number is required, not Token` | `body.local_var_sig_tok` is a Token | don't format it with `%d` |

**dtok table numbers** (the one that bit me):

| high byte | table |
|---|---|
| `0x01` | TypeRef |
| `0x02` | TypeDef |
| `0x04` | **Field** |
| `0x06` | **MethodDef** |
| `0x0A` | **MemberRef** |
| `0x1B` | TypeSpec |
| `0x2B` | MethodSpec |
| `0x70` | UserString |

I initially had `0x06` = MemberRef and `0x0A` = MethodDef; the dump was full of real-looking wrong names (`DynamicBonePlaneCollider::Collide` where `Object.op_Equality` belonged). Wrong symbols do not look wrong — sanity-check one known call.

---

## 4. Unity asset recipes (stripped type trees)

**MonoBehaviour header layout** (stable, no typetree needed):

```
PPtr m_GameObject : int32 fileID + int64 pathID   (12)
u8   m_Enabled    + 3 pad                          (4)
PPtr m_Script     : int32 fileID + int64 pathID   (12)
int32 m_Name length, then UTF-8 name
```

**Property-like serialized records** are plain: `PPtr (12)` then the fields in declaration order. `FoliageSetting` is 28 bytes: `PPtr tree`, `int32 perlinSize`, `float density`, `int32 sparsity`, `float maxSlope`.

**Find the live object before patching:**

```python
pat = b"\x00\x00\x00\x00" + struct.pack("<q", path_id)
d.count(pat)                                   # must be 1 in the assets file
struct.pack("<q", path_id) in open("level0","rb")   # scene must reference it
```

**Verify restored state:** `md5sum` the asset against the pristine copy. `sharedassets0.assets` original is `3ab2197244ba3ced4d6e779a3653ed7c`.

**Split responsibility:** REA (`inspect_managed_members`) gives types, fields, call edges and opcode counts quickly; local `dnfile` gives exact instruction order. REA still emits coded indices like `class:348` — resolve them yourself (`348 >> 2` = TypeDef RID 87) instead of guessing a type name.

---

## 5. Two-signal testing for "content never spawns"

Visuals alone are ambiguous: a prop can exist but be invisible, buried, or culled. Patch the source data to an extreme, then read a second, independent signal.

- Collision / mobility: at `density=1.0` the player still walked freely → the ledger holds no props at all.
- Check the clamp range first: `Mathf.Clamp(sparsity, 1, 16)` means `sparsity=0` is silently coerced to `1`.
- Cross-check the pipeline is alive with `DXVK_HUD=drawcalls,gpuload`: dispatches running proves nothing about output.

---

## 6. Log forensics

Normalize, then compare machines:

```sh
norm() { sed -E 's/^[0-9]+://' "$1" \
  | sed -E 's/\(-?[0-9]+[^)]*\)/(X)/g; s/[-0-9]+\.[0-9]+/N/g; s/[0-9]{2,}/N/g'; }
norm dev.log | sort -u > a; norm ours.log | sort -u > b
comm -23 a b   # only in dev
comm -13 a b   # only in ours
```

Findings:

- `FamilyTime_Data/Logs/` ships the **author's own Windows runs** — `C:/Users/Master-Donut/Project Family/Builds/FamilyTime_Patreon-DX11/`, `NVIDIA GeForce RTX 2070 SUPER`. The GPU string is the tell.
- The only real message difference between his run and ours is `NotSupportedException: … GraphicsFence … does not support async compute` at `GAT.BufferRelayoutUtil.Update()`, 4 438 times in his session, never in ours.
- That exception fires when `SystemInfo.supportsAsyncCompute == false`. **Wine reports the capability differently**, so "no exception here" means "different code path", not "healthy".
- Known harmless noise: `FloodPathRequest … has no flood group`, `CoarseSearch queue exceeded fail threshold`, `Proton: Error while copying … amdxcffx64.dll`, `GarbageCollector disposing of ComputeBuffer`, `ArgumentNullException … AsyncGPUReadback` in `PropChunkManager.OnDestroy`.

---

## 7. Environment facts

| Item | Value |
|---|---|
| Prefix | `~/.local/share/Steam/steamapps/compatdata/3259212083` |
| Proton | `common/Proton 11.0`, `common/Proton - Experimental` |
| Launcher | `~/.local/bin/family-time` (same prefix, `proton run`, never Steam) |
| Settings | `GAT.Graphics.*` in `pfx/user.reg` (`FullscreenMode=2` borderless) |
| Panel | eDP-1, 3072×1728 only — exclusive fullscreen impossible |
| Installed matchers | `spirv-tools` only; no `apitrace`, `renderdoc`, `dxc`, `glslangValidator` |
| Python venv | `/tmp/rev2` (ephemeral) — `UnityPy`, `dnfile`, `dncil` |

Reproduce a run:

```sh
STEAM_COMPAT_CLIENT_INSTALL_PATH=$HOME/.local/share/Steam \
STEAM_COMPAT_DATA_PATH=$HOME/.local/share/Steam/steamapps/compatdata/3259212083 \
"$HOME/.local/share/Steam/steamapps/common/Proton 11.0/proton" run FamilyTime.exe
```

First launch needs 60–90 s of world generation. `pkill -x FamilyTime.exe`, never `pkill -f`.

---

## 7. Prop pipeline: what is confirmed (session 3)

**A prop is one cell per 32-bit word.** In the `props` section a word is `R | G<<8 | B<<16 | A<<24`: R holds the packed cell (`typeId & 63 | collisionType << 6`), G/B stay 0, **A is 255 everywhere** (the kernel writes alpha 1.0). Treating all four bytes as cells reports ~25% occupancy — that number is alpha. The real count is the R channel only.

**Generation is a backend property.** Same pristine install, only the D3D11 backend changed:
- DXVK: **0** prop cells — and the 262,108-cell ledger injected through the game's own loader re-saves as 0.
- `PROTON_USE_WINED3D=1`: **37,564** cells — 35,574 tree `0x41`, 1,990 rock `0x82`, uniform over the 2048² grid, nearest one ~4.5 cells from the player.

**Nothing is ever drawn, in either backend.** A world holding 37,564 props (one ~9 m from the player) renders identically to a world holding 0. So the draw path has its own gate, independent of ledger content.

**Ruled out by direct evidence, not reasoning:**
- `AreMapFormatsSupported` — logs `Graphics format {0} does not support sampled random-write maps.`; never appears.
- Window size / chunk config — logs on failure; never appears. Scene `CameraChunkChangeListener`s are radius 1.5–6.0, chunkSize 8–128 → a 512–1024 cell window.
- Component disabled — `PropChunkManager` is scene object 562 in `level0`, `m_Enabled = 1`.
- The silent `cameraChunkListener == null || !HasCurrentCameraChunk` return — its `ldc.i4.0; ret` was patched into the method's own `LogError`; no line appeared, so that gate passes.
- `renderStates` being empty — `EnsureRenderStates` builds one state per `propMeshes` entry, and lanes 1/2 were generated, so `propMeshes` is populated.

**IL patching of the shipped assembly works without any compiler.** `TryInitializeWindow` (RVA 0x51a20) body starts at file offset **0x4fc20** (12-byte fat header), i.e. `file = 0x4fc20 + IL_offset`. Two bytes are enough to redirect a silent `ret` to an in-method `LogError`. Backup: `/tmp/opencode/Assembly-CSharp.dll.orig`, md5 `a399271e0f0cdfad3df5c5526e526c45`. This is the tool to reach for when a gate is silent: make it speak, don't guess.

**Real, independent game bug:** loading any save that contains props throws
`System.IO.InvalidDataException: Props section contains an unregistered type.`
(quick load of `test.world`, written by the same build one second earlier). `ReadSerializedLedger` masks every byte of every word into a cell, so the serialized alpha byte 255 → 63 > `propMeshes.Count`. Worlds with trees can be saved, never loaded.

**wined3d capability gap in the log:** `GpuFence::Create(): Failed to create ID3D11Fence, error 0x80004001` (E_NOTIMPL); `DispatchRenderForCamera` creates fences with `CommandBuffer.CreateGraphicsFence`.

**Where this leaves it:** the game's logic is intact; what fails is the D3D11 feature path (UAV storage texture + GPU-driven prop render) as implemented by both translation layers. A game-side fix would have to replace that path in the compute shaders, and this box has no DXBC/shader assembler (`dxc`, `fxc`, `glslangValidator` absent; apt offers only `spirv-tools`).

## 8. The prop draw path is not gated in C# (session 3, probes)

**Correction to the IL toolkit rule:** dncil prints *absolute* branch targets, but the bytes on disk encode them **relative to the end of the branch**. Example, `DrawRenderResults` IL_0045 is `2c 0c` (brfalse.s → 0x45+2+0x0c = 0x53), not `2c 53`. Read the operand out of the file, never from the dump.

**Probes and what they proved** (all under `PROTON_USE_WINED3D=1`, pristine assets, one reversible edit each, DLL restored to `a399271e0f0cdfad3df5c5526e526c45` afterwards):

1. Removing branches leaves the evaluation stack unbalanced — Mono rejects the method with `InvalidProgramException: Invalid IL code ... IL_00xx`. To neutralise a `brfalse`/`bgt` guard, replace it with `pop` (0x26), not `nop`; a 2-pop guard needs two `pop`s. This accidentally confirmed the stack trace channel: the exception names the class, method and IL offset.
2. That stack trace showed `RenderForCamera → DrawRenderResults` at RenderForCamera IL_004e, so `windowReady`, the ledger `TextureHandle`, and the null checks all pass.
3. Neutralising all three `DrawRenderResults` guards (null output pair, null args pair, `propRenderMaxLodCount <= 0`) produced **no exception and no visual change**.
4. Neutralising the single `ret` that all four `DispatchRenderForCamera` guards jump to (render shader null, the three kernel indices `< 0`, `renderStates.Count == 0`) produced **no exception and no visual change**.

Conclusion: every managed gate in the prop render path is **open**. The instanced draw is issued; what comes out of the GPU-side cull (`buildRenderRangesKernel` → `writeRenderKernel` → `buildDrawArgsKernel`, args written into `propDrawArgsPair`) must be zero. Further IL gate-hunting cannot help; the counts have to be read back from the GPU.

**Scene data confirmed by decoding `level0` object 562** (the live `PropChunkManager`): `propMeshes = [PPtr(2,1684) prop_tree01, PPtr(2,1683) prop_rock01]`, `cameraChunkListener = object 665` (radius 6.0, chunkSize 64 → 768-cell window), map/collision/render compute shaders all referenced. The draw list is not empty.

**Tooling note:** `/tmp/opencode`, `/tmp/rev2` (dnfile/UnityPy venv) and every backup under `/tmp` were lost to a host restart. Rebuild the venv before the next session; the pristine `Assembly-CSharp.dll` md5 is the only state that matters and it verifies.

## 9. Trees never spawned: the atlas is rewritten per foliage layer (session 4)

**The bug was in generation, not rendering.** Sessions 2–3 chased the render path because `propDrawArgs.instanceCount` was 0; that was a *downstream symptom*. Two independent defects upstream:

1. **Each foliage pass rewrites the whole 2048² prop atlas.** Replaying the game's own `GenerateTrees` kernel layer by layer, from a cleared atlas, with the game's settings and seed, gives: tree layer alone → 142,463 cells at lane `65` (correct); rock layer alone → 10,100 cells at lane `130` (correct); tree then rock (the shipped order) → **zero tree cells**, 10,077 rocks, and 35,626 cells stamped `0xFF`. So only the last layer can survive, and the `0xFF` cells are the second pass's "already occupied" marks — the same value that makes the save loader throw `Props section contains an unregistered type`.
2. **`_PropTypeCounts` is never written.** Recounting the entire ledger with `CountPropRegion` still yields zeros, so even a correct atlas produces no instances.

**How the diagnosis was made — replay, don't reason.** The decisive tool was dispatching the game's own compute kernel through reflection (`Resources.FindObjectsOfTypeAll` → private `generateComputeShader` / `generateTreesKernel` / `generatedMap`, plus `World.heightfield.BindToComputeShader`) with one input changed per run, then reading the atlas back. Six dispatches in one session replaced hours of theorising about lanes, textures and LODs. Guessing at lane arithmetic (65 vs 130 vs 193) from the values alone produced four wrong hypotheses; one controlled re-dispatch produced the answer.

**Traps confirmed again:**
- `Debug.LogError` from the companion assembly prints a stack trace per call — useful during probing, noisy in a patch. The game's own `[LOG]` lines prove `Debug.Log` is *not* stripped, so the final patch uses it.
- `GetProceduralPropLane` lives on `PropChunkManager`, not `ProceduralMapManager`; `GetMethod` on the wrong type returns null silently and the fix no-ops.
- `LoadProceduralMap` has exactly one caller (`GenerateTrees`), so hooking its entry is safe; save loading goes through `UploadLedger`/`pendingLedgerData` instead and does not regenerate.
- `AsyncGPUReadbackRequest.WaitForCompletion()` works on the main thread here, which makes the per-layer merge possible inside the generation call at all.

**Result:** per-layer replay + readback merge + CPU type counts (`linux-fix/`) restores ~140k trees and ~10k rocks, all LODs visible, `instanceCount` non-zero. Reversible with `linux-fix/build-propfix.sh --restore`.
