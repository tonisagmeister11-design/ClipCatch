using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipCatch
{
    /// <summary>A display, in physical pixels on the virtual desktop.</summary>
    public sealed class MonitorInfo
    {
        public string DeviceName { get; set; }
        public Int32Rect Bounds { get; set; }
        /// <summary>The monitor without the taskbar.</summary>
        public Int32Rect WorkArea { get; set; }
        public bool IsPrimary { get; set; }
    }

    internal static class NativeMethods
    {
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        private const uint SWP_NOACTIVATE = 0x10;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        /// <summary>The display scale (1.0 = 100 %) of the monitor at a point in physical pixels.</summary>
        public static double ScaleAt(int x, int y)
        {
            try
            {
                var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
                if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
            }
            catch
            {
                // Windows before 8.1: fall through.
            }
            return 1.0;
        }

        public static List<MonitorInfo> GetMonitors()
        {
            var list = new List<MonitorInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (GetMonitorInfo(h, ref info))
                {
                    var m = info.rcMonitor;
                    var w = info.rcWork;
                    list.Add(new MonitorInfo
                    {
                        DeviceName = info.szDevice,
                        Bounds = new Int32Rect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top),
                        WorkArea = new Int32Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top),
                        IsPrimary = (info.dwFlags & 1) != 0,
                    });
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

        /// <summary>Places a window at exact physical pixels, topmost, without activating it.</summary>
        public static void PlaceTopmost(Window w, Int32Rect px) =>
            SetWindowPos(Handle(w), HWND_TOPMOST, px.X, px.Y, px.Width, px.Height, SWP_NOACTIVATE);

        public static Int32Rect GetWindowRectPx(Window w)
        {
            GetWindowRect(Handle(w), out var r);
            return new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        /// <summary>
        /// Clicks go through the window to whatever is underneath, and it never takes focus.
        /// For an opaque window (no AllowsTransparency) the layered style needs an explicit alpha.
        /// </summary>
        public static void MakeClickThrough(Window w)
        {
            var h = Handle(w);
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            if (!w.AllowsTransparency) SetLayeredWindowAttributes(h, 0, 255, 0x2 /* LWA_ALPHA */);
        }

        /// <summary>No taskbar button, no Alt+Tab entry, never activated by a click.</summary>
        public static void MakeToolWindow(Window w)
        {
            var h = Handle(w);
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        }

        /// <summary>
        /// Hides the window from every screen capture, ours included (Windows 10 2004 and newer).
        /// Returns whether Windows really applied it: older systems refuse it, and then the window
        /// must stay outside the recorded area.
        /// </summary>
        public static bool ExcludeFromCapture(Window w)
        {
            var h = Handle(w);
            bool applied;
            try
            {
                // ScreenRecorderLib only calls SetWindowDisplayAffinity on Windows 10 2004 and newer:
                // on older builds the flag is misread and the window turns into a black box in the video.
                applied = ScreenRecorderLib.Recorder.SetExcludeFromCapture(h, true);
            }
            catch
            {
                applied = false;
            }
            return applied && GetWindowDisplayAffinity(h, out uint affinity) && affinity == WDA_EXCLUDEFROMCAPTURE;
        }

        /// <summary>Rounded window corners on Windows 11 (ignored elsewhere).</summary>
        public static void RoundCorners(Window w)
        {
            int round = 2; // DWMWCP_ROUND
            try { DwmSetWindowAttribute(Handle(w), 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int)); } catch { /* Windows 10 */ }
        }
    }
}
