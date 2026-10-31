#!/usr/bin/env bash
# Builds ACR Extra Stages from this checkout and installs it for Linux (Steam + Proton).
#
#   scripts/install-linux.sh [--prefix DIR] [--no-link]
#
# Needs git, the .NET 10 SDK, a Rust toolchain (cargo) and tar. PowerShell 7 is used for the build scripts; when
# `pwsh` is not installed it is installed for this checkout only (`dotnet tool install --tool-path work/tools`).
#
# What it does:
#   1. scripts/package.ps1: bootstraps third_party/, publishes the launcher and tools (self-contained linux-x64),
#      builds acr-mappings and retoc (pinned commit) and writes dist/ACR-Extra-Stages-<version>-linux-x64.tar.gz.
#   2. Extracts that archive into --prefix (default ~/ACR-Extra-Stages). An existing install is updated in place:
#      its work/ folder (settings, prepared data, built stages) is kept.
#   3. Links ~/.local/bin/acr-extrastages to the launcher (skip with --no-link).
#
# The install folder must be on the same filesystem as Assetto Corsa Rally: the build reads the game's containers
# and installs the stages through hard links. The launcher's preflight checks this too.
set -euo pipefail

prefix="$HOME/ACR-Extra-Stages"
link=1
while [[ $# -gt 0 ]]; do
    case "$1" in
        --prefix) prefix="${2:?--prefix needs a folder}"; shift 2 ;;
        --no-link) link=0; shift ;;
        -h|--help) sed -n '2,/^set -euo/{/^set -euo/d;s/^# \{0,1\}//;p}' "$0"; exit 0 ;;
        *) echo "unknown option: $1 (see --help)" >&2; exit 2 ;;
    esac
done

# physical path: on Fedora Atomic /home -> /var/home, and MSBuild must always see the same spelling of the checkout
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
say() { printf '\033[36m==> %s\033[0m\n' "$*"; }
die() { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

# ---- prerequisites ---------------------------------------------------------------------------------------------
for tool in git dotnet cargo tar; do
    command -v "$tool" >/dev/null || die "$tool not found. Install git, the .NET 10 SDK (https://dot.net) and Rust (https://rustup.rs) first."
done
dotnet --list-sdks | grep -q '^1[0-9]\.' || die ".NET SDK 10 or newer is required (found: $(dotnet --list-sdks | tr '\n' ' '))"

# Framework-dependent helpers (pwsh as a dotnet tool) need DOTNET_ROOT when dotnet is not in a default location
# (Homebrew, ~/.dotnet). Take it from the runtime the SDK reports.
if [[ -z "${DOTNET_ROOT:-}" ]]; then
    rt="$(dotnet --list-runtimes | sed -n 's/^Microsoft\.NETCore\.App [^ ]* \[\(.*\)\/shared\/Microsoft\.NETCore\.App\]$/\1/p' | head -n1)"
    [[ -n "$rt" ]] && export DOTNET_ROOT="$rt"
fi

pwsh="$(command -v pwsh || true)"
if [[ -z "$pwsh" ]]; then
    pwsh="$root/work/tools/pwsh"
    if [[ ! -x "$pwsh" ]]; then
        say "PowerShell 7 not found: installing it for this checkout (work/tools, dotnet tool 'PowerShell')"
        dotnet tool install --tool-path "$root/work/tools" PowerShell >/dev/null
    fi
fi

# ---- build -----------------------------------------------------------------------------------------------------
say "Building the release (first run: a few minutes)"
"$pwsh" -NoProfile -File "$root/scripts/package.ps1"

version="$(tr -d '[:space:]' < "$root/VERSION")"
archive="$root/dist/ACR-Extra-Stages-$version-linux-x64.tar.gz"
[[ -f "$archive" ]] || die "package.ps1 did not write $archive"

# ---- install ---------------------------------------------------------------------------------------------------
say "Installing into $prefix"
mkdir -p "$prefix"
tar -xzf "$archive" -C "$prefix" --strip-components=1

if [[ $link -eq 1 ]]; then
    mkdir -p "$HOME/.local/bin"
    ln -sfn "$prefix/AcrExtraStages" "$HOME/.local/bin/acr-extrastages"
    say "Linked ~/.local/bin/acr-extrastages"
    case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) echo "    note: ~/.local/bin is not on your PATH; run $prefix/AcrExtraStages instead" ;; esac
fi

scope="$(cat /proc/sys/kernel/yama/ptrace_scope 2>/dev/null || echo 0)"
if [[ "$scope" != "0" ]]; then
    echo
    echo "    note: kernel.yama.ptrace_scope is $scope. Creating the mappings file reads the running game's memory,"
    echo "    which needs this once per ACR update (see README, \"Linux\"):"
    echo "        sudo setcap cap_sys_ptrace=ep \"$prefix/tools/acr-mappings/acr-mappings\""
fi

echo
say "Done. Close the game, then run: acr-extrastages   (or $prefix/AcrExtraStages)"
