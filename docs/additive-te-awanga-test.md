# Te Awanga additive: install & test (~10 min)

**What this tests:** Te Awanga Sprint Reverse (v1.11 content) as its **own** menu entry under a new **New Zealand**
location, without overriding Hafren South. Offline verify: ALL PASS.

**Do not install with** `zz_acrxs_nz01r5_P.*` (the old override): both ship `DT_TracksVariants`.

## Install

```powershell
$paks = 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally\acr\Content\Paks'
New-Item -ItemType Directory -Force "$paks\~mods" | Out-Null
Remove-Item "$paks\~mods\zz_acrxs_*", "$paks\~mods\zz_acrdirt_*" -ErrorAction SilentlyContinue
Copy-Item C:\Users\robbe\Projects\acr-dirt\work\acr\additive\dist\zz_acrxs_*_P.* "$paks\~mods\"
```

**Rollback:** `Remove-Item "$paks\~mods\zz_acrxs_*", "$paks\~mods\zz_acrdirt_*"` (the second pattern is the legacy name from before the rename).

## Checks

1. **New Zealand** appears in the location list (logo may still be Wales's).
2. It lists **Te Awanga** → **Te Awanga Sprint Reverse**, length **4.69 km**.
3. Free practice: drives like v1.11 (DR2 ground, pacenotes, scenery hidden).
4. **Wales → Hafren South** forward and reverse both load as vanilla (reverse was broken under the old override).
