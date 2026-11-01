# Linux (Steam + Proton)

ACR Extra Stages runs natively on Linux for *Assetto Corsa Rally* played through Steam and Proton.
The launcher and every helper tool are Linux programs; only the game itself runs under Proton. The
stages it builds are the same `~mods` files as on Windows.

Everything in the [README](../README.md) applies (offline only, disk space, no redistribution);
this page covers what is different on Linux.

## Requirements

- Linux, x86-64. Tested on Bazzite (Fedora Atomic) with the native Steam client; other
  distributions, the Steam Deck and Flatpak Steam are expected to work (see
  [Steam installs](#steam-installs)).
- *Assetto Corsa Rally* installed through Steam and started at least once with Proton.
- *DiRT Rally 2.0*, your own copy, installed through Steam (Proton) or copied from another PC. It
  is only read.
- The launcher folder on the **same filesystem** as ACR (see
  [Same filesystem](#same-filesystem-hard-links)), with the free space the README lists.
- Nothing else for the release archive: it has no .NET, Wine or PowerShell requirement.

## Install from a release

1. Download `ACR-Extra-Stages-<version>-linux-x64.tar.gz` from the
   [releases page](https://github.com/cherrymcgerry/acr-extrastages/releases) and, optionally,
   check it: `sha256sum -c SHA256SUMS-linux-x64.txt`.
2. Extract it in your home folder (the same filesystem as your Steam library):

   ```bash
   tar -xzf ACR-Extra-Stages-<version>-linux-x64.tar.gz -C ~
   ```

3. Close the game, then run the launcher from a terminal:

   ```bash
   ~/ACR-Extra-Stages-<version>/AcrExtraStages
   ```

   The menu, the commands (`install --yes`, `uninstall`, `--help`, ...) and the working folder
   (`work/` next to the launcher) are the same as on Windows.

## Install from source

From a checkout of this repository:

```bash
scripts/install-linux.sh                    # builds, installs to ~/ACR-Extra-Stages, links ~/.local/bin/acr-extrastages
scripts/install-linux.sh --prefix ~/Games/acrxs --no-link
```

It needs git, the [.NET 10 SDK](https://dot.net), [Rust](https://rustup.rs) (`cargo`) and tar.
PowerShell 7 runs the build scripts; when `pwsh` is missing, the script installs it for the
checkout only (`work/tools`, the `PowerShell` .NET tool). It runs `scripts/package.ps1`, which
writes the same archive the release workflow publishes, and extracts it into the install folder.
Running it again updates an existing install and keeps its `work/` folder.

Developers can also use the Windows instructions in [CONTRIBUTING.md](../CONTRIBUTING.md): the
PowerShell scripts run unchanged under PowerShell 7 on Linux.

## The mappings step and ptrace

The first Install creates the game's mappings file: the launcher starts ACR through Steam, reads
its type layout from the running game and closes it again. On Linux, `acr-mappings` reads the
game process through `/proc/<pid>/mem`, read-only, like on Windows. The kernel allows that only
when `kernel.yama.ptrace_scope` permits it:

```bash
cat /proc/sys/kernel/yama/ptrace_scope
```

- `0` (Fedora, Bazzite and others): nothing to do.
- `1` (Ubuntu, Linux Mint, Pop!_OS, Arch and others): only a parent process may read another
  process, so the dump fails with "Permission denied". Pick one, once:
  - give `acr-mappings` the permission (lasts until the file is replaced by an update):

    ```bash
    sudo setcap cap_sys_ptrace=ep <launcher folder>/tools/acr-mappings/acr-mappings
    ```

  - or allow it until the next reboot: `sudo sysctl kernel.yama.ptrace_scope=0`.
- `2` works with the `setcap` line; `3` disables reading other processes completely: create the
  mappings on another PC or supply a Dumper-7 `.usmap` with `--usmap`.

The mappings file is cached per ACR build (`work/mappings/<buildId>.usmap`), so this only matters
the first time and after a game update. Do not run the launcher with `sudo`: files in `work/`
would then belong to root.

To close the game after the dump, the tool sends it `SIGTERM` (there is no window-close message
from outside Proton), then `SIGKILL` after 60 s.

## Same filesystem (hard links)

The build reads the game's containers, and Install puts the stages into `~mods`, through hard
links. Hard links only work inside one filesystem (one mount; on btrfs also one subvolume), so the
launcher folder must be on the same mount as ACR. The preflight shows the mount it found:

```text
free space on /var/home: 96.8 GB ...
install target: .../Assetto Corsa Rally/acr/Content/Paks/~mods  -> hard links (no extra space)
```

If ACR is in a Steam library on another drive, extract the launcher on that drive. DiRT Rally 2.0
can be anywhere: it is only read.

## Steam installs

The launcher looks for Steam in `~/.local/share/Steam` (native client, SteamOS / Steam Deck),
`~/.steam/steam`, `~/.steam/root`, Flatpak (`~/.var/app/com.valvesoftware.Steam/data/Steam`) and
Snap (`~/snap/steam/common/.local/share/Steam`), then reads `libraryfolders.vdf` for the other
libraries. If detection fails, it asks for the folders (or pass `--acr <dir> --dr2 <dir>`, or set
`ACR_GAME_DIR` / `DR2_DIR`).

To start the game for the mappings step it runs `steam steam://rungameid/3917090`, falling back to
`xdg-open`. If that does not start the game (for example Flatpak Steam without a `steam://`
handler), create the mappings by hand: start ACR from Steam, wait for the main menu, run
`AcrExtraStages mappings --dump` (it attaches to the running game and leaves it running), close the
game, then choose Install.

## Troubleshooting

[troubleshooting.md](troubleshooting.md) applies, with these Linux specifics:

| Symptom | Fix |
|---|---|
| `opening /proc/<pid>/mem read-only: Permission denied` | [ptrace](#the-mappings-step-and-ptrace) |
| "the work folder must be on the same drive as ACR" | [Same filesystem](#same-filesystem-hard-links) |
| The mappings step waits for `acr.exe` until it times out | Steam did not start the game: [create the mappings by hand](#steam-installs) |
| "no PE modules mapped in pid ..." | The process found is not the Proton game; close other `acr.exe` processes |
| Missing fonts in generated location logos | Install a sans font (Noto Sans, DejaVu Sans or Liberation Sans) |

When you report a Linux problem, add your distribution, how Steam is installed (native, Flatpak,
Snap), the Proton version ACR uses and the output of `cat /proc/sys/kernel/yama/ptrace_scope`.
