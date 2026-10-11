//! stage-validate: in-game validation of ported DR2 stages in Assetto Corsa Rally. See docs/validation.md.

mod check;
mod ground;
mod record;
mod route;
mod run;
mod svg;
mod synth;
mod transform;

use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use std::path::PathBuf;

#[derive(Parser)]
#[command(
    version,
    about = "Record ACR telemetry while driving a ported stage, then check the run against the stage IR"
)]
struct Cli {
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Subcommand)]
enum Cmd {
    /// Poll the ACR shared memory pages (read-only) and write a run CSV until Ctrl+C or the session ends.
    Record {
        #[arg(long)]
        out: PathBuf,
        /// Poll rate.
        #[arg(long, default_value_t = 50.0)]
        hz: f64,
        /// Fail instead of waiting when the game isn't running.
        #[arg(long)]
        no_wait: bool,
        /// Stop when shared memory doesn't change for this many seconds (0 = never).
        #[arg(long, default_value_t = 300.0)]
        idle_timeout: f64,
        /// Stop when the car position disappears for this many seconds after being on stage (0 = never).
        #[arg(long, default_value_t = 5.0)]
        lost_timeout: f64,
    },
    /// Check a recorded run against the stage IR placed with the stage-build transform.
    Check {
        #[arg(long)]
        run: PathBuf,
        /// Stage IR stage.json (DR2 native coordinates).
        #[arg(long)]
        stage: PathBuf,
        /// stage-build report JSON with the `placement` transform.
        #[arg(long)]
        transform: PathBuf,
        /// DR2 physics-ground glb for exact ground heights (default: the IR's collision.glb if present).
        #[arg(long)]
        ground: Option<PathBuf>,
        /// Don't load the ground mesh (use only the IR gate heights).
        #[arg(long)]
        no_ground: bool,
        /// SVG plot output (default: <run>.svg).
        #[arg(long)]
        svg: Option<PathBuf>,
        /// Also write the report as JSON.
        #[arg(long)]
        json: Option<PathBuf>,
        /// Off-route margin beyond the road edge (m).
        #[arg(long, default_value_t = 2.0)]
        off_margin: f64,
        /// Fall-through threshold below road/ground (m).
        #[arg(long, default_value_t = 1.0)]
        fall: f64,
        /// Sudden-drop vertical velocity threshold (m/s, negative).
        #[arg(long, default_value_t = -15.0, allow_hyphen_values = true)]
        drop_vy: f64,
        /// Speed profile bin size (m).
        #[arg(long, default_value_t = 250.0)]
        bin: f64,
    },
    /// Write a synthetic run that follows the centreline (to try `check` without the game).
    Synth {
        #[arg(long)]
        stage: PathBuf,
        #[arg(long)]
        transform: PathBuf,
        #[arg(long)]
        out: PathBuf,
        #[arg(long, default_value_t = 90.0)]
        speed: f64,
        /// Stop at this route distance (m).
        #[arg(long)]
        stop_at: Option<f64>,
        /// Inject a fall-through at this route distance (m).
        #[arg(long)]
        fall_at: Option<f64>,
        /// Leave the shm timing fields at 0.
        #[arg(long)]
        no_timing: bool,
    },
}

fn main() -> Result<()> {
    match Cli::parse().cmd {
        Cmd::Record {
            out,
            hz,
            no_wait,
            idle_timeout,
            lost_timeout,
        } => record::record(&record::RecordOpts {
            out,
            hz,
            wait: !no_wait,
            idle_timeout_s: idle_timeout,
            lost_timeout_s: lost_timeout,
        }),
        Cmd::Check {
            run,
            stage,
            transform,
            ground,
            no_ground,
            svg,
            json,
            off_margin,
            fall,
            drop_vy,
            bin,
        } => {
            let pl = transform::Placement::from_report(&transform)?;
            let ir = route::Ir::load(&stage)?;
            let route = route::Route::new(&ir, &pl);
            let ground_path = if no_ground {
                None
            } else {
                ground.or_else(|| ir.collision_glb())
            };
            let ground = match &ground_path {
                Some(p) => {
                    eprintln!("loading DR2 ground {} ...", p.display());
                    let g = ground::Ground::load(p, &pl)?;
                    eprintln!("  {} triangles", g.triangle_count());
                    Some(g)
                }
                None => None,
            };
            let r = run::read_run(&run)?;
            if !r.unknown_columns.is_empty() {
                eprintln!(
                    "note: ignoring unknown columns {}",
                    r.unknown_columns.join(", ")
                );
            }
            let opts = check::CheckOpts {
                off_margin_m: off_margin,
                fall_m: fall,
                drop_vy,
                bin_m: bin,
                ..Default::default()
            };
            let report = check::analyze(&route, ground.as_ref(), &r.samples, &opts);
            print!("{}", check::render_text(&report));
            let svg_path = svg.unwrap_or_else(|| run.with_extension("svg"));
            std::fs::write(&svg_path, svg::render(&route, &report))
                .with_context(|| svg_path.display().to_string())?;
            println!("plot -> {}", svg_path.display());
            if let Some(j) = json {
                std::fs::write(&j, serde_json::to_string_pretty(&report)?)?;
                println!("report -> {}", j.display());
            }
            std::process::exit(if report.verdict == check::Verdict::Fail {
                1
            } else {
                0
            });
        }
        Cmd::Synth {
            stage,
            transform,
            out,
            speed,
            stop_at,
            fall_at,
            no_timing,
        } => {
            let pl = transform::Placement::from_report(&transform)?;
            let route = route::Route::new(&route::Ir::load(&stage)?, &pl);
            let o = synth::SynthOpts {
                speed_kmh: speed,
                stop_at_s: stop_at,
                fall_at_s: fall_at,
                timing: !no_timing,
                ..Default::default()
            };
            let samples = synth::synthesize(&route, &o);
            let f = std::fs::File::create(&out).with_context(|| out.display().to_string())?;
            let mut w = run::RunWriter::new(
                std::io::BufWriter::new(f),
                &["stage-validate synth (synthetic run, not telemetry)".into()],
            )?;
            for s in &samples {
                w.write(s)?;
            }
            w.flush()?;
            eprintln!("{} samples -> {}", samples.len(), out.display());
            Ok(())
        }
    }
}
