//! `validate`: CUE4Parse must load the vanilla DB tables and the Hafren South packages with the
//! usmap. Runs `acr-additive usmap-check`, which uses the same CUE4Parse setup as acr-additive
//! and acr-ref.

use anyhow::{bail, Context, Result};
use std::path::{Path, PathBuf};
use std::process::Command;

const TOOL_NAMES: [&str; 2] = ["acr-additive.exe", "AcrAdditive.exe"];

/// `--tool`, else `ACR_ADDITIVE`, else `work/bin/acr-additive/acr-additive.exe` in the current
/// directory or any parent, else next to this executable.
pub fn find_tool(arg: Option<PathBuf>) -> Result<PathBuf> {
    if let Some(p) = arg.or_else(|| std::env::var_os("ACR_ADDITIVE").map(PathBuf::from)) {
        return if p.is_file() { Ok(p) } else { bail!("{} does not exist", p.display()) };
    }
    let mut roots: Vec<PathBuf> = Vec::new();
    if let Ok(cwd) = std::env::current_dir() {
        roots.extend(cwd.ancestors().map(Path::to_path_buf));
    }
    for root in &roots {
        for n in TOOL_NAMES {
            let p = root.join("work").join("bin").join("acr-additive").join(n);
            if p.is_file() {
                return Ok(p);
            }
        }
    }
    if let Some(dir) = std::env::current_exe().ok().and_then(|e| e.parent().map(Path::to_path_buf)) {
        for n in TOOL_NAMES {
            for p in [dir.join(n), dir.join("acr-additive").join(n)] {
                if p.is_file() {
                    return Ok(p);
                }
            }
        }
    }
    bail!(
        "acr-additive not found: build it (dotnet publish tools/acr-additive -o work/bin/acr-additive), \
         or pass --tool / set ACR_ADDITIVE"
    )
}

/// Runs the check; output streams to the console. Returns whether it passed.
pub fn run(tool: &Path, packages_root: &Path, usmap: &Path) -> Result<bool> {
    println!("validate: {} usmap-check --game {} --usmap {}", tool.display(), packages_root.display(), usmap.display());
    let status = Command::new(tool)
        .arg("usmap-check")
        .arg("--game")
        .arg(packages_root)
        .arg("--usmap")
        .arg(usmap)
        .status()
        .with_context(|| format!("running {}", tool.display()))?;
    Ok(status.success())
}
