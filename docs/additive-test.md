# Additive stage prototype: install & test checklist (~15 min)

**What this tests:** can new, separately selectable stages be added to the menu without replacing any ACR stage?
The prototype adds:
- **"ACR-XS TEST"** under **Wales** (Hafren South forward clone, menu length **1.23 km**)
- **"ACR-XS LOC"** under a **new location "New Zealand"** (same Hafren clone under new package paths)

Both drive like Hafren South 1/2. Hafren South itself must stay unchanged. Design: [additive-plan.md](additive-plan.md).

## 0. Build (skip if `work\acr\additive\dist\zz_acrxs_registry_P.*` exists)

```powershell
cd C:\Users\robbe\Projects\acr-dirt
pwsh -NoProfile -File mods\additive\build.ps1 -Verify     # ~2 min; ends with ALL PASS and the round trip line
```

Output in `work\acr\additive\dist\`:
- `zz_acrxs_registry_P.*` (50 KB): the stage-menu tables, i.e. all vanilla rows plus the new ones.
- `zz_acrxs_s_acrxstest_P.*` (~364 MB): the Wales-track clone.
- `zz_acrxs_s_acrxsloc_P.*` (~364 MB): the New Zealand-location clone.

## 1. Install (manual, reversible)

**Remove the nz01r5 override first.** It also replaces the stage-menu table (`DT_TracksVariants`), and whichever
container loads last would hide the other's rows.

```powershell
$paks = 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally\acr\Content\Paks'
New-Item -ItemType Directory -Force "$paks\~mods" | Out-Null
Get-ChildItem "$paks\~mods"                                   # note what is there
Remove-Item "$paks\~mods\zz_acrdirt_*", "$paks\~mods\zz_acrxs_nz01r5_P.*", "$paks\~mods\zz_acrxs_test_P.*", "$paks\~mods\zz_acrxs_ramp_P.*" -ErrorAction SilentlyContinue
Copy-Item C:\Users\robbe\Projects\acr-dirt\work\acr\additive\dist\zz_acrxs_*_P.* "$paks\~mods\"
```

**Rollback:** `Remove-Item "$paks\~mods\zz_acrxs_registry_P.*", "$paks\~mods\zz_acrxs_s_*"`. To return to the
DR2 stage, copy `zz_acrxs_nz01r5_P.*` back from its dist folder.

## 2. Test

1. Stage selection → **Wales**.
   - **Check A:** Wales lists **Hafren South** exactly as before, with **two** variants: 1/2 forward and 2/2 reverse.
     Their names, lengths (4.8 km forward) and images are the vanilla ones.
   - **Check B:** Wales also lists a second track, **"ACR-XS TEST"**, with **one** variant, "ACR-XS TEST".
     The info panel shows **1.23 km**. The map and background images are Hafren South's (reused on purpose).
     Note anything odd about the layout, e.g. the track list or paging.
2. Stage selection → look for **"New Zealand"** (logo/background will be Wales's, reused on purpose).
   - **Check B2:** New Zealand lists **"ACR-XS LOC"** with one variant, length **1.23 km**.
3. Other locations: open each of them briefly.
   - **Check C:** their stage lists are unchanged.
4. Pick **ACR-XS TEST** (Wales) and start a **free practice** (offline) with any car.
   - **Check D:** it loads, and you spawn at Hafren South's forward start.
5. Drive a few hundred metres, or the whole stage if you like.
   - **Check E:** scenery, road, surfaces, start, splits and finish behave like Hafren South 1/2 (the HUD route is
     about 5.1 km; only the menu length is 1.23 km).
   - If it fails, note the step and whether the game crashed or showed an error, then send
     the newest log in `%LOCALAPPDATA%\acr\Saved\Logs\`.
6. Optional: pick **ACR-XS LOC** under New Zealand and confirm it also loads (same Hafren clone).
7. Back in the menu, start **Hafren South 1/2** (forward) and **2/2** (reverse).
   - **Check F:** both load and drive normally, i.e. the vanilla stages are untouched.
8. Optional: start a time trial or rally event with ACR-XS TEST.
   - Note whether it is offered and whether results save.

## 3. What to report

- A, B, B2, C, D, E, F: pass or fail, with a screenshot of the Wales stage list and of New Zealand if it appears.
- If B fails (no entry) but C, D and F pass, the merged tables are not what the menu reads; see the fallbacks in
  additive-plan.md §6.
- If B2 fails (no New Zealand location), new location rows are ignored; keep using existing locations until that is fixed.
- If D fails, send the log line with the first error about `/Game/AcrXs/`.
