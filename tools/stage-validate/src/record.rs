//! `record`: polls the ACR shared memory pages (read-only) and writes a run CSV.
//!
//! The physics page carries no world position, so the car position comes from graphics `carCoordinates`
//! (`acr_shm::car_position`: the player's entry, else slot 0, which is what offline solo fills). Quirks on build
//! 25170642 (acr-rewind docs/technical-reference.md section 7): `graphics.status` reads OFF while live, so it is
//! logged but not used to gate recording; `carCoordinates` trails physics by ~1 update; the physics page can
//! read all zero.

use crate::run::{field_stats, RunWriter, Sample};
use acr_shm::layout::{wstr, RawGraphics, RawPhysics};
use acr_shm::{car_position, quirks, CarPosSource, SharedMemory, ShmError};
use anyhow::{Context, Result};
use std::io::Write;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub struct RecordOpts {
    pub out: PathBuf,
    pub hz: f64,
    pub wait: bool,
    /// Stop when nothing changed in shm for this long (0 = never).
    pub idle_timeout_s: f64,
    /// Stop when the car position disappears (back to menus) for this long after having been on stage (0 = never).
    pub lost_timeout_s: f64,
}

pub fn sample_from(t_s: f64, p: &RawPhysics, g: &RawGraphics) -> Sample {
    let (p, g) = (*p, *g);
    let (pos, pos_src) = match car_position(&g) {
        Some((v, CarPosSource::Player)) => (v, 1),
        Some((v, CarPosSource::Car0)) => (v, 2),
        None => ([0.0; 3], 0),
    };
    let (vel, acc) = (p.velocity, p.accG);
    let (load, susp, slip, sr, sa, ang, dirty) = (
        p.wheelLoad,
        p.suspensionTravel,
        p.wheelSlip,
        p.slipRatio,
        p.slipAngle,
        p.wheelAngularSpeed,
        p.tyreDirtyLevel,
    );
    let (cp, cn, dmg, sdmg) = (
        p.tyreContactPoint,
        p.tyreContactNormal,
        p.carDamage,
        p.suspensionDamage,
    );
    Sample {
        t_s,
        phys_packet: p.packetId,
        gfx_packet: g.packetId,
        status: g.status,
        session: g.session,
        pos_src,
        pos_x: pos[0],
        pos_y: pos[1],
        pos_z: pos[2],
        vel_x: vel[0],
        vel_y: vel[1],
        vel_z: vel[2],
        speed_kmh: p.speedKmh,
        gear: quirks::gear(p.gear),
        rpm: p.rpms,
        gas: p.gas,
        brake: p.brake,
        clutch: p.clutch,
        steer: p.steerAngle,
        heading: p.heading,
        pitch: p.pitch,
        roll: p.roll,
        cg_height: p.cgHeight,
        acc_g_x: acc[0],
        acc_g_y: acc[1],
        acc_g_z: acc[2],
        tyres_out: p.numberOfTyresOut,
        load_fl: load[0],
        load_fr: load[1],
        load_rl: load[2],
        load_rr: load[3],
        susp_fl: susp[0],
        susp_fr: susp[1],
        susp_rl: susp[2],
        susp_rr: susp[3],
        slip_fl: slip[0],
        slip_fr: slip[1],
        slip_rl: slip[2],
        slip_rr: slip[3],
        slip_ratio_fl: sr[0],
        slip_ratio_fr: sr[1],
        slip_ratio_rl: sr[2],
        slip_ratio_rr: sr[3],
        slip_angle_fl: sa[0],
        slip_angle_fr: sa[1],
        slip_angle_rl: sa[2],
        slip_angle_rr: sa[3],
        wheel_rads_fl: ang[0],
        wheel_rads_fr: ang[1],
        wheel_rads_rl: ang[2],
        wheel_rads_rr: ang[3],
        dirty_fl: dirty[0],
        dirty_fr: dirty[1],
        dirty_rl: dirty[2],
        dirty_rr: dirty[3],
        cp_x_fl: cp[0][0],
        cp_y_fl: cp[0][1],
        cp_z_fl: cp[0][2],
        cp_x_fr: cp[1][0],
        cp_y_fr: cp[1][1],
        cp_z_fr: cp[1][2],
        cp_x_rl: cp[2][0],
        cp_y_rl: cp[2][1],
        cp_z_rl: cp[2][2],
        cp_x_rr: cp[3][0],
        cp_y_rr: cp[3][1],
        cp_z_rr: cp[3][2],
        cn_y_fl: cn[0][1],
        cn_y_fr: cn[1][1],
        cn_y_rl: cn[2][1],
        cn_y_rr: cn[3][1],
        damage_0: dmg[0],
        damage_1: dmg[1],
        damage_2: dmg[2],
        damage_3: dmg[3],
        damage_4: dmg[4],
        susp_damage_fl: sdmg[0],
        susp_damage_fr: sdmg[1],
        susp_damage_rl: sdmg[2],
        susp_damage_rr: sdmg[3],
        surface_grip: g.surfaceGrip,
        i_current_ms: g.iCurrentTime,
        i_last_ms: g.iLastTime,
        i_best_ms: g.iBestTime,
        completed_laps: g.completedLaps,
        number_of_laps: g.numberOfLaps,
        sector_index: g.currentSectorIndex,
        last_sector_ms: g.lastSectorTime,
        i_split_ms: g.iSplit,
        distance_traveled: g.distanceTraveled,
        normalized_pos: g.normalizedCarPosition,
        session_time_left: g.sessionTimeLeft,
        penalty_time: g.penaltyTime,
        is_valid_lap: g.isValidLap,
        flag: g.flag,
        is_in_pit: g.isInPit,
    }
}

fn open(wait: bool, stop: &AtomicBool) -> Result<Option<SharedMemory>> {
    let mut said = false;
    loop {
        match SharedMemory::open() {
            Ok(m) => return Ok(Some(m)),
            Err(ShmError::NotRunning(page)) if wait => {
                if !said {
                    eprintln!("waiting for Assetto Corsa Rally ({page} not found yet; Ctrl+C to quit) ...");
                    said = true;
                }
            }
            Err(e) => return Err(e.into()),
        }
        if stop.load(Ordering::SeqCst) {
            return Ok(None);
        }
        std::thread::sleep(Duration::from_millis(500));
    }
}

/// Change detector: a row is written only when a page updated or the car moved.
fn key(s: &Sample) -> (i32, i32, [u32; 3], i32) {
    (
        s.phys_packet,
        s.gfx_packet,
        [s.pos_x.to_bits(), s.pos_y.to_bits(), s.pos_z.to_bits()],
        s.i_current_ms,
    )
}

pub fn record(opts: &RecordOpts) -> Result<()> {
    let stop = Arc::new(AtomicBool::new(false));
    {
        let stop = stop.clone();
        ctrlc::set_handler(move || stop.store(true, Ordering::SeqCst))
            .context("installing Ctrl+C handler")?;
    }
    let Some(shm) = open(opts.wait, &stop)? else {
        return Ok(());
    };
    let st = shm.static_info();
    let started = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let comments = vec![
        format!("stage-validate record v{}", env!("CARGO_PKG_VERSION")),
        format!("started_unix {started}"),
        format!(
            "static: sm_version={:?} ac_version={:?} car={:?} track={:?} config={:?}",
            st.sm_version, st.ac_version, st.car_model, st.track, st.track_configuration
        ),
        "frame: ACR physics/sim metres, Y up (sim = (UE.x, UE.z, UE.y) / 100); pos from graphics carCoordinates".into(),
    ];
    if let Some(dir) = opts.out.parent().filter(|d| !d.as_os_str().is_empty()) {
        std::fs::create_dir_all(dir)?;
    }
    let file = std::fs::File::create(&opts.out)
        .with_context(|| format!("creating {}", opts.out.display()))?;
    let mut w = RunWriter::new(std::io::BufWriter::new(file), &comments)?;
    eprintln!(
        "recording to {} at {} Hz; Ctrl+C to stop",
        opts.out.display(),
        opts.hz
    );

    let period = Duration::from_secs_f64(1.0 / opts.hz.clamp(1.0, 500.0));
    let t0 = Instant::now();
    let mut next = t0;
    let mut last_key = None;
    let mut last_change = t0;
    let mut last_flush = t0;
    let mut last_status = t0;
    let mut on_stage_since: Option<Instant> = None;
    let mut lost_since: Option<Instant> = None;
    let mut rows: Vec<Sample> = Vec::new();
    let mut n_rows = 0usize;
    let mut reason = "Ctrl+C";
    while !stop.load(Ordering::SeqCst) {
        let now = Instant::now();
        let s = sample_from(
            now.duration_since(t0).as_secs_f64(),
            &shm.read_raw_physics(),
            &shm.read_raw_graphics(),
        );
        let k = key(&s);
        if last_key != Some(k) {
            last_key = Some(k);
            last_change = now;
            if s.has_pos() || s.phys_ok() {
                w.write(&s)?;
                n_rows += 1;
                if rows.len() < 200_000 {
                    rows.push(s.clone());
                }
            }
        }
        if s.has_pos() {
            on_stage_since.get_or_insert(now);
            lost_since = None;
        } else if on_stage_since.is_some() {
            let since = *lost_since.get_or_insert(now);
            if opts.lost_timeout_s > 0.0
                && now.duration_since(since).as_secs_f64() > opts.lost_timeout_s
            {
                reason = "car left the stage (no position)";
                break;
            }
        }
        if opts.idle_timeout_s > 0.0
            && now.duration_since(last_change).as_secs_f64() > opts.idle_timeout_s
        {
            reason = "shared memory idle (game paused, closed or in menus)";
            break;
        }
        if now.duration_since(last_flush) > Duration::from_secs(1) {
            w.flush()?;
            last_flush = now;
        }
        if now.duration_since(last_status) > Duration::from_millis(500) {
            last_status = now;
            let pos = if s.has_pos() {
                format!("({:.1}, {:.1}, {:.1})", s.pos_x, s.pos_y, s.pos_z)
            } else {
                "-".into()
            };
            let lap = wstr(&{ shm.read_raw_graphics().currentTime });
            eprint!(
                "\r{:7.1}s rows {:6} | {:5.1} km/h gear {:2} | pos {:28} | time {:10} laps {} | status {}   ",
                s.t_s, n_rows, s.speed_kmh, s.gear, pos, lap, s.completed_laps, s.status
            );
            std::io::stderr().flush().ok();
        }
        next += period;
        let now = Instant::now();
        if next > now {
            std::thread::sleep(next - now);
        } else {
            next = now;
        }
    }
    w.flush()?;
    eprintln!();
    eprintln!(
        "stopped: {reason}; {n_rows} rows in {:.1} s -> {}",
        t0.elapsed().as_secs_f64(),
        opts.out.display()
    );

    let st = shm.static_info();
    let fs = field_stats(&rows);
    let meta = serde_json::json!({
        "rows": n_rows,
        "duration_s": t0.elapsed().as_secs_f64(),
        "stop_reason": reason,
        "static": {
            "sm_version": st.sm_version, "ac_version": st.ac_version, "car_model": st.car_model,
            "track": st.track, "track_configuration": st.track_configuration, "max_rpm": st.max_rpm,
            "tyre_radius": st.tyre_radius,
        },
        "always_zero_columns": fs.always_zero,
        "constant_columns": fs.constant_nonzero.iter().map(|(c, v)| serde_json::json!({ "column": c, "value": v })).collect::<Vec<_>>(),
    });
    let meta_path = PathBuf::from(format!("{}.meta.json", opts.out.display()));
    std::fs::write(&meta_path, serde_json::to_string_pretty(&meta)?)?;
    if !fs.always_zero.is_empty() {
        eprintln!(
            "columns that stayed 0 (likely not published by ACR): {}",
            fs.always_zero.join(", ")
        );
    }
    eprintln!("metadata -> {}", meta_path.display());
    Ok(())
}
