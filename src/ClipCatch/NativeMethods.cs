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
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);

        public static List<MonitorInfo> GetMonitors()
        {
            var list = new List<MonitorInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (GetMonitorInfo(h, ref info))
                {
                    var m = info.rcMonitor;
                    list.Add(new MonitorInfo
                    {
                        DeviceName = info.szDevice,
                        Bounds = new Int32Rect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top),
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

        /// <summary>Clicks go through the window to whatever is underneath, and it never takes focus.</summary>
        public static void MakeClickThrough(Window w)
        {
            var h = Handle(w);
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        /// <summary>Hides the window from every screen capture, ours included (Windows 10 2004 and newer).</summary>
        public static void ExcludeFromCapture(Window w)
        {
            var h = Handle(w);
            try
            {
                if (ScreenRecorderLib.Recorder.SetExcludeFromCapture(h, true)) return;
            }
            catch
            {
                // Fall through to the plain Win32 call.
            }
            SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        }
    }
}
