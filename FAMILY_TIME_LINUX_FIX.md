# Family Time (DX11 Patreon build) on Ubuntu 25.04 — Full Fix Report

**Machine:** Lenovo ThinkPad T490 · Intel WhiskeyLake-U GT2 / UHD Graphics 620 (Gen9, 24 EU)
**OS:** Ubuntu 25.04 · kernel `6.14.0-37-generic`
**Runtime:** Steam Proton 11.0 (DXVK → Vulkan → Mesa ANV → i915)
**Game:** closed Windows Unity build, DX11, shipped `FamilyTime.exe` + `Assembly-CSharp.dll`
**Result:** runs fullscreen at the panel's native 3072×1728, world generates, playable, no GPU hang.

---

## 1. Symptoms before the fix

| Symptom | What actually happened |
|---|---|
| "Nothing happens" on launch | Game process started, then died silently ~60–90 s in, before the world finished generating |
| Black / frozen window | GPU context was reset by the kernel; DXVK lost the device |
| Fullscreen never worked | Exclusive fullscreen request failed — the panel has no matching mode |
| Repeating failures | Same crash every run, at the same code point |

---

## 2. Root cause #1 — GPU hang (the "nothing happens" bug)

### Evidence

`dmesg`, every run:

```
i915 0000:00:02.0: [drm] Resetting rcs0 for preemption time out
i915 0000:00:02.0: [drm] GPU HANG: ecode 9:1:8ed9fff2, in FamilyTime.exe [315509]
i915 0000:00:02.0: [drm] FamilyTime.exe[...] context reset due to GPU hang
```

Game log at the same instant:

```
DxvkSubmissionQueue: Command submission failed: VK_ERROR_DEVICE_LOST
```

### Why

The game dispatches large GPU **compute** batches (terrain heightfield, prop chunk culling,
flow-field pathfinding — all `ComputeBuffer` based). On Gen9 i915 the driver enforces two
watchdog timers:

- `preempt_timeout_ms` = **640 ms** — how long a running batch may block preemption
- `request_timeout_ms` = **20 s** — how long a single submission may run

A Unity compute dispatch of this size can exceed 640 ms on 24 EUs. The kernel then
force-resets the engine (`rcs0`), the Vulkan device is lost, and Unity has no recovery path.

This is **not** a game bug and **not** a DXVK bug — it is the generic Linux watchdog being
tuned for desktop workloads, not long compute dispatches.

### Fix applied

Raise both timeouts instead of patching the game (patching would mean re-shipping shaders we
don't have, and would still lose the device on slower scenes).

`/etc/systemd/system/i915-long-compute.service`:

```ini
[Unit]
Description=i915 GPU timeouts large enough for Unity compute batches (Family Time)
After=multi-user.target

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/bin/sh -c "for e in /sys/class/drm/card*/engine/*/preempt_timeout_ms; do echo 30000 > $e 2>/dev/null; done; echo 120000 > /sys/module/i915/parameters/request_timeout_ms 2>/dev/null"

[Install]
WantedBy=multi-user.target
```

Installed with:

```sh
sudo systemctl enable --now i915-long-compute.service
```

### Result

- `preempt_timeout_ms` = **30000** on `rcs0`, `vcs0`, `bcs0`, `vecs0`
- `request_timeout_ms` = **120000**
- `grep -c 'GPU HANG' /var/log/kern.log` after the change → **0**
- `VK_ERROR_DEVICE_LOST` in game logs → **0**

### Revert

```sh
sudo systemctl disable --now i915-long-compute.service
sudo rm /etc/systemd/system/i915-long-compute.service
sudo systemctl daemon-reload
```

A reboot restores the driver defaults (640 ms / 20 s) by itself.

### What was tested and rejected

- `preempt_timeout_ms = 10000` **alone** (without the request timeout) → still reset, different
  ecode (`9:1:85dffefd`, failing in `wined3d_cs`). Both timers had to be raised.
- Patching the game to split compute dispatches → rejected: requires shader source we don't
  have, and is not reversible.

---

## 3. Root cause #2 — fullscreen (the "can't go fullscreen" bug)

### Evidence

Panel mode list (`xrandr`, eDP-1):

```
3072x1728  59.97*+   ← native
2048x1536, 1920x1440, 1600x1200, 1440x1080, 1400x1050, 1280x1024, 1024x768 ...
```

**No 1280×720 and no 1920×1080.** Exclusive fullscreen (`DXGI_MODE_SCANLINE_ORDER` /
`SetFullscreenState`) requires a mode that exactly matches the requested resolution, so every
attempt failed and the game fell back or died.

Decompiled `Assembly-CSharp.dll` (`GAT.Graphics`, `GraphicsMenu`, `AppliedSettings`):

```csharp
enum Fullscreen { Fullscreen = 0, Windowed = 1, Borderless = 2 }
// AppliedSettings:
SetResolution(w, h, FullscreenModes[Mathf.Clamp(mode, 0, 2)]);
```

And the Unity call maps `Borderless` → `FullScreenMode.FullScreenWindow` (a borderless window
covering the screen), which the compositor can scale — no matching mode needed.

### Fix applied

Set the game's own saved settings to **Borderless** with a 1280×720 backbuffer, in the Proton
prefix:

`~/.local/share/Steam/steamapps/compatdata/3259212083/pfx/user.reg`

```
"GAT.Graphics.FullscreenMode"=dword:00000002   ; 2 = Borderless
"GAT.Graphics.ResolutionWidth"=dword:00000500  ; 1280
"GAT.Graphics.ResolutionHeight"=dword:000002d0 ; 720
"GAT.Graphics.Quality"=dword:00000000
"GAT.Graphics.ViewDistance"=dword:00000000
"GAT.Graphics.Vsync"=dword:00000000
"Screenmanager Fullscreen mode_h3630240806"=dword:00000001  ; FullScreenWindow
```

Backup created before the edit:

```
compatdata/3259212083/pfx/user.reg.bak-fixscreen-20261109-072029
```

### Result

Measured window geometry (`wmctrl -l -G`):

```
0x04000003  0 0 0 3072 1728  soufiano  Family Time
```

Fullscreen at native resolution, `xrandr` mode **unchanged** (no mode switch, no flicker, no
broken desktop on exit). Verified that the requested 1280×720 / 640×480 only changes the
backbuffer — the window stays 3072×1728 and the compositor upscales. That keeps the GPU load
low on the UHD 620 while filling the screen.

### Revert

```sh
cp ~/.local/share/Steam/steamapps/compatdata/3259212083/pfx/user.reg.bak-fixscreen-20261109-072029 \
   ~/.local/share/Steam/steamapps/compatdata/3259212083/pfx/user.reg
```

Or simply change the option back in the in-game graphics menu.

---

## 4. Launcher without Steam

Steam cost ~350 MB of RAM before the game even started, on a machine where that matters
(see §7). The game now launches through Proton directly.

`~/.local/bin/family-time`:

```sh
#!/bin/sh
GAME_DIR="$HOME/Games/FamilyTime_Patreon-DX11-Build37"
PROTON="$HOME/.local/share/Steam/steamapps/common/Proton 11.0/proton"

export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.local/share/Steam"
export STEAM_COMPAT_DATA_PATH="$HOME/.local/share/Steam/steamapps/compatdata/3259212083"

cd "$GAME_DIR" || exit 1
exec "$PROTON" run FamilyTime.exe
```

`~/.local/share/applications/family-time.desktop` (+ copy on `~/Desktop`):

```ini
[Desktop Entry]
Type=Application
Version=1.0
Name=Family Time
Comment=Launch Family Time through Proton
Exec=$HOME/.local/bin/family-time
Path=$HOME/Games/FamilyTime_Patreon-DX11-Build37
Icon=$HOME/.local/share/icons/hicolor/512x512/apps/family-time.png
Terminal=false
Categories=Game;
StartupNotify=false
StartupWMClass=steam_proton
```

Icon: `~/.local/share/icons/hicolor/512x512/apps/family-time.png` — the official `game_icone.png`
center-cropped 1080×1080 → 512×512 (the original is 16:9 and letterboxes badly in a square slot).

### Two non-obvious details

- **`proton run` is required, not `runinprefix`.** The Steam prefix contains relative DLL
  symlinks (`kernel32.dll -> Proton 11.0/files/...`). `runinprefix` fails with
  `wine: could not load kernel32.dll, status c0000135`; `proton run` performs the prefix setup
  that repairs them.
- **Same prefix on purpose** (`compatdata/3259212083`), so saves and graphics settings are
  shared with the Steam launch. Don't run both at once — the prefix lock rejects the second.

### Revert

```sh
rm ~/.local/bin/family-time \
   ~/.local/share/applications/family-time.desktop \
   ~/Desktop/family-time.desktop \
   ~/.local/share/icons/hicolor/512x512/apps/family-time.png
```

---

## 5. Verification

| Check | Expected | Observed |
|---|---|---|
| Window geometry | 3072×1728 fullscreen | `3072 1728` at 0,0 |
| Display mode | unchanged | `xrandr` mode identical before/after |
| Kernel GPU hangs after fix | 0 | 0 |
| `VK_ERROR_DEVICE_LOST` | 0 | 0 |
| World generation | completes | `Generated terrain and 2 foliage layers with seed …` |
| Rendering | not black | frame capture mean 36827/65535 across channels |
| Gameplay | interactive | screenshot shows `Morning`, `3 Children`, tool bar, `Press F to Follow` |

Reproduction command used for testing:

```sh
STEAM_COMPAT_DATA_PATH=$HOME/.local/share/Steam/steamapps/compatdata/3259212083 \
  "$HOME/.local/share/Steam/steamapps/common/Proton 11.0/proton" run FamilyTime.exe
```

---

## 6. Approaches examined and rejected

| Idea | Why rejected |
|---|---|
| Compress GPU data / "ZRAM for the iGPU" | iGPU shares system RAM — no bus to save. Gen9 has no hardware decompressor for compute buffers, and decompression would run on the same 24 EUs. Doesn't address the preemption timeout. Needs shader source we don't have. |
| Patch the game to use smaller dispatches | Not reversible without source; same shaders still exceed the watchdog. |
| Exclusive fullscreen at 1280×720 | Panel exposes no such mode. Borderless is the only correct answer here. |
| Wayland (`PROTON_ENABLE_WAYLAND`) | Not supported by Proton 11.0 — no such variable. |
| Add RAM / rely on swap | Swap was already ~7 GiB of 7.6 GiB used. Swap cannot hold live GPU memory; the killer was the timeout, not allocation failure. |
| Lower in-game quality to fix the hang | Quality settings were lowered anyway (helpful), but with the timeout at 640 ms the hang reproduced regardless. |

---

## 7. Machine state notes

- Total RAM is the real headroom limit: during play, free memory measured **601–810 MB**, swap
  **~6.6–7.0 GiB of 7.6 GiB** in use. The game is playable, but the machine is close to the line.
  Closing the browser before playing measurably helps.
- Still using the **Steam runtime** — that's the supported path on this driver stack.
- Warnings that appear in the game log and are **harmless / not investigated further**:
  - `[LightRenderFeature] Texture format B10G11R11_UFloatPack32 is unsupported … using R16G16B16A16_SFloat` — normal Gen9 fallback.
  - `FloodPathRequest … has no flood group` and `CoarseSearch queue exceeded fail threshold of 56` — game-side AI warnings, non-fatal.
  - `Proton: Error while copying … amdxcffx64.dll` — cosmetic, the file doesn't exist in this Proton build.
  - `GarbageCollector disposing of ComputeBuffer` + `PropChunkManager.OnDestroy()` null-buffer exception when quitting **during world load** — a game-side teardown bug, not a launcher issue. Closing the game after loading avoids it.

---

## 8. Confirmed / inferred / unknown

**Confirmed (measured)**
- Preemption timeout at 640 ms caused the GPU reset; raising it to 30 s / 120 s eliminated it (0 hangs, 0 device-lost across multiple runs).
- The panel has no 1280×720 or 1920×1080 mode; Borderless gives fullscreen at 3072×1728 without a mode change.
- `proton run` works on the Steam prefix outside Steam; `runinprefix` does not.
- The launcher/desktop entry starts the game with no kernel32 error and no Steam process.

**Inferred**
- The specific compute batch that overruns 640 ms is terrain/flow-field generation — the last log line before each old crash was the heightfield/procedural map stage, and the crash moved past that point after the fix.
- 1280×720 backbuffer is what keeps the frame time inside budget on 24 EUs (it was chosen for headroom, not measured as a threshold).

**Unknown**
- Whether the remaining `FloodPathRequest` / `CoarseSearch` warnings affect AI behaviour over long sessions.
- Whether a future Proton or Mesa update changes the required timeout values — if hangs return, re-check `preempt_timeout_ms` first.

---

## 7. Root cause #3 — no trees, almost no props (session 4)

Full write-up with measurements: `PROP_FIX_REPORT.md`.

### Symptom

Terrain and grass render, but no trees at all and only a handful of rocks. Every managed gate in the prop render path is open (sessions 2–3), the instanced draw is issued, yet `propDrawArgs.instanceCount` stays 0.

### Evidence

The shipped `GenerateTrees` kernel was replayed layer by layer through the game's own `ComputeShader`, from a cleared atlas, with the game's own settings and seed:

| Replay | Result |
|---|---|
| tree layer alone | 142,463 cells with lane `65` (type 1 + collision 1) — correct |
| rock layer alone | 10,100 cells with lane `130` (type 2 + collision 2) — correct |
| tree then rock (the game's own order) | **0 tree cells**, 10,077 rocks, 35,626 cells stamped `0xFF` |

So the lane values, the prop registry (`propMeshes = [prop_tree01, prop_rock01]`), the seeds and the dispatch parameters are all right. **Every foliage pass rewrites the whole 2048² atlas**, so the second pass erases the first one's cells and stamps `0xFF` ("blocked") wherever it collides with one. Only the last foliage layer can ever survive — and `0xFF` is exactly the value that makes the save loader throw `Props section contains an unregistered type`.

Independently, `_PropTypeCounts` stays all zeros: recounting the entire ledger with `CountPropRegion` still produces zeros, so even a correct atlas never produces an instance.

### Fix applied

`linux-fix/` builds a companion assembly `PropFix.dll` and injects three calls into `Assembly-CSharp.dll` with Mono.Cecil:

1. `ProceduralMapManager.GenerateTrees` entry — capture the generation seed.
2. `PropChunkManager.LoadProceduralMap` entry — replay each foliage layer with the game's own kernel onto `generatedMap`, read each layer back, merge them (first layer wins, `0xFF` treated as empty), upload the merged atlas, and count lanes into `propTypeCounts` on the CPU.
3. `PropChunkManager.LateUpdate` — keep the CPU counts uploaded, since the shipped count kernel writes nothing.

```sh
linux-fix/build-propfix.sh            # build + install
linux-fix/build-propfix.sh --restore  # back to pristine
```

Pristine `Assembly-CSharp.dll` (md5 `a399271e0f0cdfad3df5c5526e526c45`) is kept at `linux-fix/pristine/Assembly-CSharp.dll` and verified before every install.

### Result

`[PropFix] merged 2 foliage layers: ~150000 props` at world generation, and trees render at the intended density at all LODs, together with rocks — in view: `n=10` / `n=135` (tree LOD0/LOD1) and `n=5` / `n=52` (rock LOD0/LOD1).

### Limits

- The merge costs two 16 MB readbacks and one upload, once per world generation (well under a second on the UHD 620).
- Props added or removed during play are not recounted; the shipped count kernel cannot count them either.
- The generation kernel still stamps `0xFF` where a later layer collides with an earlier one. The merge ignores those cells rather than trusting them, which is why layering works.

**Confirmed:** lane values, seed, prop registry and kernel parameters were all correct; the atlas was being overwritten by each successive foliage pass, and the type counts were never written.
**Inferred:** the `0xFF` stamp is the kernel's "already occupied" marker — it appears only when a second pass runs over an occupied cell, and it matches the save format's invalid-type value.
**Unknown:** where that marker was meant to be consumed; nothing in the shipped C# decodes it.
