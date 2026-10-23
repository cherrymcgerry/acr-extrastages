# acr-mappings

Creates the CUE4Parse mappings file (`.usmap`) for the installed Assetto Corsa Rally build, replacing the
manual Dumper-7 step. It starts ACR through Steam (or attaches to a running game), reads the engine's
reflection data (GObjects, FNamePool, every UClass / UScriptStruct / UEnum and its FProperty chain) **from
outside the process**, writes the usmap, and closes the game again.

- **Read-only:** the process is opened with `PROCESS_VM_READ | PROCESS_QUERY_INFORMATION` only (asserted by a unit
  test). There is no injection, no DLL, no thread creation and nothing is written to the game.
- **Engine globals** are resolved with acr-rewind's `acr_ue` crate (`resolve_gobjects` / `resolve_fnamepool` and the
  reflection walker), driven by acr-rewind's `config/signatures.toml`. This is the same code acr-rewind's hook and
  `acr-probe sig-test` run.
- **Output:** usmap version 4 (`ExplicitEnumValues`), Zstandard-compressed, which is what Dumper-7 writes and what
  CUE4Parse 1.2.2 reads (`CUE4Parse.MappingsProvider.Usmap.UsmapParser`). Conventions follow Dumper-7: every loaded
  UStruct except functions, own properties in `ChildProperties` order, enum entry names without the `EType::` prefix,
  and byte properties that have an enum written as `EnumProperty<ByteProperty>`.

## Usage

```text
acr-mappings dump     [--game <ACR dir>] [--out <file.usmap>] [--attach | --exe-wait] [--force] [--keep-running]
                      [--timeout 300] [--cache-dir work/mappings] [--signatures <file>] [--allow-build-mismatch]
acr-mappings validate [--game <ACR dir>] [--usmap <file>] [--vanilla <mods-free game dir>] [--tool <acr-additive.exe>]
acr-mappings path     [--game <ACR dir>] [--unvalidated]
acr-mappings inspect  <file.usmap> [--struct <Name>...] [--enum <Name>...] [--find <text>]
acr-mappings diff     <a.usmap> <b.usmap> [--limit 40] [--strict]
```

- `--game` defaults to `ACR_GAME_DIR`, else `C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally`.
- **dump:**
  - If ACR is already running, the tool attaches to it and leaves it running.
  - Otherwise it starts ACR with `steam://rungameid/3917090`. It waits until GObjects resolve, the key types (`Actor`,
    `DataTable`, `SoftObjectPath`, `DBTrack`, `DBTrackVariant`, `BakedDataContainer`) have their properties linked,
    and the type count is stable. Then it dumps and closes the game: WM_CLOSE first, and a forced kill after 60 s.
  - `--attach` fails instead of starting the game. `--exe-wait` waits for the user to start it.
  - The result is checked before it is written: the usmap must read back, and the types the tools need must be
    present with no unknown property types.
- **validate** runs `acr-additive usmap-check`. It must succeed, with no exceptions and no CUE4Parse
  "could not read correctly" warnings, on:
  - the five DB tables: `DT_Levels`, `DT_Tracks`, `DT_TracksVariants`, `DT_TracksLocations` and `DT_TracksTextures`;
  - the Hafren South host packages: the persistent level, the generated cell stage-build edits, the BakedData asset
    and the pacenote table.

  Packages come from `--vanilla`, else from `--game`. The `~mods` subfolder is never read. The result is recorded in
  the sidecar.
- **path** prints the cached usmap for the installed build. It exits with 1 if there is none, or if the cached file has
  not passed `validate` (add `--unvalidated` to accept it anyway).

### Cache

`work/mappings/<buildId>.usmap` plus `work/mappings/<buildId>.json`. The build ID comes from
`steamapps/appmanifest_3917090.acf`. The sidecar records:

- the build ID, the creation time, and the tool version;
- the signatures build, and where GObjects and FNamePool were found;
- object, struct, enum and property counts;
- the `validated` result.

`dump` returns the cached file without starting the game unless `--force` is given. `--out` additionally copies the
file, with its sidecar, to the given path.

### Build mismatch

`dump` refuses to run when the installed Steam build is not the `verified_build` in the signatures. To fix that:

- update acr-rewind's `config/signatures.toml` for the new build (acr-rewind `docs/updating-after-a-patch.md`);
- bump the pinned `acr_ue` revision in `Cargo.toml` and copy the file over `tools/acr-mappings/signatures.toml`. A unit
  test checks that the copy matches a sibling `../acr-rewind` checkout.

`--allow-build-mismatch` tries anyway. The result is still checked by `validate`. Until then, the Dumper-7 usmap
still works with every tool through `--usmap` / `ACR_USMAP`.

## Launcher integration

The launcher should run, in order:

1. `acr-mappings path --game <ACR>`. If it exits with 0, use the printed file and stop here.
2. Otherwise show "ACR will start briefly to read its data layout", then run `acr-mappings dump --game <ACR>`. Make
   sure ACR is not running first, so that the tool closes it again afterwards.
3. Run `acr-mappings validate --game <ACR> --tool <bundled acr-additive.exe>`.
4. Pass the cached path to `acr-ref` / `acr-additive` as `--usmap` (or `ACR_USMAP`).

Exit code 0 means success; any other code means failure, with the reason on stderr. A failed dump or validation means
falling back to a user-supplied Dumper-7 file (`--usmap`).

## Build

```powershell
cargo build --release --manifest-path tools/acr-mappings/Cargo.toml
dotnet publish tools/acr-additive -c Release -o work/bin/acr-additive   # needed by `validate`
```

### Local acr-rewind checkout

`acr_ue` is a git dependency pinned to a commit of <https://github.com/cherrymcgerry/acr-rewind>. To build against a
sibling checkout instead, without editing `Cargo.toml`:

```powershell
cargo build --release --config 'patch."https://github.com/cherrymcgerry/acr-rewind.git".acr_ue.path="../../../acr-rewind/crates/acr_ue"'
```

Or put the same `[patch."https://github.com/cherrymcgerry/acr-rewind.git"]` entry in an uncommitted
`tools/acr-mappings/.cargo/config.toml`.

## Verified (ACR build 25170642, 2026-10-23)

- **Timing:** a cold run (start, dump, close) takes about 30 to 60 s; most of that is game start-up. The output is
  about 620 KB, with 14.9k structs, 2.6k enums and 51.7k properties.
- **Against the Dumper-7 usmap** (`acr-mappings diff`):
  - 14,768 structs and 2,621 enums are identical.
  - Every type present only on one side is a Blueprint class, a user-defined struct or enum, or a runtime
    `PropertyBag_<hash>` struct that was loaded in one session and not the other. No native type is missing.
  - The remaining differences are Dumper-7 renames for C++ (`PF_MAX_0`, `TRANSPARENT_0`, Blueprint property names cut
    at `/`). Our file keeps the real names.
- **CUE4Parse:** `validate` passes on the vanilla install. A copy with one wrong `DBTrackVariant` property type fails
  on `DT_TracksVariants`.

## Limitations

- **Blueprint types depend on load state.** Blueprint classes and user structs are dumped only if they are loaded at
  dump time (main menu). CUE4Parse reads Blueprint schemas from their own packages, so the tools don't need them.
  Dumper-7 files have the same property.
- **Fixed layout.** The FProperty sub-layout (`sizeof(FProperty)` 0x70, array inner at 0x78, `UEnum::Names` at 0x40)
  is fixed for UE 5.6 in `walk::PropLayout`, not read from `signatures.toml`. An engine upgrade would need these
  offsets checked (Dumper-7 CppSDK `Basic.hpp`).
- **Windows only.** The game is started through Steam, so Steam must be able to launch it unattended.
