//! The ACR install: Steam build id, game process name, start through Steam, polite close.

use anyhow::{bail, Context, Result};
use std::path::{Path, PathBuf};
use std::process::Command;

pub const APP_ID: u32 = 3917090;
pub const DEFAULT_GAME_DIR: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally";

/// `--game`, else `ACR_GAME_DIR`, else the default Steam library path.
pub fn game_dir(arg: Option<PathBuf>) -> PathBuf {
    arg.or_else(|| std::env::var_os("ACR_GAME_DIR").map(PathBuf::from))
        .unwrap_or_else(|| PathBuf::from(DEFAULT_GAME_DIR))
}

pub fn check_game_dir(game: &Path) -> Result<()> {
    if !game.join("acr").join("Content").join("Paks").is_dir() {
        bail!("{} does not look like an Assetto Corsa Rally install (no acr\\Content\\Paks)", game.display());
    }
    Ok(())
}

/// Value of `"key" "value"` in a Valve KeyValues text (first occurrence, any depth).
pub fn vdf_value(text: &str, key: &str) -> Option<String> {
    let want = format!("\"{key}\"");
    text.lines().find_map(|l| {
        let l = l.trim();
        let rest = l.strip_prefix(&want)?.trim();
        Some(rest.strip_prefix('"')?.strip_suffix('"')?.to_string())
    })
}

/// The Steam build id of the install, from `steamapps/appmanifest_3917090.acf` next to
/// `steamapps/common/<install dir>`.
pub fn build_id(game: &Path) -> Result<String> {
    let steamapps = game
        .parent()
        .and_then(Path::parent)
        .with_context(|| format!("{} is not inside a Steam library (steamapps/common/...)", game.display()))?;
    let manifest = steamapps.join(format!("appmanifest_{APP_ID}.acf"));
    let text = std::fs::read_to_string(&manifest)
        .with_context(|| format!("reading {} (is ACR installed through Steam?)", manifest.display()))?;
    if let Some(dir) = vdf_value(&text, "installdir") {
        let actual = game.file_name().map(|f| f.to_string_lossy().to_string()).unwrap_or_default();
        if !dir.eq_ignore_ascii_case(&actual) {
            bail!("{} describes install dir '{dir}', not '{actual}'", manifest.display());
        }
    }
    vdf_value(&text, "buildid")
        .filter(|b| !b.is_empty())
        .with_context(|| format!("no buildid in {}", manifest.display()))
}

/// Asks Steam to start the game (Steam starts too if needed).
pub fn launch() -> Result<()> {
    let url = format!("steam://rungameid/{APP_ID}");
    let status =
        Command::new("cmd").args(["/C", "start", "", &url]).status().context("starting the game through Steam")?;
    if !status.success() {
        bail!("`start {url}` failed ({status})");
    }
    Ok(())
}

/// Asks the game to close (WM_CLOSE through `taskkill` without `/F`); with `force`, terminates it.
pub fn close(pid: u32, force: bool) -> Result<()> {
    let mut cmd = Command::new("taskkill");
    cmd.args(["/PID", &pid.to_string()]);
    if force {
        cmd.arg("/F");
    }
    let out = cmd.output().context("running taskkill")?;
    if !out.status.success() {
        bail!("taskkill /PID {pid}{}: {}", if force { " /F" } else { "" }, String::from_utf8_lossy(&out.stderr).trim());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_acf_values() {
        let acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"3917090\"\n\t\"installdir\"\t\t\"Assetto Corsa Rally\"\n\t\"buildid\"\t\t\"25170642\"\n}\n";
        assert_eq!(vdf_value(acf, "buildid").as_deref(), Some("25170642"));
        assert_eq!(vdf_value(acf, "installdir").as_deref(), Some("Assetto Corsa Rally"));
        assert_eq!(vdf_value(acf, "missing"), None);
    }

    #[test]
    fn build_id_from_library_layout() {
        let root = std::env::temp_dir().join(format!("acr-mappings-test-{}", std::process::id()));
        let game = root.join("steamapps").join("common").join("Assetto Corsa Rally");
        std::fs::create_dir_all(&game).unwrap();
        std::fs::write(
            root.join("steamapps").join(format!("appmanifest_{APP_ID}.acf")),
            "\"AppState\"\n{\n\"installdir\" \"Assetto Corsa Rally\"\n\"buildid\" \"123\"\n}\n",
        )
        .unwrap();
        assert_eq!(build_id(&game).unwrap(), "123");
        assert!(build_id(&root).is_err());
        std::fs::remove_dir_all(&root).unwrap();
    }
}
