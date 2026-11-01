//! The game process, opened with `PROCESS_VM_READ | PROCESS_QUERY_INFORMATION` only (the same
//! access as acr-rewind's `acr_probe`). There is no write, allocate, thread or inject capability.

use acr_ue::mem::Memory;
use anyhow::{bail, Result};
use std::collections::HashMap;
use std::sync::Mutex;

const PAGE: usize = 0x1000;

#[derive(Clone, Debug)]
pub struct ModuleEntry {
    pub name: String,
    pub base: usize,
}

#[derive(Clone, Debug)]
pub struct ProcessEntry {
    pub pid: u32,
    pub name: String,
}

#[cfg(windows)]
mod imp {
    use super::*;
    use std::mem::size_of;
    use windows::Win32::Foundation::{CloseHandle, HANDLE, STILL_ACTIVE};
    use windows::Win32::System::Diagnostics::Debug::ReadProcessMemory;
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, Module32FirstW, Module32NextW, Process32FirstW, Process32NextW, MODULEENTRY32W,
        PROCESSENTRY32W, TH32CS_SNAPMODULE, TH32CS_SNAPMODULE32, TH32CS_SNAPPROCESS,
    };
    use windows::Win32::System::Threading::{
        GetExitCodeProcess, OpenProcess, PROCESS_ACCESS_RIGHTS, PROCESS_QUERY_INFORMATION, PROCESS_VM_READ,
    };

    /// The only access rights ever requested.
    pub const ACCESS: PROCESS_ACCESS_RIGHTS = PROCESS_ACCESS_RIGHTS(PROCESS_VM_READ.0 | PROCESS_QUERY_INFORMATION.0);

    struct Handle(HANDLE);
    // SAFETY: a process handle may be used from any thread; ReadProcessMemory is thread-safe.
    unsafe impl Send for Handle {}
    unsafe impl Sync for Handle {}

    impl Drop for Handle {
        fn drop(&mut self) {
            // SAFETY: the handle came from OpenProcess / CreateToolhelp32Snapshot and is closed once.
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }

    fn wide_to_string(w: &[u16]) -> String {
        let n = w.iter().position(|&c| c == 0).unwrap_or(w.len());
        String::from_utf16_lossy(&w[..n])
    }

    pub fn list_processes() -> Result<Vec<ProcessEntry>> {
        // SAFETY: snapshot handle is wrapped (closed on drop); the entry is correctly sized.
        unsafe {
            let snap = Handle(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)?);
            let mut e = PROCESSENTRY32W { dwSize: size_of::<PROCESSENTRY32W>() as u32, ..Default::default() };
            let mut out = Vec::new();
            if Process32FirstW(snap.0, &mut e).is_ok() {
                loop {
                    out.push(ProcessEntry { pid: e.th32ProcessID, name: wide_to_string(&e.szExeFile) });
                    if Process32NextW(snap.0, &mut e).is_err() {
                        break;
                    }
                }
            }
            Ok(out)
        }
    }

    pub struct RemoteProcess {
        handle: Handle,
        pub pid: u32,
    }

    impl RemoteProcess {
        pub fn open(pid: u32) -> Result<Self> {
            // SAFETY: plain OpenProcess call; the handle is owned by `Handle`.
            let h = unsafe { OpenProcess(ACCESS, false, pid) }
                .map_err(|e| anyhow::anyhow!("OpenProcess({pid}, VM_READ|QUERY_INFORMATION): {e}"))?;
            Ok(Self { handle: Handle(h), pid })
        }

        pub fn is_alive(&self) -> bool {
            let mut code = 0u32;
            // SAFETY: valid handle with PROCESS_QUERY_INFORMATION; `code` is a valid out-pointer.
            unsafe { GetExitCodeProcess(self.handle.0, &mut code) }.is_ok() && code == STILL_ACTIVE.0 as u32
        }

        pub fn modules(&self) -> Result<Vec<ModuleEntry>> {
            // ERROR_BAD_LENGTH is transient while the target loads modules; retry briefly.
            let mut last = None;
            for _ in 0..8 {
                // SAFETY: snapshot handle is wrapped; entry struct is correctly sized.
                let snap = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, self.pid) };
                let snap = match snap {
                    Ok(s) => Handle(s),
                    Err(e) => {
                        last = Some(e);
                        std::thread::sleep(std::time::Duration::from_millis(50));
                        continue;
                    }
                };
                let mut e = MODULEENTRY32W { dwSize: size_of::<MODULEENTRY32W>() as u32, ..Default::default() };
                let mut out = Vec::new();
                // SAFETY: see above.
                unsafe {
                    if Module32FirstW(snap.0, &mut e).is_ok() {
                        loop {
                            out.push(ModuleEntry { name: wide_to_string(&e.szModule), base: e.modBaseAddr as usize });
                            if Module32NextW(snap.0, &mut e).is_err() {
                                break;
                            }
                        }
                    }
                }
                return Ok(out);
            }
            bail!("module snapshot of pid {} failed: {:?}", self.pid, last)
        }

        pub fn read(&self, addr: usize, buf: &mut [u8]) -> bool {
            if buf.is_empty() {
                return true;
            }
            if addr.checked_add(buf.len()).is_none() {
                return false;
            }
            let mut n = 0usize;
            // SAFETY: the kernel validates the remote range; `buf` is a valid destination.
            let ok = unsafe {
                ReadProcessMemory(self.handle.0, addr as *const _, buf.as_mut_ptr().cast(), buf.len(), Some(&mut n))
            };
            ok.is_ok() && n == buf.len()
        }
    }

    /// `InitializeSRWLock` / `RtlInitializeSRWLock` addresses (the FNamePool "dumper7" scan looks
    /// for calls to them). System DLLs share their base across processes within a boot session.
    pub fn srwlock_fns(_mem: &dyn Memory, _modules: &[ModuleEntry]) -> Vec<usize> {
        use windows::core::{s, w};
        use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
        let mut out = Vec::new();
        for (module, func) in [
            (w!("kernel32.dll"), s!("InitializeSRWLock")),
            (w!("kernelbase.dll"), s!("InitializeSRWLock")),
            (w!("ntdll.dll"), s!("RtlInitializeSRWLock")),
        ] {
            // SAFETY: lookups of already-loaded system modules in this process.
            unsafe {
                if let Ok(h) = GetModuleHandleW(module) {
                    if let Some(p) = GetProcAddress(h, func) {
                        out.push(p as usize);
                    }
                }
            }
        }
        out
    }
}

/// Linux: the game runs under Wine/Proton, a normal process of the same user. Its Windows virtual addresses are
/// the process's own addresses, so `/proc/<pid>/mem` is the ReadProcessMemory equivalent (opened read-only; needs
/// ptrace access, which `kernel.yama.ptrace_scope` 0 gives same-user processes). Modules come from `/proc/<pid>/maps`:
/// Wine maps each PE image's headers from its file, so the lowest offset-0 mapping of `<name>.exe/.dll` is its base.
#[cfg(target_os = "linux")]
mod imp {
    use super::*;
    use std::fs::File;
    use std::os::unix::fs::FileExt;

    /// The executable a process runs: basename of argv[0] (Wine sets it to the Windows path), else `comm`.
    fn process_name(pid: u32) -> Option<String> {
        if let Ok(cmd) = std::fs::read(format!("/proc/{pid}/cmdline")) {
            let argv0 = String::from_utf8_lossy(cmd.split(|&b| b == 0).next().unwrap_or_default()).into_owned();
            let base = argv0.rsplit(['\\', '/']).next().unwrap_or_default().to_string();
            if !base.is_empty() {
                return Some(base);
            }
        }
        std::fs::read_to_string(format!("/proc/{pid}/comm")).ok().map(|s| s.trim_end().to_string())
    }

    pub fn list_processes() -> Result<Vec<ProcessEntry>> {
        let mut out = Vec::new();
        for e in std::fs::read_dir("/proc")? {
            let Ok(e) = e else { continue };
            let Some(pid) = e.file_name().to_str().and_then(|s| s.parse::<u32>().ok()) else { continue };
            if let Some(name) = process_name(pid) {
                out.push(ProcessEntry { pid, name });
            }
        }
        Ok(out)
    }

    pub struct RemoteProcess {
        mem: File,
        pub pid: u32,
    }

    impl RemoteProcess {
        pub fn open(pid: u32) -> Result<Self> {
            let mem = File::open(format!("/proc/{pid}/mem")).map_err(|e| {
                let scope = std::fs::read_to_string("/proc/sys/kernel/yama/ptrace_scope").unwrap_or_default();
                let hint = if e.kind() == std::io::ErrorKind::PermissionDenied && scope.trim() != "0" {
                    format!(
                        "; kernel.yama.ptrace_scope is {} (only parents may read a process). Either give this tool the \
                         right once: `sudo setcap cap_sys_ptrace=ep {}`, or allow it until the next reboot: \
                         `sudo sysctl kernel.yama.ptrace_scope=0`",
                        scope.trim(),
                        std::env::current_exe().map(|p| p.display().to_string()).unwrap_or_else(|_| "acr-mappings".into())
                    )
                } else {
                    String::new()
                };
                anyhow::anyhow!("opening /proc/{pid}/mem read-only: {e}{hint}")
            })?;
            Ok(Self { mem, pid })
        }

        pub fn is_alive(&self) -> bool {
            // state is the field after "(comm)"; a zombie has exited
            match std::fs::read_to_string(format!("/proc/{}/stat", self.pid)) {
                Ok(s) => s.rsplit_once(')').map(|(_, r)| !r.trim_start().starts_with('Z')).unwrap_or(false),
                Err(_) => false,
            }
        }

        pub fn modules(&self) -> Result<Vec<ModuleEntry>> {
            let maps = std::fs::read_to_string(format!("/proc/{}/maps", self.pid))?;
            let mut base: HashMap<String, usize> = HashMap::new();
            for line in maps.lines() {
                // start-end perms offset dev inode path
                let mut f = line.splitn(6, char::is_whitespace);
                let (Some(range), Some(_perms), Some(offset), Some(_dev), Some(_inode)) =
                    (f.next(), f.next(), f.next(), f.next(), f.next())
                else {
                    continue;
                };
                let path = f.next().unwrap_or("").trim();
                let name = path.rsplit('/').next().unwrap_or("");
                let lower = name.to_ascii_lowercase();
                if !(lower.ends_with(".exe") || lower.ends_with(".dll")) || !offset.trim_start_matches('0').is_empty() {
                    continue;
                }
                let Some(start) = range.split('-').next().and_then(|a| usize::from_str_radix(a, 16).ok()) else {
                    continue;
                };
                let mut mz = [0u8; 2];
                if !self.read(start, &mut mz) || &mz != b"MZ" {
                    continue;
                }
                let b = base.entry(name.to_string()).or_insert(start);
                *b = (*b).min(start);
            }
            if base.is_empty() {
                bail!("no PE modules mapped in pid {} (not a Wine process?)", self.pid)
            }
            let mut out: Vec<_> = base.into_iter().map(|(name, base)| ModuleEntry { name, base }).collect();
            out.sort_by_key(|m| m.base);
            Ok(out)
        }

        pub fn read(&self, addr: usize, buf: &mut [u8]) -> bool {
            if buf.is_empty() {
                return true;
            }
            if addr.checked_add(buf.len()).is_none() || addr > i64::MAX as usize {
                return false;
            }
            self.mem.read_exact_at(buf, addr as u64).is_ok()
        }
    }

    /// `InitializeSRWLock` / `RtlInitializeSRWLock` in the game process. Wine's system DLLs are not mapped at
    /// the same addresses in this (native) process, so they come from the target's own export tables. Wine's
    /// kernel32 / kernelbase entries are usually forwarders to ntdll and are skipped; ntdll's is the call target.
    pub fn srwlock_fns(mem: &dyn Memory, modules: &[ModuleEntry]) -> Vec<usize> {
        let mut out = Vec::new();
        for (module, func) in [
            ("kernel32.dll", "InitializeSRWLock"),
            ("kernelbase.dll", "InitializeSRWLock"),
            ("ntdll.dll", "RtlInitializeSRWLock"),
        ] {
            if let Some(m) = modules.iter().find(|m| m.name.eq_ignore_ascii_case(module)) {
                if let Some(addr) = super::pe_export(mem, m.base, func) {
                    out.push(addr);
                }
            }
        }
        out
    }
}

#[cfg(not(any(windows, target_os = "linux")))]
mod imp {
    use super::*;
    pub struct RemoteProcess {
        pub pid: u32,
    }
    impl RemoteProcess {
        pub fn open(_pid: u32) -> Result<Self> {
            bail!("acr-mappings only supports Windows and Linux (Proton)")
        }
        pub fn is_alive(&self) -> bool {
            false
        }
        pub fn modules(&self) -> Result<Vec<ModuleEntry>> {
            bail!("acr-mappings only supports Windows and Linux (Proton)")
        }
        pub fn read(&self, _addr: usize, _buf: &mut [u8]) -> bool {
            false
        }
    }
    pub fn list_processes() -> Result<Vec<ProcessEntry>> {
        bail!("acr-mappings only supports Windows and Linux (Proton)")
    }
    pub fn srwlock_fns(_mem: &dyn Memory, _modules: &[ModuleEntry]) -> Vec<usize> {
        Vec::new()
    }
}

pub use imp::{list_processes, srwlock_fns, RemoteProcess};

/// Address of the export `name` of the PE64 image at `base`, read through `mem`; `None` when it is missing or a
/// forwarder (its RVA points into the export directory).
#[cfg_attr(windows, allow(dead_code))]
pub fn pe_export(mem: &dyn Memory, base: usize, name: &str) -> Option<usize> {
    let u32_at = |a: usize| mem.read_u32(a);
    let u16_at = |a: usize| {
        let mut b = [0u8; 2];
        mem.read(a, &mut b).then(|| u16::from_le_bytes(b))
    };
    if u16_at(base)? != 0x5A4D {
        return None; // MZ
    }
    let nt = base + u32_at(base + 0x3C)? as usize;
    if u32_at(nt)? != 0x4550 || u16_at(nt + 0x18)? != 0x20B {
        return None; // "PE\0\0", PE32+ optional header
    }
    let (dir_rva, dir_size) = (u32_at(nt + 0x18 + 0x70)? as usize, u32_at(nt + 0x18 + 0x74)? as usize);
    if dir_rva == 0 {
        return None;
    }
    let dir = base + dir_rva;
    let count = u32_at(dir + 0x18)? as usize;
    let (funcs, names, ords) =
        (base + u32_at(dir + 0x1C)? as usize, base + u32_at(dir + 0x20)? as usize, base + u32_at(dir + 0x24)? as usize);
    let mut buf = vec![0u8; name.len() + 1];
    for i in 0..count.min(0x10000) {
        let at = base + u32_at(names + 4 * i)? as usize;
        if !mem.read(at, &mut buf) || &buf[..name.len()] != name.as_bytes() || buf[name.len()] != 0 {
            continue;
        }
        let rva = u32_at(funcs + 4 * u16_at(ords + 2 * i)? as usize)? as usize;
        return (rva != 0 && !(dir_rva..dir_rva + dir_size).contains(&rva)).then_some(base + rva);
    }
    None
}

/// Pids of processes named `name` (case-insensitive).
pub fn find_pids(name: &str) -> Result<Vec<u32>> {
    Ok(list_processes()?.into_iter().filter(|p| p.name.eq_ignore_ascii_case(name)).map(|p| p.pid).collect())
}

/// Adapts the remote process to `acr_ue`'s [`Memory`] with a page cache (reflection walks touch
/// the same pages over and over). Writes always fail.
pub struct PageCache<'a> {
    inner: &'a RemoteProcess,
    pages: Mutex<HashMap<usize, Option<Box<[u8]>>>>,
    max_pages: usize,
}

impl<'a> PageCache<'a> {
    pub fn new(inner: &'a RemoteProcess, max_pages: usize) -> Self {
        Self { inner, pages: Mutex::new(HashMap::new()), max_pages: max_pages.max(16) }
    }

    /// Forgets every cached page (between polls of a still-initialising game).
    pub fn clear(&self) {
        if let Ok(mut p) = self.pages.lock() {
            p.clear();
        }
    }

    fn with_page<T>(&self, page: usize, f: impl FnOnce(&[u8]) -> T) -> Option<T> {
        let mut pages = self.pages.lock().ok()?;
        if !pages.contains_key(&page) {
            if pages.len() >= self.max_pages {
                pages.clear();
            }
            let mut buf = vec![0u8; PAGE].into_boxed_slice();
            let ok = self.inner.read(page, &mut buf);
            pages.insert(page, ok.then_some(buf));
        }
        pages.get(&page)?.as_deref().map(f)
    }

    /// Reads a large range in 1 MiB pieces, zero-filling unreadable pieces; `None` if nothing
    /// was readable.
    pub fn read_big(&self, addr: usize, len: usize) -> Option<Vec<u8>> {
        let mut out = vec![0u8; len];
        let mut any = false;
        for (i, chunk) in out.chunks_mut(1 << 20).enumerate() {
            let base = addr + i * (1 << 20);
            if self.inner.read(base, chunk) {
                any = true;
                continue;
            }
            for (j, page) in chunk.chunks_mut(PAGE).enumerate() {
                any |= self.inner.read(base + j * PAGE, page);
            }
        }
        any.then_some(out)
    }
}

impl Memory for PageCache<'_> {
    fn read(&self, addr: usize, buf: &mut [u8]) -> bool {
        if buf.is_empty() {
            return true;
        }
        if addr < 0x10000 || addr.checked_add(buf.len()).is_none() {
            return false;
        }
        if buf.len() > 16 * PAGE {
            return self.inner.read(addr, buf);
        }
        let mut done = 0usize;
        while done < buf.len() {
            let a = addr + done;
            let page = a & !(PAGE - 1);
            let off = a - page;
            let n = (PAGE - off).min(buf.len() - done);
            if self.with_page(page, |p| buf[done..done + n].copy_from_slice(&p[off..off + n])).is_none() {
                return false;
            }
            done += n;
        }
        true
    }

    fn write(&self, _addr: usize, _data: &[u8]) -> bool {
        false
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;

    #[test]
    fn access_mask_is_read_only() {
        assert_eq!(imp::ACCESS.0, 0x0410, "PROCESS_VM_READ | PROCESS_QUERY_INFORMATION");
    }

    #[test]
    fn is_alive_tracks_exit() {
        let mut child = std::process::Command::new("ping")
            .args(["-n", "30", "127.0.0.1"])
            .stdout(std::process::Stdio::null())
            .spawn()
            .unwrap();
        let p = RemoteProcess::open(child.id()).unwrap();
        assert!(p.is_alive());
        child.kill().unwrap();
        child.wait().unwrap();
        assert!(!p.is_alive());
    }

    #[test]
    fn reads_own_process_and_never_writes() {
        let me = RemoteProcess::open(std::process::id()).unwrap();
        assert!(me.is_alive());
        let value: u64 = 0xA5A5_1234_5678_9ABC;
        let addr = &value as *const u64 as usize;
        let pc = PageCache::new(&me, 64);
        assert_eq!(pc.read_u64(addr), Some(value));
        assert!(!pc.write_u32(addr, 1));
        assert_eq!(value, 0xA5A5_1234_5678_9ABC);
        let exe = std::env::current_exe().unwrap();
        let name = exe.file_name().unwrap().to_string_lossy().into_owned();
        assert!(find_pids(&name).unwrap().contains(&std::process::id()));
        assert!(me.modules().unwrap().iter().any(|m| m.name.eq_ignore_ascii_case("kernel32.dll")));
    }
}

#[cfg(all(test, target_os = "linux"))]
mod linux_tests {
    use super::*;

    #[test]
    fn reads_own_process_and_never_writes() {
        let me = RemoteProcess::open(std::process::id()).unwrap();
        assert!(me.is_alive());
        let value: u64 = 0xA5A5_1234_5678_9ABC;
        let addr = &value as *const u64 as usize;
        let pc = PageCache::new(&me, 64);
        assert_eq!(pc.read_u64(addr), Some(value));
        assert!(!pc.write_u32(addr, 1));
        assert_eq!(value, 0xA5A5_1234_5678_9ABC);
        assert!(!me.read(0x10, &mut [0u8; 8]), "unmapped page reads fail instead of panicking");
    }

    #[test]
    fn lists_own_process_by_argv0_basename() {
        let exe = std::env::args().next().unwrap();
        let name = exe.rsplit('/').next().unwrap().to_string();
        assert!(find_pids(&name).unwrap().contains(&std::process::id()));
    }

    #[test]
    fn exited_process_is_not_alive() {
        let mut child = std::process::Command::new("sleep").arg("30").spawn().unwrap();
        let p = RemoteProcess::open(child.id()).unwrap();
        assert!(p.is_alive());
        child.kill().unwrap();
        child.wait().unwrap();
        assert!(!p.is_alive());
    }

    /// A minimal PE32+ image with an export directory: one function and one forwarder.
    struct Image(Vec<u8>, usize);
    impl Memory for Image {
        fn read(&self, addr: usize, buf: &mut [u8]) -> bool {
            let Some(off) = addr.checked_sub(self.1) else { return false };
            match self.0.get(off..off + buf.len()) {
                Some(s) => {
                    buf.copy_from_slice(s);
                    true
                }
                None => false,
            }
        }
        fn write(&self, _addr: usize, _data: &[u8]) -> bool {
            false
        }
    }

    #[test]
    fn resolves_exports_and_skips_forwarders() {
        let base = 0x7FF0_0000_0000usize;
        let mut img = vec![0u8; 0x2000];
        let put32 = |img: &mut Vec<u8>, at: usize, v: u32| img[at..at + 4].copy_from_slice(&v.to_le_bytes());
        img[0..2].copy_from_slice(b"MZ");
        put32(&mut img, 0x3C, 0x80);
        img[0x80..0x84].copy_from_slice(b"PE\0\0");
        img[0x98..0x9A].copy_from_slice(&0x20Bu16.to_le_bytes());
        let (dir, size) = (0x1000usize, 0x200u32);
        put32(&mut img, 0x98 + 0x70, dir as u32);
        put32(&mut img, 0x98 + 0x74, size);
        put32(&mut img, dir + 0x18, 2); // NumberOfNames
        put32(&mut img, dir + 0x1C, 0x1100); // AddressOfFunctions
        put32(&mut img, dir + 0x20, 0x1110); // AddressOfNames
        put32(&mut img, dir + 0x24, 0x1120); // AddressOfNameOrdinals
        put32(&mut img, 0x1100, 0x1800); // function 0: code
        put32(&mut img, 0x1104, 0x1180); // function 1: forwarder string inside the directory
        put32(&mut img, 0x1110, 0x1140);
        put32(&mut img, 0x1114, 0x1160);
        img[0x1122..0x1124].copy_from_slice(&1u16.to_le_bytes());
        img[0x1140..0x1140 + 21].copy_from_slice(b"RtlInitializeSRWLock\0");
        img[0x1160..0x1160 + 18].copy_from_slice(b"InitializeSRWLock\0");
        let mem = Image(img, base);
        assert_eq!(pe_export(&mem, base, "RtlInitializeSRWLock"), Some(base + 0x1800));
        assert_eq!(pe_export(&mem, base, "InitializeSRWLock"), None, "forwarder");
        assert_eq!(pe_export(&mem, base, "RtlInitializeSRWLoc"), None, "prefix only");
        assert_eq!(pe_export(&mem, base + 0x10, "RtlInitializeSRWLock"), None, "not a PE");
    }
}
