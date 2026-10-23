//! GObjects / FNamePool resolution in the external game process, through `acr_ue`'s resolvers
//! (the same code acr-rewind's hook and `acr-probe sig-test` run).

use crate::process::{srwlock_fns, ModuleEntry, PageCache};
use acr_ue::mem::Memory;
use acr_ue::pattern::Region;
use acr_ue::pe::ModuleInfo;
use acr_ue::reflection::{Ue, UeGlobals};
use acr_ue::resolve::{resolve_fnamepool, resolve_gobjects, Scan};
use acr_ue::sigs::Signatures;
use anyhow::{anyhow, bail, Context, Result};

/// Local copies of the game module's code sections and `.data`.
pub struct LoadedModule {
    pub info: ModuleInfo,
    pub code: Vec<(usize, Vec<u8>)>,
    pub data: Option<(usize, Vec<u8>)>,
}

impl LoadedModule {
    pub fn load(mem: &PageCache<'_>, modules: &[ModuleEntry], sigs: &Signatures) -> Result<Self> {
        let entry = modules
            .iter()
            .find(|m| m.name.eq_ignore_ascii_case(&sigs.module.name))
            .ok_or_else(|| anyhow!("module {} not loaded in the target (yet)", sigs.module.name))?;
        let info = ModuleInfo::parse(mem, entry.base).context("parsing the game's PE headers")?;
        let read = |name: &str| info.section_range(name).and_then(|(a, l)| mem.read_big(a, l).map(|v| (a, v)));
        let code: Vec<_> = sigs.module.scan_sections.iter().filter_map(|s| read(s)).collect();
        if code.is_empty() {
            bail!("none of the scan_sections {:?} are readable", sigs.module.scan_sections);
        }
        Ok(Self { data: read(".data"), code, info })
    }
}

/// Where GObjects / FNamePool were found and how.
#[derive(Clone, Debug)]
pub struct Resolved {
    pub globals: UeGlobals,
    pub gobjects_how: String,
    pub fnamepool_how: String,
    pub module_base: usize,
}

/// Resolves the engine globals and returns a reflection view over `mem`.
pub fn open_ue<'m>(
    mem: &'m PageCache<'m>,
    modules: &[ModuleEntry],
    sigs: &Signatures,
) -> Result<(Ue<&'m PageCache<'m>>, Resolved)> {
    let module = LoadedModule::load(mem, modules, sigs)?;
    let dmem: &dyn Memory = mem;
    let scan = Scan {
        mem: dmem,
        module: &module.info,
        code: module.code.iter().map(|(b, v)| Region { base: *b, bytes: v }).collect(),
        data: module.data.as_ref().map(|(b, v)| Region { base: *b, bytes: v }),
        srwlock_fns: srwlock_fns(),
        sigs,
    };
    let (gobj, layout) = resolve_gobjects(&scan).map_err(|e| anyhow!("GObjects: {e}"))?;
    let names = resolve_fnamepool(&scan).map_err(|e| anyhow!("FNamePool: {e}"))?;
    let globals = UeGlobals { gobjects: gobj.addr, fnamepool: names.addr, gworld: None, process_event: None };
    let ue = Ue::new(mem, globals, layout, sigs.fnamepool.layout, sigs.raw_offsets.uobject);
    let r = Resolved { globals, gobjects_how: gobj.how, fnamepool_how: names.how, module_base: module.info.base };
    Ok((ue, r))
}
