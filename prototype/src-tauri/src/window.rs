// Systems' QEMU windows (Windows): finding one by its process, bringing it to the front (showing it again if
// it was hidden), hiding it, and noticing clicks on its close button. QEMU ignores that button
// (window-close=off), so Try Linux hides the window instead and the system keeps running.

#[cfg(windows)]
mod imp {
    use std::sync::{mpsc, Mutex, OnceLock};

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct Point {
        x: i32,
        y: i32,
    }
    #[repr(C)]
    struct MouseHook {
        pt: Point,
        mouse_data: u32,
        flags: u32,
        time: u32,
        extra: usize,
    }
    type EnumProc = unsafe extern "system" fn(isize, isize) -> i32;
    type HookProc = unsafe extern "system" fn(i32, usize, isize) -> isize;

    #[link(name = "user32")]
    extern "system" {
        fn EnumWindows(f: EnumProc, l: isize) -> i32;
        fn GetWindowThreadProcessId(h: isize, pid: *mut u32) -> u32;
        fn IsWindowVisible(h: isize) -> i32;
        fn IsWindow(h: isize) -> i32;
        fn GetWindowTextLengthW(h: isize) -> i32;
        fn IsIconic(h: isize) -> i32;
        fn ShowWindow(h: isize, cmd: i32) -> i32;
        fn SetForegroundWindow(h: isize) -> i32;
        fn WindowFromPoint(p: Point) -> isize;
        fn GetAncestor(h: isize, flags: u32) -> isize;
        fn PhysicalToLogicalPointForPerMonitorDPI(h: isize, p: *mut Point) -> i32;
        fn SendMessageTimeoutW(h: isize, msg: u32, w: usize, l: isize, flags: u32, timeout: u32, result: *mut usize) -> isize;
        fn SetWindowsHookExW(id: i32, f: HookProc, module: isize, thread: u32) -> isize;
        fn CallNextHookEx(h: isize, code: i32, w: usize, l: isize) -> isize;
        fn GetMessageW(msg: *mut [u8; 48], h: isize, min: u32, max: u32) -> i32;
    }
    #[link(name = "kernel32")]
    extern "system" {
        fn GetModuleHandleW(name: *const u16) -> isize;
    }

    // The system's window: a visible, titled top-level window of its QEMU process.
    pub fn find(pid: u32) -> Option<isize> {
        struct Search {
            pid: u32,
            found: isize,
        }
        unsafe extern "system" fn each(h: isize, l: isize) -> i32 {
            let s = &mut *(l as *mut Search);
            let mut owner = 0u32;
            GetWindowThreadProcessId(h, &mut owner);
            if owner == s.pid && IsWindowVisible(h) != 0 && GetWindowTextLengthW(h) > 0 {
                s.found = h;
                return 0;
            }
            1
        }
        let mut s = Search { pid, found: 0 };
        unsafe { EnumWindows(each, &mut s as *mut Search as isize) };
        (s.found != 0).then_some(s.found)
    }

    // Shown (if hidden), restored (if minimized) and in front.
    pub fn front(h: isize) {
        unsafe {
            if IsWindow(h) == 0 {
                return;
            }
            if IsWindowVisible(h) == 0 {
                ShowWindow(h, 5); // SW_SHOW
            }
            if IsIconic(h) != 0 {
                ShowWindow(h, 9); // SW_RESTORE
            }
            SetForegroundWindow(h);
        }
    }

    pub fn hide(h: isize) {
        unsafe { ShowWindow(h, 0) }; // SW_HIDE
    }

    // True when (x, y), in physical screen pixels, is on the close button of top-level window `h`.
    pub fn on_close_button(h: isize, x: i32, y: i32) -> bool {
        unsafe {
            let mut p = Point { x, y };
            if IsWindowVisible(h) == 0 || GetAncestor(WindowFromPoint(p), 2) != h {
                return false; // GA_ROOT
            }
            PhysicalToLogicalPointForPerMonitorDPI(h, &mut p);
            let mut hit = 0usize;
            let l = ((p.y as u32 & 0xFFFF) << 16 | (p.x as u32 & 0xFFFF)) as isize;
            SendMessageTimeoutW(h, 0x0084, 0, l, 0x0002, 500, &mut hit) != 0 && hit == 20 // WM_NCHITTEST, HTCLOSE
        }
    }

    static CLICKS: OnceLock<Mutex<mpsc::Sender<(i32, i32)>>> = OnceLock::new();

    unsafe extern "system" fn mouse(code: i32, w: usize, l: isize) -> isize {
        // Left button released: hand the point to another thread (a hook must return quickly).
        if code >= 0 && w == 0x0202 {
            let m = &*(l as *const MouseHook);
            if let Some(tx) = CLICKS.get() {
                let _ = tx.lock().unwrap().send((m.pt.x, m.pt.y));
            }
        }
        CallNextHookEx(0, code, w, l)
    }

    // Calls `click` with each left click's screen point, from a mouse hook on a thread of its own. Once.
    pub fn watch_clicks(click: impl Fn(i32, i32) + Send + 'static) {
        let (tx, rx) = mpsc::channel();
        if CLICKS.set(Mutex::new(tx)).is_err() {
            return;
        }
        std::thread::spawn(move || {
            for (x, y) in rx {
                click(x, y);
            }
        });
        std::thread::spawn(|| unsafe {
            SetWindowsHookExW(14, mouse, GetModuleHandleW(std::ptr::null()), 0); // WH_MOUSE_LL
            let mut msg = [0u8; 48];
            while GetMessageW(&mut msg, 0, 0, 0) > 0 {}
        });
    }
}

#[cfg(not(windows))]
mod imp {
    pub fn find(_pid: u32) -> Option<isize> {
        None
    }
    pub fn front(_h: isize) {}
    pub fn hide(_h: isize) {}
    pub fn on_close_button(_h: isize, _x: i32, _y: i32) -> bool {
        false
    }
    pub fn watch_clicks(_click: impl Fn(i32, i32) + Send + 'static) {}
}

pub use imp::*;
