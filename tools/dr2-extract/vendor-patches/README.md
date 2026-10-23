# Vendor patches

`third_party/` is gitignored (cloned by `scripts/bootstrap.ps1` at pinned commits), so local fixes
to those clones live here as `git diff` patches named `<third_party dir>-<topic>.patch`.

`scripts/bootstrap.ps1` applies every matching patch after checking out the pinned commit
(idempotent). After bootstrap, rebuild with
`dotnet build tools/dr2-extract/Dr2Extract.csproj -c Release -p:NuGetAudit=false --no-restore`.

- `ego.nefsedit-aes-padding.patch` — `NefsTransformer.cs`:
  - AES ECB uses `PaddingMode.None` (Zeros stripped trailing 0x00 and truncated chunks, giving short
    `tracksplit.pssg` in AES locations such as Scotland/Finland/Wales/Sweden).
  - Skips inflate for chunks stored raw although flagged zlib.
  - Output streams opened with `FileMode.Create` (truncate) and `FileShare.Read`.

Regenerate after further edits: `git -C third_party/ego.nefsedit diff --no-color --output=<abs path to patch>`.
