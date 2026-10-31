# AGENTS.md: notes for developers and AI agents

Read README.md, CONTRIBUTING.md and docs/decisions.md first. This file collects what was learned the hard way while
porting the tool to Linux and working on stage visuals (2026-10/11, branches `linux-port` and `visual-preview`).

## Ground rules

- Never commit game data, `.usmap`, Oodle or generated stages (`scripts/check-repo-content.ps1`).
- Keep Windows behaviour unchanged when adding Linux code: guard with `OperatingSystem.IsWindows()` / `#[cfg]`.
- Build stages offline only; test in game with Steam offline.
- Each change: rebuild one stage (`AcrExtraStages build --only AcrXsNz01r5 --force --keep-temp --yes`), check the
  report (`work/acr/additive/stage-<id>.report.json`) and logs (`work/acr/batch/logs/<id>.log`), then test in game.

## Linux specifics

- The game runs under Proton; every tool runs natively. `acr-mappings` reads the game through `/proc/<pid>/mem`
  (needs `kernel.yama.ptrace_scope` 0 or `setcap cap_sys_ptrace=ep`). See docs/linux.md.
- Fedora Atomic (Bazzite): `/home` -> `/var/home`. Always build from the physical path (`pwd -P`, `realpath`), or
  MSBuild's incremental clean deletes outputs recorded under the other spelling.
- Homebrew .NET: framework-dependent tools need `DOTNET_ROOT` (`/home/linuxbrew/.linuxbrew/opt/dotnet/libexec`).
- The repo's mini TOML parser (`tools/acr-ref/Toml.cs`) supports only single-line arrays; strings may contain commas.
- Watch out for `pgrep -f <pattern>` matching your own shell's command line.

## Seeing what the game does: `acr-mappings live` (read-only)

With ACR running and the stage loaded:

```bash
tools/acr-mappings/target/release/acr-mappings live --find <name text> [--find 'Outer.Path.'] [--prop X] [--near <double>]
```

Finds live UObjects by name (or by full path when the text contains `.`), prints class, flags, outer chain and
reflected properties decoded by size: bools with their real bit mask, pointers to named objects, TArrays (Tags as
names, PerInstanceSMData as matrices), FVectors. `--near` finds unreflected members (e.g. ComponentToWorld) by value.
This is how the ServicePark tag problem below was found. Use it before guessing.

## Offline visual loop: `acr-ref preview`

docs/visual-preview.md. Renders the built cell's visuals mesh next to the full DR2 scene from the same cameras;
writes contact.png, station images and metrics.json (vegetation/object coverage, agreement). ~6 s per stage. It cannot
show ACR materials or level actors (native foliage is not in it).

## How a stage is built (short)

The stage is a relocated copy of Hafren South (`/Game/AcrXs/Stages/<id>/<id>.umap`, persistent level plus cell
22VG). Ground, road, objects and vista are one ProceduralMeshComponent (world-space vertices) in cell 22VG;
collision is Kunos BakedData; Hafren's own scenery is hidden (config/scenery.toml, Scenery.cs). DR2 textures become new
Texture2D + MaterialInstanceConstant packages cloned from cooked templates (DropIn.cs).

## Using ACR assets: what works

- **Materials:** a cloned MIC keeps its parent's cooked shader, so pick a template whose parent has the features you
  need. `M_Simple` children: opaque lit (objects, ground). `M_TreelineMaster` (`M_Treeline_Wales`): masked, two-sided,
  BaseColor alpha = opacity. Its atlas tiling scalars `UsedTiles` / `VerticalTiles` are patched to 1. Billboards on it
  render, but shade dark: it was made for distant treelines.
- **Meshes and foliage (best result):** do not draw vegetation into the procedural mesh (no instancing, no LOD, no
  culling). Reuse Hafren's instanced foliage components instead (NativeFoliage.cs, `[native_foliage]` in
  config/scenery.toml, launcher `--native-foliage`).
  - Hafren's persistent level has 12 PCG stamp HISMs (actors `PCGStamp_3d3e525db80efa5b_*`: about 50k spruce, beech
    and grass instances) plus 267 HLOD-owned ones. All ACR foliage meshes are Nanite with 4 LODs (`acr-ref
    mesh-info` gives sizes).
  - **All stamp actors carry the tag `ServicePark`, and ACR sets bHidden on such actors during a stage.** The tag must
    be renamed (to `None`, via `LegacyPackage.AddExportName`) or the trees never show.
  - Any foliage mesh in the game can be used: add a Package + StaticMesh import, retarget the component's StaticMesh
    value (it sits after InstanceReorderTable) and its preload dependency (`SetDep`).
  - Cooked HISM export layout (UE 5.6): SortedInstances (count + ints), NumBuiltInstances, BuiltInstanceBounds (6
    doubles + byte), CacheMeshExtendedBounds (7 doubles), InstanceCountToRender, InstancingRandomSeed,
    InstanceReorderTable (count + ints), …, CachedBounds[0..2] (FBoxSphereBounds, [2] in component space, [0..1] in
    world space), then native data: PerInstanceSMData bulk (128, n) row-major double matrices relative to the stamp's
    DefaultSceneRoot (translated and yawed), custom data (4, 0), one int32 0, ClusterTree bulk (64, nodes) to the end
    of the export. NativeFoliage rebuilds all of it for any instance count (own breadth-first cluster tree, identity
    order arrays).
  - Never use scale-0 instances (singular matrices); the component then does not render.
- **Names:** new FNames used from export data must go inside `NamesReferencedFromExportDataCount`
  (`AddExportName`), or `retoc to-zen` drops them.
- **Persistent-level edits:** CUE4Parse reads values (floats) from the vanilla game; locate them in the legacy bytes
  as doubles (FindDoubles) and write in place; verify with `export-bytes` on
  `work/acr/additive/mod-stage-<id>/acr/Content/AcrXs/Stages/<id>/<id>.umap` (the relocated level that ships).
  `acr-ref export` reads the unmodded game, not the mod.

## Useful acr-ref commands added

`preview`, `mesh-info --filter`, `export-bytes <pkg> <export0>`, `data-resources <pkg>`, `dds-preview --alpha 1`
(now decodes BC7), `mic-scan`, `tex-scan`, `legacy-dump`.

## Status (2026-11-01)

- `linux-port`: Linux support, draft PR cherrymcgerry/acr-extrastages#1 (CI on the fork needs Actions enabled once).
- `visual-preview`: preview tool, billboards (`--vegetation billboards`, works but dark), native foliage
  (`--native-foliage`). Proof with 143 spruces worked in game. Full mapping (19,770 plants, 9 ACR meshes; species
  in config/scenery.toml) is built and installed, **not yet checked in game**.
- Next: check native foliage in game (sizes, density, frame rate), then ground grass (option 4) from the WildGrass
  stamp host, then the flat-colour start gate / posts (debug MI colours) and fences.
