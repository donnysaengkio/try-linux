// A system's QEMU window: found by its process, and brought to the front (restored if minimized).

#[cfg(windows)]
pub fn front(pid: u32) -> bool {
    use windows_sys::Win32::Foundation::{BOOL, HWND, LPARAM};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        EnumWindows, GetWindowTextLengthW, GetWindowThreadProcessId, IsIconic, IsWindowVisible, SetForegroundWindow, ShowWindow, SW_RESTORE,
    };

    struct Search {
        pid: u32,
        found: HWND,
    }
    unsafe extern "system" fn each(h: HWND, l: LPARAM) -> BOOL {
        let s = &mut *(l as *mut Search);
        let mut owner = 0u32;
        GetWindowThreadProcessId(h, &mut owner);
        if owner == s.pid && IsWindowVisible(h) != 0 && GetWindowTextLengthW(h) > 0 {
            s.found = h;
            return 0;
        }
        1
    }
    let mut s = Search { pid, found: std::ptr::null_mut() };
    unsafe {
        EnumWindows(Some(each), &mut s as *mut Search as LPARAM);
        if s.found.is_null() {
            return false;
        }
        if IsIconic(s.found) != 0 {
            ShowWindow(s.found, SW_RESTORE);
        }
        SetForegroundWindow(s.found);
    }
    true
}

#[cfg(not(windows))]
pub fn front(_pid: u32) -> bool {
    false
}
