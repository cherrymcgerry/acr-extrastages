# Release checklist

Adapted from acr-rewind's `docs/nexus-checklist.md`. ACR Extra Stages is a **converter**, not a
mod pack: the release contains tools only. Distribution order is **GitHub release first**; Nexus
Mods only after the moderators have been asked (see [Nexus Mods](#nexus-mods)).

Tick the boxes in a copy of this file (or an issue) per release and record the version, the date and
the ACR build. Do not tick a box you did not run.

## Status of the first beta (0.1.0-beta.1)

Recorded 2026-10-23. Nothing below has been published.

| Item | Status |
|---|---|
| This checklist, `docs/troubleshooting.md`, `docs/updating-after-a-patch.md` | Written |
| `scripts\package.ps1`, GitHub Actions workflows (`ci.yml`, `release.yml`) | Present; local `package.ps1` produced `dist/ACR-Extra-Stages-0.1.0-beta.1.zip` (~142 MB) |
| Clean-machine run (Windows Sandbox / VM) | **Not run.** Windows Sandbox is not installed on the development PC (`WindowsSandbox.exe` missing). See [the notes on Sandbox](#clean-machine-test) |
| VirusTotal scan of `acr-mappings.exe` / `AcrExtraStages.exe` | **Not run** |
| SHA-256 of a release zip | Local: see `dist/SHA256SUMS.txt` after `package.ps1`; re-check from the GitHub Actions artifact before publishing |
| Nexus moderators asked | **Not done.** Policy note: Nexus [Porting content](https://wiki.nexusmods.com/index.php/Porting_content) forbids uploading ported *assets* without rights-holder permission. This release ships a converter with **no** game assets; still ask moderators before creating an ACR page, and never upload generated `zz_acrxs_*` paks. Internet-connected tools (Oodle fetch) may need staff review per the File Submission Guidelines. |

## 1. Before tagging

- [ ] `CHANGELOG.md` has an entry for the version; the README beta banner and version match.
- [ ] The `verified_build` in `tools/acr-mappings/signatures.toml` and the "supported ACR build"
      in the README / release notes are the current Steam build (see
      [updating-after-a-patch.md](updating-after-a-patch.md)).
- [ ] Builds are clean: `dotnet build` for `tools/acr-ref`, `tools/acr-additive`,
      `tools/dr2-extract` and `tools/launcher`; `cargo test` and `cargo build --release` for
      `tools/acr-mappings`.
- [ ] `pwsh -NoProfile -File scripts\check-repo-content.ps1` passes (no game-like files tracked).
- [ ] `THIRD_PARTY_NOTICES.md` matches the dependencies in the release (new package, bumped
      version, retoc pin).
- [ ] The pinned `acr_ue` revision in `tools/acr-mappings/Cargo.toml` is a commit that exists in the
      public acr-rewind repository (the CI build cannot use a sibling checkout).
- [ ] Release notes say: beta, offline / single-player only, the ACR build verified, and the
      "do not redistribute generated stages" rule.

## 2. Build the release (GitHub Actions)

Tag `v<version>` and let the release workflow build, rather than uploading a local build, so the
published zip and its hash come from the same pipeline.

- [ ] The workflow produced `ACR-Extra-Stages-<version>.zip` and `SHA256SUMS.txt` as a **draft**
      release.
- [ ] Download the zip from the draft release (not from your local `dist\`) for every check below.

## 3. Zip contents: no game data

Expected contents (see [tools/launcher/README.md](../tools/launcher/README.md), "Layout it expects"):

```text
AcrExtraStages.exe
config\*.toml
tools\acr-ref\acr-ref.exe
tools\acr-additive\acr-additive.exe
tools\dr2-extract\dr2-extract.exe
tools\retoc\retoc.exe
tools\acr-mappings\acr-mappings.exe
README.md, CHANGELOG.md, LICENSE-MIT, LICENSE-APACHE, THIRD_PARTY_NOTICES.md
docs\troubleshooting.md, docs\updating-after-a-patch.md   (optional but recommended)
```

- [ ] List the zip: `Expand-Archive` to a temp folder, or `tar -tf <zip>`; nothing outside the list
      above (the self-contained .NET tools bring their runtime files; that is expected).
- [ ] **No game data.** None of these anywhere in the zip:
      `*.usmap`, `*.pak`, `*.utoc`, `*.ucas`, `*.uasset`, `*.umap`, `*.dds`, `*.nefs`, `*.dat` from
      DR2, `*.png` / `*.jpg` that come from a game, generated stage specs, or a `work\` folder.
- [ ] **No Oodle.** No `oo2core*.dll`, `oodle*.dll` (the tools fetch it on the user's PC).
- [ ] No memory dumps, logs, personal paths, `launcher-settings.json`, or Dumper-7 output.
- [ ] No dev-only tools (`stage-validate`, test helpers).
- [ ] `config\stages.toml` contains our own stage list and text only.
- [ ] Zip size is plausible (tens of MB; hundreds of MB would mean something was added by mistake).

## 4. SHA-256

- [ ] Hash the exact file that will be offered:

  ```powershell
  Get-FileHash .\ACR-Extra-Stages-<version>.zip -Algorithm SHA256
  ```

- [ ] It matches the line in the release's `SHA256SUMS.txt`. A local build has a different hash from
      the CI build; always quote the hash of the file you publish.
- [ ] The README / release notes tell users how to check it.
- [ ] Also record the SHA-256 of `AcrExtraStages.exe` and `acr-mappings.exe` inside the zip (below).

## 5. VirusTotal and antivirus false positives

`acr-mappings.exe` opens the game process to read memory, scans it and starts/closes a game: that
profile gets flagged by heuristic scanners. `AcrExtraStages.exe` is a single-file self-extracting
.NET app that starts other programs, and `retoc.exe` and the .NET tools can also trigger
reputation-based detections.

- [ ] Upload to <https://www.virustotal.com> **before** publishing:
  - [ ] `tools\acr-mappings\acr-mappings.exe`
  - [ ] `AcrExtraStages.exe`
  - [ ] the release zip
  - [ ] optionally `retoc.exe`, `acr-ref.exe`, `acr-additive.exe`, `dr2-extract.exe`
- [ ] Record, per file: SHA-256, date, detections (for example `2/70`) and the report URL in the
      release notes.
- [ ] Review each detection. Generic or heuristic names ("Trojan.Generic", "Malware.AI", `PUA`,
      "HackTool/Cheat") on the memory-reading tool are expected false positives; a family-specific
      name from several major engines is not. Investigate that before releasing.
- [ ] If a major vendor flags it, submit it as a false positive to that vendor, and mention it in the
      release notes with the workaround (exclusion, or the Dumper-7 `--usmap` route).
- [ ] Release notes and README troubleshooting carry the antivirus note and a pointer to
      [troubleshooting.md](troubleshooting.md#antivirus-flags-acr-mappingsexe).
- [ ] Test with Windows Defender (default settings) on the clean machine in step 6: unzip, then run
      a scan or just launch the tools; note any quarantine.
- [ ] Do **not** upload anything generated from the games (stage paks, `.usmap` files) to VirusTotal
      or any other service; that is a redistribution of game-derived content.

## 6. Clean-machine test

Goal: prove the zip works on a PC that has nothing but the games and Windows, i.e. no SDKs, no
PowerShell 7, no repo checkout. Use a throwaway Windows 10/11 VM or Windows Sandbox.

**Sandbox caveats** (why a VM is usually the better fit):

- Windows Sandbox starts with no Steam login, no games and a small disk. Both games are large, and
  the build needs about 36 GB free on the game's drive.
- The launcher needs hard links, so the zip and `work\` must be on the same NTFS volume as ACR.
  Games mapped into the sandbox as shared folders are a different volume from the sandbox's own
  disk, and a mapped host folder may not support hard links, so the preflight "same drive" check
  fails.
- `acr-mappings` starts ACR through Steam, which needs Steam installed, signed in and able to start
  the game unattended inside the sandbox. A sandbox is discarded on close, so mappings and `work\`
  are lost each time.
- For a **dry run without the games**, Sandbox is still useful for the first steps (unzip, antivirus
  behaviour, `detect` / `preflight` output with no games present).
- Preferred: a Hyper-V or VMware/VirtualBox VM with a large virtual disk, Steam, ACR and your own DR2
  copy installed, a snapshot before the test, and Steam set to offline mode.

Test steps (record pass/fail and the ACR build for each):

- [ ] **Unzip** `ACR-Extra-Stages-<version>.zip` to a folder on the same NTFS drive as the game,
      for example `D:\ACR-Extra-Stages`. Windows SmartScreen / Defender prompts: note them.
- [ ] **Detect.** `AcrExtraStages.exe detect` (or the menu): both games found through Steam, and the
      folders saved. Also try with Steam closed and with a game in a second Steam library folder.
- [ ] **Preflight.** `AcrExtraStages.exe preflight`: shows the ACR build id, free space and the
      estimate, `ACR not running: ok`, every tool found, `config` present, and the install target
      reports `hard links (no extra space)`. Also check that it **fails** with a clear message when the
      folder is on another drive, and when ACR is running.
- [ ] **Mappings.** `AcrExtraStages.exe mappings --dump --yes` (ACR starts, closes itself): a
      `work\mappings\<buildId>.usmap` and sidecar `.json` appear, `validated` is true, ACR is no
      longer running. Then run it again: it must return the cached file without starting the game.
      Also check the Oodle download works on this fresh PC.
- [ ] **One-stage dry run.**

  ```powershell
  AcrExtraStages.exe install --only AcrXsNz01r5 --dry-run-mods $env:TEMP\acrxs-mods --yes
  ```

  The stage prepares and builds, checks pass (WARN lines are acceptable, FAIL is not),
  `work\acr\batch\report.md` lists it as built, and `$env:TEMP\acrxs-mods` has the
  `zz_acrxs_registry_P.*` and `zz_acrxs_s_acrxsnz01r5_P.*` files. The game's real `~mods` is not touched.
- [ ] **Real install of the same stage** (without `--dry-run-mods`), start the game offline, and check
      the stage appears in the menu and loads.
- [ ] **Resume.** Start another build, kill the launcher part-way, run it again: it continues, and
      completed work is skipped.
- [ ] **Simulated game update.** Verify the game's files, or change the build id in a copy, and
      confirm **Update** regenerates the mappings and rebuilds only what changed
      ([updating-after-a-patch.md](updating-after-a-patch.md)).
- [ ] **Uninstall.** `AcrExtraStages.exe uninstall` (or the menu): every `zz_acrxs_*` file is gone from
      `~mods`, no other file was removed, and the game starts normally. Also try with a leftover
      legacy `zz_acrdirt_*` file: it must be removed too.
- [ ] (Optional, full run) all 30 stages install and the menus show them; record the disk used
      against the README estimate (`scripts\estimate-disk.ps1`), so the "to be confirmed for the
      release" note in the README can be replaced by measured numbers.

Write the results (date, ACR build, pass/fail per step, log excerpts without personal paths) into the
release notes, and mark here what was **not** run.

## 7. Publish on GitHub

- [ ] Check the draft release text: version, beta banner, supported ACR build, the zip, `SHA256SUMS.txt`,
      the VirusTotal links and the offline-only / do-not-redistribute notes.
- [ ] Publish the release (mark it **pre-release** while in beta).
- [ ] Download the published zip once more and verify its SHA-256.
- [ ] Update the README install link if needed ("No release zip has been published yet" note and the
      repository URL).

## Nexus Mods

**Do not post yet.** Nexus's rules forbid uploading other games' assets, and a tool that converts
content between games sits in a grey area. Before anything is posted:

- [ ] **Ask the Nexus moderators** (contact the site staff / the game page's moderators) whether a
      converter that ships no assets and generates output from the user's own installs of two games is
      allowed on the Assetto Corsa Rally page. Describe it plainly: source-only tool, no game data in the
      archive, output stays on the user's PC, offline only. Keep their answer (link or screenshot) with
      the release notes.
- [ ] Only if they agree: reuse acr-rewind's Nexus checklist for the page fields, media, description
      sections, permissions and credits (<https://github.com/cherrymcgerry/acr-rewind>, `docs/nexus-checklist.md`),
      adapted to this tool, and mirror the exact GitHub zip with the same SHA-256.
- [ ] Description must open with the beta and offline / single-player framing, the requirement of
      **both** games, and the antivirus note with the SHA-256 and VirusTotal links.
- [ ] Permissions: **do not** invite re-uploads of generated stages; keep "Upload to other sites" and
      "Convert to other games" decisions deliberate (they apply to the tool, not to any output).
- [ ] **Never upload generated paks** (`zz_acrxs_*` or anything in `~mods` / `work\acr\additive\dist`),
      screenshots with reusable game art, or `.usmap` files to Nexus, GitHub, Discord or anywhere else.
      Share the tool, not its output.
- [ ] If Nexus says no, stay on GitHub only; nothing else in this checklist changes.

## After publishing

- [ ] Watch the issue tracker for the first reports; ask for the stage id, ACR build and log lines
      only (never game files).
- [ ] When ACR updates, follow [updating-after-a-patch.md](updating-after-a-patch.md) and cut a new
      release from the start of this checklist.
