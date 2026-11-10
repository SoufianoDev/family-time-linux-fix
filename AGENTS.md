# AGENTS.md

Always load and follow these skills for work in this project:

1. `reverse-engineer-anything` — evidence-first analysis of shipped binaries (this game is a closed Windows Unity build; ask the binary, not guesses).
2. `ponytail` at **ultra** level — smallest complete change, question the request, no unrequested abstractions. End each reply with what was skipped and the risk.

## Project rules

- Treat the game install as read-only. All experiments happen in a copy under `/tmp/opencode/ft-work`.
- Every patch must be reversible; keep the pristine original.
- No DRM bypass, no random DLLs, no redistribution of proprietary assets.
- Distinguish confirmed findings, inferences, and unknowns in reports.

## Working rules learned

Details, traps and the IL toolkit live in `LEARNINGS.md`.

1. **A success log is not evidence.** Trace the statement to the value it prints. `GenerateTrees` logs "N foliage layers" where N is `foliage.Length`, not the number actually generated — it returns `true` even when every layer is skipped.
2. **Before any A/B experiment, name the one observation that separates the hypotheses.** Renderer, Proton version, quality and density variants burned five runs and produced no information.
3. **Read `Clamp()` bounds out of the IL before choosing test values** — out-of-range values are silently coerced and the experiment tests the clamp, not the data.
4. **Prove an asset patch reaches the live object first:** the PPtr byte pattern is unique in the file *and* the scene file references that pathID. Back up, and verify the md5 on restore.
5. **Find callers before theorizing about a "gate".** `HasPendingRenderOutputFence` was only used by buffer cleanup — that killed the async-compute theory in seconds.
6. **Absence of exceptions under Wine is not health.** Native Windows D3D11 throws from `GraphicsFence.get_passed`; Wine does not. Compare against the game's own shipped logs.
7. **Never `pkill -f` with the game path in the command line** — use `pkill -x FamilyTime.exe`.
8. **Check tool availability before designing around it:** on this box `apt` offers only `spirv-tools`; no `apitrace`, no `renderdoc`, no `dxc`.
