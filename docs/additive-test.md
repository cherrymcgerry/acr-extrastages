# Additive stage prototype: install & test checklist (~10 min)

**What this tests:** can a new, separately selectable stage be added to the menu without replacing any ACR stage?
The prototype adds **"ACR-DIRT TEST"** under **Wales**: a full copy of Hafren South forward under its own package
paths, with a menu length of **1.23 km**. It drives exactly like Hafren South 1/2. Hafren South itself must stay
unchanged. Design: [additive-plan.md](additive-plan.md).

## 0. Build (skip if `work\acr\additive\dist\zz_acrdirt_registry_P.*` exists)

```powershell
cd C:\Users\robbe\Projects\acr-dirt
pwsh -NoProfile -File mods\acrdirt-additive\build.ps1 -Verify     # ~1 min; ends with ALL PASS and the round trip line
```

Output in `work\acr\additive\dist\`:
- `zz_acrdirt_registry_P.*` (50 KB): the stage-menu tables, i.e. all vanilla rows plus the new ones.
- `zz_acrdirt_s_acrdirttest_P.*` (364 MB): the copied level.

## 1. Install (manual, reversible)

**Remove the nz01r5 override first.** It also replaces the stage-menu table (`DT_TracksVariants`), and whichever
container loads last would hide the other's rows.

```powershell
$paks = 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally\acr\Content\Paks'
New-Item -ItemType Directory -Force "$paks\~mods" | Out-Null
Get-ChildItem "$paks\~mods"                                   # note what is there
Remove-Item "$paks\~mods\zz_acrdirt_nz01r5_P.*", "$paks\~mods\zz_acrdirt_test_P.*", "$paks\~mods\zz_acrdirt_ramp_P.*" -ErrorAction SilentlyContinue
Copy-Item C:\Users\robbe\Projects\acr-dirt\work\acr\additive\dist\zz_acrdirt_*_P.* "$paks\~mods\"
```

**Rollback:** `Remove-Item "$paks\~mods\zz_acrdirt_registry_P.*", "$paks\~mods\zz_acrdirt_s_*"`. To return to the
DR2 stage, copy `zz_acrdirt_nz01r5_P.*` back from its dist folder.

## 2. Test

1. Stage selection → **Wales**.
   - **Check A:** Wales lists **Hafren South** exactly as before, with **two** variants: 1/2 forward and 2/2 reverse.
     Their names, lengths (4.8 km forward) and images are the vanilla ones.
   - **Check B:** Wales also lists a second track, **"ACR-DIRT TEST"**, with **one** variant, "ACR-DIRT TEST".
     The info panel shows **1.23 km**. The map and background images are Hafren South's (reused on purpose).
   - Note anything odd about the layout, e.g. the track list or paging.
2. Other locations: open each of them briefly.
   - **Check C:** their stage lists are unchanged.
3. Pick **ACR-DIRT TEST** and start a **free practice** (offline) with any car.
   - **Check D:** it loads, and you spawn at Hafren South's forward start.
4. Drive a few hundred metres, or the whole stage if you like.
   - **Check E:** scenery, road, surfaces, start, splits and finish behave like Hafren South 1/2 (the HUD route is
     about 5.1 km; only the menu length is 1.23 km).
   - If it fails, note the step and whether the game crashed or showed an error, then send
     the newest log in `%LOCALAPPDATA%\acr\Saved\Logs\`.
5. Back in the menu, start **Hafren South 1/2** (forward) and **2/2** (reverse).
   - **Check F:** both load and drive normally, i.e. the vanilla stages are untouched.
6. Optional: start a time trial or rally event with ACR-DIRT TEST.
   - Note whether it is offered and whether results save.

## 3. What to report

- A to F: pass or fail, with a screenshot of the Wales stage list.
- If B fails (no entry) but C, D and F pass, the merged tables are not what the menu reads; see the fallbacks in
  additive-plan.md §6.
- If D fails, send the log line with the first error about `/Game/AcrDirt/`.
