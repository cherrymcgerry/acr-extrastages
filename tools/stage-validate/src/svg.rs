//! Top-down plot of the route corridor vs the driven path (coloured by lateral offset), plus speed and lateral
//! offset along the route.
//!
//! Map axes: SVG x = physics x (UE X), SVG y = physics z (UE Y). Viewed from above (Y up, right-handed), that is
//! not mirrored: a right-hand corner on the road is a right-hand turn on the plot.

use crate::check::{fmt_time, Point, Report};
use crate::route::Route;
use std::fmt::Write as _;

const W: f64 = 1200.0;
const PAD: f64 = 40.0;
const CHART_H: f64 = 170.0;

fn colour(p: &Point) -> &'static str {
    if p.fall {
        "#d000d0"
    } else if p.edge_ratio <= 0.5 {
        "#1a9641"
    } else if p.edge_ratio <= 1.0 {
        "#a6d96a"
    } else if p.edge_ratio <= 1.5 {
        "#fdae61"
    } else {
        "#d7191c"
    }
}

fn esc(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}

pub fn render(route: &Route, r: &Report) -> String {
    let pts = &r.points;
    let xs = route
        .left
        .iter()
        .chain(&route.right)
        .map(|p| p[0])
        .chain(pts.iter().map(|p| p.x));
    let zs = route
        .left
        .iter()
        .chain(&route.right)
        .map(|p| p[2])
        .chain(pts.iter().map(|p| p.z));
    let (min_x, max_x) = xs.fold((f64::MAX, f64::MIN), |(a, b), v| (a.min(v), b.max(v)));
    let (min_z, max_z) = zs.fold((f64::MAX, f64::MIN), |(a, b), v| (a.min(v), b.max(v)));
    let span = (max_x - min_x).max(max_z - min_z).max(1.0);
    let scale = (W - 2.0 * PAD) / span;
    let map_h = (max_z - min_z) * scale + 2.0 * PAD + 60.0;
    let total_h = map_h + 2.0 * (CHART_H + PAD) + 40.0;
    let mx = |x: f64| PAD + (x - min_x) * scale;
    let mz = |z: f64| PAD + 60.0 + (z - min_z) * scale;

    let mut o = String::new();
    let _ = writeln!(
        o,
        r#"<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{total_h:.0}" viewBox="0 0 {W} {total_h:.0}" font-family="Segoe UI, Arial, sans-serif" font-size="12">"#
    );
    let _ = writeln!(o, r#"<rect width="100%" height="100%" fill="white"/>"#);
    let _ = writeln!(
        o,
        r#"<text x="{PAD}" y="22" font-size="16" font-weight="bold">{} ({}) - {}</text>"#,
        esc(&r.stage_name),
        esc(&r.stage),
        r.verdict
    );
    let summary = format!(
        "progress {:.0}/{:.0} m ({:.1}%), on road {:.1}%, off-route segments {}, falls {}, teleports {}, timing started {} finished {}{}",
        r.max_s,
        r.route_total_m,
        r.progress_pct,
        r.on_road_pct,
        r.off_route.len(),
        r.falls.len(),
        r.teleports.len(),
        r.timing.started,
        r.timing.finished,
        r.timing.shm_stage_time_s.or(r.timing.pos_stage_time_s).map(|t| format!(" ({})", fmt_time(t))).unwrap_or_default()
    );
    let _ = writeln!(o, r#"<text x="{PAD}" y="42">{}</text>"#, esc(&summary));

    // road corridor
    let mut poly = String::new();
    for p in route.left.iter().chain(route.right.iter().rev()) {
        let _ = write!(poly, "{:.1},{:.1} ", mx(p[0]), mz(p[2]));
    }
    let _ = writeln!(
        o,
        r##"<polygon points="{poly}" fill="#d9d9d9" stroke="#999" stroke-width="0.5"/>"##
    );
    let mut cl = String::new();
    for p in &route.centre {
        let _ = write!(cl, "{:.1},{:.1} ", mx(p[0]), mz(p[2]));
    }
    let _ = writeln!(
        o,
        r##"<polyline points="{cl}" fill="none" stroke="#555" stroke-width="0.8" stroke-dasharray="4 3"/>"##
    );

    // timing lines
    for t in &route.timing {
        let (l, rr) = (route.left[t.gate], route.right[t.gate]);
        let c = match t.kind.as_str() {
            "start" => "#2b83ba",
            "finish" => "#000",
            _ => "#7b3294",
        };
        let _ = writeln!(
            o,
            r#"<line x1="{:.1}" y1="{:.1}" x2="{:.1}" y2="{:.1}" stroke="{c}" stroke-width="3"><title>{} (gate {}, s {:.0} m)</title></line>"#,
            mx(l[0]),
            mz(l[2]),
            mx(rr[0]),
            mz(rr[2]),
            t.kind,
            t.gate,
            t.s
        );
        let _ = writeln!(
            o,
            r#"<text x="{:.1}" y="{:.1}" fill="{c}">{}</text>"#,
            mx(rr[0]) + 4.0,
            mz(rr[2]),
            t.kind
        );
    }

    // driven path: runs of same-colour segments as one polyline; teleports as thin dashed jumps
    let mut run = String::new();
    let mut run_c = "";
    let flush = |o: &mut String, run: &mut String, c: &str| {
        if !run.is_empty() {
            let _ = writeln!(
                o,
                r#"<polyline points="{run}" fill="none" stroke="{c}" stroke-width="2.2" stroke-linejoin="round"/>"#
            );
            run.clear();
        }
    };
    for k in 1..pts.len() {
        let (a, b) = (&pts[k - 1], &pts[k]);
        if b.teleport {
            flush(&mut o, &mut run, run_c);
            let _ = writeln!(
                o,
                r##"<line x1="{:.1}" y1="{:.1}" x2="{:.1}" y2="{:.1}" stroke="#2b83ba" stroke-width="1" stroke-dasharray="2 2"/>"##,
                mx(a.x),
                mz(a.z),
                mx(b.x),
                mz(b.z)
            );
            continue;
        }
        let c = colour(b);
        if c != run_c {
            flush(&mut o, &mut run, run_c);
            run_c = c;
        }
        if run.is_empty() {
            let _ = write!(run, "{:.1},{:.1} ", mx(a.x), mz(a.z));
        }
        let _ = write!(run, "{:.1},{:.1} ", mx(b.x), mz(b.z));
    }
    flush(&mut o, &mut run, run_c);
    for e in &r.falls {
        if let Some(p) = pts.iter().find(|p| p.t >= e.t_start) {
            let _ = writeln!(
                o,
                r##"<circle cx="{:.1}" cy="{:.1}" r="7" fill="none" stroke="#d000d0" stroke-width="2.5"><title>{}: {}</title></circle>"##,
                mx(p.x),
                mz(p.z),
                esc(&e.kind),
                esc(&e.detail)
            );
        }
    }
    if let (Some(a), Some(b)) = (pts.first(), pts.last()) {
        let _ = writeln!(
            o,
            r##"<circle cx="{:.1}" cy="{:.1}" r="4" fill="#2b83ba"><title>run start</title></circle>"##,
            mx(a.x),
            mz(a.z)
        );
        let _ = writeln!(
            o,
            r##"<rect x="{:.1}" y="{:.1}" width="8" height="8" fill="#000"><title>run end</title></rect>"##,
            mx(b.x) - 4.0,
            mz(b.z) - 4.0
        );
    }

    // legend
    let ly = map_h - 18.0;
    let items = [
        ("#1a9641", "|lat| <= 0.5 half-width"),
        ("#a6d96a", "<= road edge"),
        ("#fdae61", "<= 1.5x"),
        ("#d7191c", "> 1.5x"),
        ("#d000d0", "fall-through"),
        ("#d9d9d9", "IR road corridor"),
    ];
    let mut lx = PAD;
    for (c, label) in items {
        let _ = writeln!(
            o,
            r#"<rect x="{lx}" y="{:.1}" width="14" height="10" fill="{c}"/>"#,
            ly - 9.0
        );
        let _ = writeln!(
            o,
            r#"<text x="{:.1}" y="{ly:.1}">{}</text>"#,
            lx + 18.0,
            esc(label)
        );
        lx += 30.0 + label.len() as f64 * 6.5;
    }
    let bar = 100.0 * scale;
    let _ = writeln!(
        o,
        r#"<line x1="{:.1}" y1="{ly:.1}" x2="{:.1}" y2="{ly:.1}" stroke="black" stroke-width="2"/>"#,
        W - PAD - bar,
        W - PAD
    );
    let _ = writeln!(
        o,
        r#"<text x="{:.1}" y="{:.1}">100 m</text>"#,
        W - PAD - bar,
        ly - 5.0
    );

    // charts along s
    let total = r.route_total_m.max(1.0);
    let cx = |s: f64| PAD + s / total * (W - 2.0 * PAD);
    let chart = |o: &mut String,
                 top: f64,
                 title: &str,
                 vmin: f64,
                 vmax: f64,
                 value: &dyn Fn(&Point) -> f64,
                 bands: &[(f64, &str)]| {
        let cy = |v: f64| top + CHART_H - (v.clamp(vmin, vmax) - vmin) / (vmax - vmin) * CHART_H;
        let _ = writeln!(
            o,
            r##"<rect x="{PAD}" y="{top:.1}" width="{:.1}" height="{CHART_H}" fill="none" stroke="#999"/>"##,
            W - 2.0 * PAD
        );
        let _ = writeln!(
            o,
            r#"<text x="{PAD}" y="{:.1}" font-weight="bold">{title}</text>"#,
            top - 6.0
        );
        for &(v, label) in bands {
            let y = cy(v);
            let _ = writeln!(
                o,
                r##"<line x1="{PAD}" y1="{y:.1}" x2="{:.1}" y2="{y:.1}" stroke="#ccc" stroke-dasharray="3 3"/><text x="{:.1}" y="{:.1}" fill="#777">{label}</text>"##,
                W - PAD,
                W - PAD + 2.0,
                y + 4.0,
            );
        }
        for t in &route.timing {
            let _ = writeln!(
                o,
                r##"<line x1="{x:.1}" y1="{top:.1}" x2="{x:.1}" y2="{:.1}" stroke="#7b3294" stroke-width="0.8"/>"##,
                top + CHART_H,
                x = cx(t.s)
            );
        }
        let mut d = String::new();
        let mut last_s = f64::NAN;
        for p in pts.iter().filter(|p| !p.fall) {
            let cmd = if p.teleport || (p.s - last_s).abs() > 50.0 || last_s.is_nan() {
                "M"
            } else {
                "L"
            };
            let _ = write!(d, "{cmd}{:.1},{:.1} ", cx(p.s), cy(value(p)));
            last_s = p.s;
        }
        let _ = writeln!(
            o,
            r##"<path d="{d}" fill="none" stroke="#2b83ba" stroke-width="1.2"/>"##
        );
        for k in 0..=((total / 500.0) as i64) {
            let s = k as f64 * 500.0;
            let _ = writeln!(
                o,
                r##"<text x="{:.1}" y="{:.1}" fill="#777" text-anchor="middle">{:.1}</text>"##,
                cx(s),
                top + CHART_H + 14.0,
                s / 1000.0
            );
        }
    };
    let vmax = (r.max_speed_kmh / 20.0).ceil().max(1.0) * 20.0;
    chart(
        &mut o,
        map_h + 20.0,
        "speed (km/h) vs route distance (km)",
        0.0,
        vmax,
        &|p| p.speed_kmh,
        &[
            (vmax / 2.0, &format!("{:.0}", vmax / 2.0)),
            (vmax, &format!("{vmax:.0}")),
        ],
    );
    chart(
        &mut o,
        map_h + CHART_H + PAD + 40.0,
        "lateral offset / half-width (+ right, - left) vs route distance (km)",
        -3.0,
        3.0,
        &|p| p.lateral.signum() * p.edge_ratio,
        &[(1.0, "edge"), (0.0, "0"), (-1.0, "edge")],
    );
    let _ = writeln!(o, "</svg>");
    o
}
