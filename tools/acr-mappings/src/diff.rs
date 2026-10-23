//! Semantic comparison of two schemas (e.g. our dump against a Dumper-7 usmap).

use crate::schema::{Schema, Struct};
use std::fmt::Write;

#[derive(Debug, Default)]
pub struct Diff {
    pub only_a_structs: Vec<String>,
    pub only_b_structs: Vec<String>,
    pub changed_structs: Vec<(String, String)>,
    pub only_a_enums: Vec<String>,
    pub only_b_enums: Vec<String>,
    pub changed_enums: Vec<(String, String)>,
    pub same_structs: usize,
    pub same_enums: usize,
}

impl Diff {
    /// No struct or enum present in both differs.
    pub fn common_identical(&self) -> bool {
        self.changed_structs.is_empty() && self.changed_enums.is_empty()
    }

    pub fn report(&self, a: &str, b: &str, limit: usize) -> String {
        let mut s = String::new();
        let _ = writeln!(
            s,
            "structs: {} identical, {} differ, {} only in {a}, {} only in {b}",
            self.same_structs,
            self.changed_structs.len(),
            self.only_a_structs.len(),
            self.only_b_structs.len()
        );
        let _ = writeln!(
            s,
            "enums:   {} identical, {} differ, {} only in {a}, {} only in {b}",
            self.same_enums,
            self.changed_enums.len(),
            self.only_a_enums.len(),
            self.only_b_enums.len()
        );
        let list = |s: &mut String, title: &str, v: &[String]| {
            if v.is_empty() {
                return;
            }
            let _ = writeln!(s, "{title} ({}):", v.len());
            for n in v.iter().take(limit) {
                let _ = writeln!(s, "  {n}");
            }
            if v.len() > limit {
                let _ = writeln!(s, "  ... {} more", v.len() - limit);
            }
        };
        let changed = |s: &mut String, title: &str, v: &[(String, String)]| {
            if v.is_empty() {
                return;
            }
            let _ = writeln!(s, "{title} ({}):", v.len());
            for (n, why) in v.iter().take(limit) {
                let _ = writeln!(s, "  {n}: {why}");
            }
            if v.len() > limit {
                let _ = writeln!(s, "  ... {} more", v.len() - limit);
            }
        };
        changed(&mut s, "structs that differ", &self.changed_structs);
        changed(&mut s, "enums that differ", &self.changed_enums);
        list(&mut s, &format!("structs only in {a}"), &self.only_a_structs);
        list(&mut s, &format!("structs only in {b}"), &self.only_b_structs);
        list(&mut s, &format!("enums only in {a}"), &self.only_a_enums);
        list(&mut s, &format!("enums only in {b}"), &self.only_b_enums);
        s
    }
}

fn struct_delta(a: &Struct, b: &Struct) -> Option<String> {
    if a.super_name != b.super_name {
        return Some(format!("super {:?} vs {:?}", a.super_name, b.super_name));
    }
    if a.properties.len() != b.properties.len() {
        let names = |s: &Struct| s.properties.iter().map(|p| p.name.clone()).collect::<Vec<_>>();
        return Some(format!(
            "{} vs {} properties: {:?} vs {:?}",
            a.properties.len(),
            b.properties.len(),
            names(a),
            names(b)
        ));
    }
    for (i, (pa, pb)) in a.properties.iter().zip(&b.properties).enumerate() {
        if pa != pb {
            return Some(format!(
                "property #{i}: {} [{}] {} vs {} [{}] {}",
                pa.name,
                pa.array_dim,
                pa.ty.describe(),
                pb.name,
                pb.array_dim,
                pb.ty.describe()
            ));
        }
    }
    None
}

pub fn diff(a: &Schema, b: &Schema) -> Diff {
    let mut d = Diff::default();
    let (sa, sb) = (a.struct_map(), b.struct_map());
    for (n, x) in &sa {
        match sb.get(n) {
            None => d.only_a_structs.push(n.to_string()),
            Some(y) => match struct_delta(x, y) {
                Some(why) => d.changed_structs.push((n.to_string(), why)),
                None => d.same_structs += 1,
            },
        }
    }
    d.only_b_structs = sb.keys().filter(|n| !sa.contains_key(*n)).map(|n| n.to_string()).collect();
    let (ea, eb) = (a.enum_map(), b.enum_map());
    for (n, x) in &ea {
        match eb.get(n) {
            None => d.only_a_enums.push(n.to_string()),
            Some(y) if x.entries != y.entries => {
                let first = x.entries.iter().zip(&y.entries).find(|(p, q)| p != q);
                d.changed_enums.push((
                    n.to_string(),
                    match first {
                        Some((p, q)) => format!("{p:?} vs {q:?}"),
                        None => format!("{} vs {} entries", x.entries.len(), y.entries.len()),
                    },
                ))
            }
            Some(_) => d.same_enums += 1,
        }
    }
    d.only_b_enums = eb.keys().filter(|n| !ea.contains_key(*n)).map(|n| n.to_string()).collect();
    d
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema::{Enum, Kind, PropType, Property};

    #[test]
    fn finds_differences() {
        let p = |n: &str, k| Property { name: n.into(), array_dim: 1, ty: PropType::Simple(k) };
        let a = Schema {
            enums: vec![Enum { name: "E".into(), entries: vec![(0, "E::A".into())] }],
            structs: vec![
                Struct { name: "S".into(), super_name: None, properties: vec![p("X", Kind::Int)] },
                Struct { name: "OnlyA".into(), super_name: None, properties: vec![] },
            ],
        };
        let mut b = a.clone();
        b.structs[0].properties[0] = p("X", Kind::Float);
        b.structs.pop();
        let d = diff(&a, &b);
        assert_eq!(d.only_a_structs, ["OnlyA"]);
        assert_eq!(d.changed_structs.len(), 1);
        assert!(d.changed_structs[0].1.contains("Int"), "{:?}", d.changed_structs);
        assert_eq!(d.same_enums, 1);
        assert!(!d.common_identical());
        assert!(diff(&a, &a).common_identical());
    }
}
