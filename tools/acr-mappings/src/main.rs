//! acr-mappings: writes the CUE4Parse `.usmap` for the installed ACR build by reading the
//! game's reflection data from outside the process (read-only). See README.md.

mod cache;
mod diff;
mod engine;
mod process;
mod schema;
mod steam;
mod usmap;
mod validate;
mod walk;

use acr_ue::sigs::Signatures;
use anyhow::{bail, Context, Result};
use clap::{Args, Parser, Subcommand};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

/// acr-rewind's `config/signatures.toml` at the pinned `acr_ue` revision.
const EMBEDDED_SIGNATURES: &str = include_str!("../signatures.toml");

/// Types the acr-dirt tools read; a dump without them is not usable.
const REQUIRED_TYPES: &[&str] = &[
    "Object",
    "DataTable",
    "TableRowBase",
    "DBTableRowBase",
    "DBReference",
    "DBLevel",
    "DBTrack",
    "DBTrackVariant",
    "DBTrackLocation",
    "DBTexture",
    "SoftObjectPath",
    "World",
    "Level",
    "Actor",
    "StaticMeshComponent",
    "BakedDataContainer",
];

/// Native types whose property chains must be linked before the dump starts (engine + game
/// module + the ACR database rows).
const READY_TYPES: &[&str] =
    &["Actor", "DataTable", "SoftObjectPath", "DBTrack", "DBTrackVariant", "BakedDataContainer"];

#[derive(Parser)]
#[command(
    name = "acr-mappings",
    version,
    about = "CUE4Parse .usmap for the installed Assetto Corsa Rally build (read-only)"
)]
struct Cli {
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Args)]
struct GameArgs {
    /// ACR install folder (default: ACR_GAME_DIR, else the default Steam library).
    #[arg(long)]
    game: Option<PathBuf>,
    /// Folder for <buildId>.usmap + <buildId>.json.
    #[arg(long, default_value = cache::DEFAULT_DIR)]
    cache_dir: PathBuf,
}

#[derive(Subcommand)]
enum Cmd {
    /// Reads the running game's reflection data and writes the usmap (cached per Steam build id).
    Dump {
        #[command(flatten)]
        g: GameArgs,
        /// Also copy the usmap here.
        #[arg(long)]
        out: Option<PathBuf>,
        /// Only attach to an already running game; fail if it is not running.
        #[arg(long, conflicts_with = "exe_wait")]
        attach: bool,
        /// Do not start the game; wait for the user to start it.
        #[arg(long)]
        exe_wait: bool,
        /// Regenerate even if the cache has a file for this build.
        #[arg(long)]
        force: bool,
        /// Leave the game running if this tool started it.
        #[arg(long)]
        keep_running: bool,
        /// Seconds to wait for the game to start and finish loading its types.
        #[arg(long, default_value_t = 300)]
        timeout: u64,
        /// signatures.toml to use (default: ACR_SIGNATURES, else the copy built into this tool).
        #[arg(long)]
        signatures: Option<PathBuf>,
        /// Try even if the installed build is not the one the signatures were verified on.
        #[arg(long)]
        allow_build_mismatch: bool,
    },
    /// Loads the five vanilla DB tables and the Hafren South packages with CUE4Parse and the usmap.
    Validate {
        #[command(flatten)]
        g: GameArgs,
        /// usmap to check (default: the cached file for the installed build).
        #[arg(long)]
        usmap: Option<PathBuf>,
        /// Mods-free game folder to read packages from (default: --game; ~mods is never read).
        #[arg(long)]
        vanilla: Option<PathBuf>,
        /// acr-additive executable (default: ACR_ADDITIVE, else work/bin/acr-additive).
        #[arg(long)]
        tool: Option<PathBuf>,
    },
    /// Prints the cached usmap path for the installed build; exit code 1 if missing or not validated.
    Path {
        #[command(flatten)]
        g: GameArgs,
        /// Accept a cached file that has not passed `validate`.
        #[arg(long)]
        unvalidated: bool,
    },
    /// Header, counts and selected types of a usmap file.
    Inspect {
        usmap: PathBuf,
        #[arg(long)]
        r#struct: Vec<String>,
        #[arg(long)]
        r#enum: Vec<String>,
        /// Lists struct and enum names containing this text.
        #[arg(long)]
        find: Option<String>,
    },
    /// Semantic difference between two usmap files (e.g. against a Dumper-7 file).
    Diff {
        a: PathBuf,
        b: PathBuf,
        #[arg(long, default_value_t = 40)]
        limit: usize,
        /// Exit code 1 if a type present in both files differs.
        #[arg(long)]
        strict: bool,
    },
}

fn load(path: &Path) -> Result<(usmap::Header, schema::Schema)> {
    let bytes = std::fs::read(path).with_context(|| format!("reading {}", path.display()))?;
    usmap::read(&bytes).with_context(|| format!("parsing {}", path.display()))
}

fn load_signatures(arg: Option<PathBuf>) -> Result<(Signatures, String)> {
    let path = arg.or_else(|| std::env::var_os("ACR_SIGNATURES").map(PathBuf::from));
    let (text, from) = match path {
        Some(p) => {
            (std::fs::read_to_string(&p).with_context(|| format!("reading {}", p.display()))?, p.display().to_string())
        }
        None => (EMBEDDED_SIGNATURES.to_string(), "built-in (acr-rewind config/signatures.toml)".to_string()),
    };
    let sigs = Signatures::from_toml_str(&text).with_context(|| format!("signatures from {from}"))?;
    Ok((sigs, from))
}

fn single_pid(name: &str) -> Result<Option<u32>> {
    let pids = process::find_pids(name)?;
    match pids.len() {
        0 => Ok(None),
        1 => Ok(Some(pids[0])),
        _ => bail!("{} processes named {name}: {pids:?}; close the extra ones", pids.len()),
    }
}

fn wait_for_pid(name: &str, deadline: Instant) -> Result<u32> {
    let mut said = false;
    loop {
        if let Some(pid) = single_pid(name)? {
            return Ok(pid);
        }
        if Instant::now() > deadline {
            bail!("{name} did not start in time");
        }
        if !said {
            println!("waiting for {name} to start ...");
            said = true;
        }
        std::thread::sleep(Duration::from_secs(1));
    }
}

struct DumpResult {
    schema: schema::Schema,
    stats: walk::WalkStats,
    resolved: engine::Resolved,
}

/// Polls until GObjects / FNamePool resolve and the number of loaded types stops changing,
/// then walks the reflection data.
fn dump_process(proc_: &process::RemoteProcess, sigs: &Signatures, deadline: Instant) -> Result<DumpResult> {
    let mem = process::PageCache::new(proc_, 1 << 16);
    let mut last: Option<usize> = None;
    let mut stable = 0;
    let mut last_err = String::new();
    loop {
        if !proc_.is_alive() {
            bail!("the game exited while waiting for it to load");
        }
        mem.clear();
        let attempt = proc_.modules().and_then(|mods| engine::open_ue(&mem, &mods, sigs));
        match attempt {
            Ok((ue, resolved)) => match walk::readiness(&ue, READY_TYPES) {
                Some(r) => {
                    println!(
                        "  {} objects, {} classes + script structs, {} enums{}",
                        r.objects,
                        r.structs,
                        r.enums,
                        if r.pending.is_empty() { String::new() } else { format!("; waiting for {:?}", r.pending) }
                    );
                    if r.pending.is_empty() && r.enums > 0 && last == Some(r.structs) {
                        stable += 1;
                    } else {
                        stable = 0;
                    }
                    last = Some(r.structs);
                    if stable >= 2 {
                        mem.clear();
                        let (schema, stats) = walk::walk(&ue, walk::PropLayout::default())?;
                        return Ok(DumpResult { schema, stats, resolved });
                    }
                }
                None => last_err = "GObjects resolved but has no 'Class' UClass yet".into(),
            },
            Err(e) => {
                let msg = format!("{e:#}");
                if msg != last_err {
                    println!("  not ready: {msg}");
                }
                last_err = msg;
            }
        }
        if Instant::now() > deadline {
            bail!(
                "timed out waiting for the game's types to load (last state: {}). If the game was patched, \
                 the signatures may need an update; Dumper-7 + --usmap remains the fallback",
                if last_err.is_empty() { "type count still changing".to_string() } else { last_err }
            );
        }
        std::thread::sleep(Duration::from_secs(3));
    }
}

fn check_required(schema: &schema::Schema) -> Result<()> {
    let have = schema.struct_map();
    let missing: Vec<&str> = REQUIRED_TYPES.iter().copied().filter(|t| !have.contains_key(t)).collect();
    if !missing.is_empty() {
        bail!(
            "the dump lacks types the tools need: {missing:?} ({} structs found). The reflection layout \
             probably does not match this build",
            schema.structs.len()
        );
    }
    let bad: Vec<String> = schema
        .structs
        .iter()
        .filter(|s| REQUIRED_TYPES.contains(&s.name.as_str()))
        .flat_map(|s| s.properties.iter().filter(|p| p.ty.has_unknown()).map(move |p| format!("{}.{}", s.name, p.name)))
        .collect();
    if !bad.is_empty() {
        bail!("required types have properties of unknown type: {bad:?}");
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn cmd_dump(
    g: GameArgs,
    out: Option<PathBuf>,
    attach: bool,
    exe_wait: bool,
    force: bool,
    keep_running: bool,
    timeout: u64,
    signatures: Option<PathBuf>,
    allow_build_mismatch: bool,
) -> Result<()> {
    let game = steam::game_dir(g.game);
    steam::check_game_dir(&game)?;
    let build = steam::build_id(&game)?;
    let (sigs, sigs_from) = load_signatures(signatures)?;
    println!("ACR build {build} at {}", game.display());
    println!("signatures: {sigs_from}, verified on build '{}'", sigs.verified_build);
    if sigs.build_matches(Some(&build)) != Some(true) {
        let msg = format!(
            "installed ACR build {build} is not the build the signatures were verified on ('{}'). Update \
             acr-rewind's config/signatures.toml for this build (acr-rewind docs/updating-after-a-patch.md) and \
             bump the pinned acr_ue revision / built-in copy, or use a Dumper-7 usmap (--usmap) meanwhile",
            sigs.verified_build
        );
        if !allow_build_mismatch {
            bail!("{msg}; --allow-build-mismatch tries anyway");
        }
        println!("warning: {msg}");
    }

    let cached = cache::usmap_path(&g.cache_dir, &build);
    let copy_out = |from: &Path| -> Result<()> {
        if let Some(o) = &out {
            if o.as_path() != from {
                cache::write_atomic(o, &std::fs::read(from)?)?;
                std::fs::copy(cache::sidecar_path(from), cache::sidecar_path(o)).ok();
                println!("copied to {}", o.display());
            }
        }
        Ok(())
    };
    if !force && cached.is_file() && cache::read_sidecar(&cached).is_some_and(|s| s.build_id == build) {
        load(&cached).context("cached usmap is unreadable; rerun with --force")?;
        println!("cached: {} (use --force to regenerate)", cached.display());
        return copy_out(&cached);
    }

    let exe = sigs.module.name.clone();
    let deadline = Instant::now() + Duration::from_secs(timeout);
    let mut launched = false;
    let pid = match single_pid(&exe)? {
        Some(pid) => {
            println!("attaching to running {exe} (pid {pid})");
            pid
        }
        None if attach => bail!("{exe} is not running (--attach)"),
        None if exe_wait => wait_for_pid(&exe, deadline)?,
        None => {
            println!("ACR will start briefly to read its data layout");
            steam::launch()?;
            launched = true;
            wait_for_pid(&exe, deadline)?
        }
    };
    let proc_ = process::RemoteProcess::open(pid)?;
    let result = dump_process(&proc_, &sigs, deadline);
    if launched && !keep_running {
        println!("closing the game (pid {pid})");
        let _ = steam::close(pid, false);
        let until = Instant::now() + Duration::from_secs(60);
        while proc_.is_alive() && Instant::now() < until {
            std::thread::sleep(Duration::from_millis(500));
        }
        if proc_.is_alive() {
            println!("the game did not close within 60 s; terminating it");
            steam::close(pid, true)?;
        }
    }
    let DumpResult { schema, stats, resolved } = result?;
    println!(
        "GObjects {:#x} ({}), FNamePool {:#x} ({})",
        resolved.globals.gobjects, resolved.gobjects_how, resolved.globals.fnamepool, resolved.fnamepool_how
    );
    println!(
        "{} objects: {} structs, {} enums, {} properties ({} duplicate names skipped)",
        stats.objects,
        stats.structs,
        stats.enums,
        schema.property_count(),
        stats.duplicate_names
    );
    if !stats.unknown_field_classes.is_empty() {
        println!("warning: unmapped property classes (written as Unknown): {:?}", stats.unknown_field_classes);
    }
    if !stats.truncated_structs.is_empty() {
        println!(
            "warning: {} structs with an unreadable property chain, e.g. {:?}",
            stats.truncated_structs.len(),
            &stats.truncated_structs[..stats.truncated_structs.len().min(5)]
        );
    }
    check_required(&schema)?;

    let bytes = usmap::write(&schema)?;
    usmap::read(&bytes).context("self-check of the written usmap")?;
    cache::write_atomic(&cached, &bytes)?;
    let now = cache::now_unix();
    let sidecar = cache::Sidecar {
        build_id: build,
        created: now,
        created_utc: cache::utc(now),
        tool: format!("acr-mappings {}", env!("CARGO_PKG_VERSION")),
        usmap_version: usmap::WRITE_VERSION,
        signatures_verified_build: sigs.verified_build.clone(),
        gobjects: format!("{:#x} ({})", resolved.globals.gobjects - resolved.module_base, resolved.gobjects_how),
        fnamepool: format!("{:#x} ({})", resolved.globals.fnamepool - resolved.module_base, resolved.fnamepool_how),
        objects: stats.objects,
        structs: stats.structs,
        enums: stats.enums,
        properties: schema.property_count(),
        unknown_field_classes: stats.unknown_field_classes.keys().cloned().collect(),
        validated: None,
    };
    cache::write_sidecar(&cached, &sidecar)?;
    println!("wrote {} ({} bytes)", cached.display(), bytes.len());
    copy_out(&cached)
}

fn cmd_validate(
    g: GameArgs,
    usmap_arg: Option<PathBuf>,
    vanilla: Option<PathBuf>,
    tool: Option<PathBuf>,
) -> Result<()> {
    let game = steam::game_dir(g.game);
    let usmap_path = match usmap_arg {
        Some(p) => p,
        None => cache::usmap_path(&g.cache_dir, &steam::build_id(&game)?),
    };
    let (_, schema) = load(&usmap_path)?;
    check_required(&schema)?;
    let root = vanilla.unwrap_or_else(|| game.clone());
    steam::check_game_dir(&root)?;
    let tool = validate::find_tool(tool)?;
    let ok = validate::run(&tool, &root, &usmap_path)?;
    if let Some(mut s) = cache::read_sidecar(&usmap_path) {
        let now = cache::now_unix();
        s.validated = Some(cache::Validation {
            ok,
            at_utc: cache::utc(now),
            game: root.display().to_string(),
            summary: if ok {
                "CUE4Parse loaded the DB tables and Hafren South packages".into()
            } else {
                "failed".into()
            },
        });
        cache::write_sidecar(&usmap_path, &s)?;
    }
    if !ok {
        bail!("validation FAILED for {}", usmap_path.display());
    }
    println!("validation passed: {}", usmap_path.display());
    Ok(())
}

fn cmd_path(g: GameArgs, unvalidated: bool) -> Result<bool> {
    let game = steam::game_dir(g.game);
    let build = steam::build_id(&game)?;
    let p = cache::usmap_path(&g.cache_dir, &build);
    let side = cache::read_sidecar(&p);
    let ok = p.is_file()
        && side
            .as_ref()
            .is_some_and(|s| s.build_id == build && (unvalidated || s.validated.as_ref().is_some_and(|v| v.ok)));
    if ok {
        println!("{}", std::path::absolute(&p).unwrap_or(p).display());
    } else {
        eprintln!(
            "no {}usmap cached for ACR build {build} in {}",
            if unvalidated { "" } else { "validated " },
            g.cache_dir.display()
        );
    }
    Ok(ok)
}

fn cmd_inspect(usmap: PathBuf, structs: Vec<String>, enums: Vec<String>, find: Option<String>) -> Result<()> {
    let (h, s) = load(&usmap)?;
    println!("{h:?}");
    println!("{} enums, {} structs, {} properties", s.enums.len(), s.structs.len(), s.property_count());
    if let Some(f) = &find {
        for st in s.structs.iter().filter(|x| x.name.contains(f.as_str())) {
            println!("struct {} : {:?} ({} properties)", st.name, st.super_name, st.properties.len());
        }
        for e in s.enums.iter().filter(|x| x.name.contains(f.as_str())) {
            println!("enum {} ({} entries)", e.name, e.entries.len());
        }
    }
    let sm = s.struct_map();
    for n in &structs {
        match sm.get(n.as_str()) {
            Some(st) => {
                println!("struct {} : {:?}", st.name, st.super_name);
                for p in &st.properties {
                    println!("  {} [{}] {}", p.name, p.array_dim, p.ty.describe());
                }
            }
            None => println!("struct {n}: not present"),
        }
    }
    let em = s.enum_map();
    for n in &enums {
        match em.get(n.as_str()) {
            Some(e) => println!("enum {}: {:?}", e.name, e.entries),
            None => println!("enum {n}: not present"),
        }
    }
    Ok(())
}

fn main() -> Result<()> {
    match Cli::parse().cmd {
        Cmd::Dump { g, out, attach, exe_wait, force, keep_running, timeout, signatures, allow_build_mismatch } => {
            cmd_dump(g, out, attach, exe_wait, force, keep_running, timeout, signatures, allow_build_mismatch)
        }
        Cmd::Validate { g, usmap, vanilla, tool } => cmd_validate(g, usmap, vanilla, tool),
        Cmd::Path { g, unvalidated } => {
            if !cmd_path(g, unvalidated)? {
                std::process::exit(1);
            }
            Ok(())
        }
        Cmd::Inspect { usmap, r#struct, r#enum, find } => cmd_inspect(usmap, r#struct, r#enum, find),
        Cmd::Diff { a, b, limit, strict } => {
            let (_, sa) = load(&a)?;
            let (_, sb) = load(&b)?;
            let d = diff::diff(&sa, &sb);
            print!("{}", d.report("A", "B", limit));
            if strict && !d.common_identical() {
                std::process::exit(1);
            }
            Ok(())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn embedded_signatures_parse() {
        let (s, from) = load_signatures(None).unwrap();
        assert!(from.starts_with("built-in"));
        assert_eq!(s.module.name, "acr.exe");
        assert!(!s.verified_build.is_empty());
        assert!(s.raw_offsets.gobjects_rva.is_some());
    }

    /// The built-in copy must match the sibling acr-rewind checkout when one is present.
    #[test]
    fn embedded_signatures_match_sibling_checkout() {
        let sibling = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../acr-rewind/config/signatures.toml");
        if let Ok(text) = std::fs::read_to_string(&sibling) {
            assert_eq!(
                text.replace("\r\n", "\n"),
                EMBEDDED_SIGNATURES.replace("\r\n", "\n"),
                "tools/acr-mappings/signatures.toml is out of date with {}",
                sibling.display()
            );
        }
    }
}
