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
    pub fn srwlock_fns() -> Vec<usize> {
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

#[cfg(not(windows))]
mod imp {
    use super::*;
    pub struct RemoteProcess {
        pub pid: u32,
    }
    impl RemoteProcess {
        pub fn open(_pid: u32) -> Result<Self> {
            bail!("acr-mappings only supports Windows")
        }
        pub fn is_alive(&self) -> bool {
            false
        }
        pub fn modules(&self) -> Result<Vec<ModuleEntry>> {
            bail!("acr-mappings only supports Windows")
        }
        pub fn read(&self, _addr: usize, _buf: &mut [u8]) -> bool {
            false
        }
    }
    pub fn list_processes() -> Result<Vec<ProcessEntry>> {
        bail!("acr-mappings only supports Windows")
    }
    pub fn srwlock_fns() -> Vec<usize> {
        Vec::new()
    }
}

pub use imp::{list_processes, srwlock_fns, RemoteProcess};

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
