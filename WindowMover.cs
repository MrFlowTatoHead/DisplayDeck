// WindowMover.exe - bring an app's main window to the primary monitor and maximize it.
// Used by the DisplayDeck "Bring Steam" button (through SwitchAgent.exe); also works
// on its own from a shortcut or command line.
//
// Commands:
//   WindowMover steam [options]          Steam's Big Picture window if open, else its main
//                                        "Steam" window. If Steam has no window (tray only)
//                                        or isn't running, it is asked to open one first.
//   WindowMover move <process> [options] the largest visible window of <process>
//   WindowMover list                     visible top-level windows with process and monitor
// Options:
//   --dry-run      report what would happen, change nothing
//   --no-focus     don't force the window to the foreground
//   --target x,y   use the monitor containing this desktop point instead of the primary
// Exit codes: 0 = done and verified, 1 = usage, 2 = no window found, 3 = verification failed.
// The last output line is "RESULT: <summary>".
//
// Borderless-fullscreen windows (e.g. Big Picture) are moved to cover the target monitor
// exactly; normal windows are restored, moved into the target's work area, then maximized.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WindowMoverTool
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public override string ToString()
        {
            return "(" + Left + "," + Top + " " + Width + "x" + Height + ")";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    class WindowInfo
    {
        public IntPtr Handle;
        public uint Pid;
        public string Process;
        public string Title;
        public string Class;
        public RECT Rect;
        public bool Minimized;
        public bool Maximized;
        public int Style;
        public int ExStyle;
        public IntPtr Owner;
        public int Area { get { return Minimized ? 0 : Math.Max(0, Rect.Width) * Math.Max(0, Rect.Height); } }
    }

    static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const uint GW_OWNER = 4;
        public const int SW_MAXIMIZE = 3;
        public const int SW_RESTORE = 9;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint MONITOR_DEFAULTTOPRIMARY = 1;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint MONITORINFOF_PRIMARY = 1;
        public const uint MOUSEEVENTF_MOVE = 0x0001;

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    }

    class Program
    {
        static bool DryRun;
        static bool NoFocus;
        static POINT Target;

        static int Main(string[] args)
        {
            MakeDpiAware();
            try
            {
                if (args.Length == 0) return Usage();
                string cmd = args[0].ToLowerInvariant();
                string processArg = null;
                int i = 1;
                if (cmd == "move")
                {
                    if (args.Length < 2) return Usage();
                    processArg = args[1];
                    i = 2;
                }
                for (; i < args.Length; i++)
                {
                    string a = args[i].ToLowerInvariant();
                    if (a == "--dry-run") DryRun = true;
                    else if (a == "--no-focus") NoFocus = true;
                    else if (a == "--target" && i + 1 < args.Length)
                    {
                        string[] xy = args[++i].Split(',');
                        if (xy.Length != 2
                            || !int.TryParse(xy[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out Target.X)
                            || !int.TryParse(xy[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out Target.Y))
                            return Usage();
                    }
                    else return Usage();
                }

                if (cmd == "list") return List();
                if (cmd == "steam") return BringSteam();
                if (cmd == "move") return BringProcess(processArg);
                return Usage();
            }
            catch (Exception ex)
            {
                Console.WriteLine("RESULT: Error - " + ex.Message);
                return 10;
            }
        }

        static int Usage()
        {
            Console.WriteLine("Usage: WindowMover steam|list [--dry-run] [--no-focus] [--target x,y]");
            Console.WriteLine("       WindowMover move <process> [--dry-run] [--no-focus] [--target x,y]");
            return 1;
        }

        static void MakeDpiAware()
        {
            // Per-monitor v2 so window and monitor rectangles are in real pixels on every display.
            try
            {
                if (Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch (EntryPointNotFoundException) { }
            try { Native.SetProcessDPIAware(); }
            catch { }
        }

        // ---------- window discovery ----------

        static List<WindowInfo> VisibleWindows()
        {
            List<WindowInfo> list = new List<WindowInfo>();
            Dictionary<uint, string> names = new Dictionary<uint, string>();
            Native.EnumWindowsProc cb = delegate(IntPtr h, IntPtr l)
            {
                if (Native.IsWindowVisible(h)) list.Add(Describe(h, names));
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return list;
        }

        static WindowInfo Describe(IntPtr h, Dictionary<uint, string> names)
        {
            WindowInfo w = new WindowInfo();
            w.Handle = h;
            Native.GetWindowThreadProcessId(h, out w.Pid);
            string name;
            if (!names.TryGetValue(w.Pid, out name))
            {
                try { name = Process.GetProcessById((int)w.Pid).ProcessName; }
                catch { name = "?"; }
                names[w.Pid] = name;
            }
            w.Process = name;
            StringBuilder sb = new StringBuilder(512);
            Native.GetWindowText(h, sb, sb.Capacity);
            w.Title = sb.ToString();
            sb.Length = 0;
            Native.GetClassName(h, sb, sb.Capacity);
            w.Class = sb.ToString();
            Native.GetWindowRect(h, out w.Rect);
            w.Minimized = Native.IsIconic(h);
            w.Maximized = Native.IsZoomed(h);
            w.Style = Native.GetWindowLong(h, Native.GWL_STYLE);
            w.ExStyle = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            w.Owner = Native.GetWindow(h, Native.GW_OWNER);
            return w;
        }

        // A real app window: titled, not a tool/owned popup, and not a tiny helper window.
        static bool IsAppWindow(WindowInfo w)
        {
            if (w.Title.Length == 0) return false;
            if ((w.ExStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;
            if (w.Owner != IntPtr.Zero) return false;
            return w.Minimized || (w.Rect.Width >= 200 && w.Rect.Height >= 150);
        }

        static bool IsSteamProcess(string name)
        {
            return name.Equals("steam", StringComparison.OrdinalIgnoreCase)
                || name.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase);
        }

        static WindowInfo PickSteam(List<WindowInfo> windows, bool anyTitle)
        {
            WindowInfo best = null;
            int bestScore = 0;
            foreach (WindowInfo w in windows)
            {
                if (!IsSteamProcess(w.Process) || !IsAppWindow(w)) continue;
                int score;
                if (w.Title.IndexOf("Big Picture", StringComparison.OrdinalIgnoreCase) >= 0) score = 3;
                else if (w.Title.Equals("Steam", StringComparison.OrdinalIgnoreCase)) score = 2;
                else if (anyTitle) score = 1;
                else continue;
                if (score > bestScore || (score == bestScore && w.Area > best.Area))
                {
                    best = w;
                    bestScore = score;
                }
            }
            return best;
        }

        static WindowInfo PickLargest(List<WindowInfo> windows, string process)
        {
            WindowInfo best = null;
            foreach (WindowInfo w in windows)
            {
                if (!w.Process.Equals(process, StringComparison.OrdinalIgnoreCase) || !IsAppWindow(w)) continue;
                if (best == null || w.Area > best.Area) best = w;
            }
            return best;
        }

        // ---------- commands ----------

        static int List()
        {
            IntPtr primary = Native.MonitorFromPoint(new POINT(), Native.MONITOR_DEFAULTTOPRIMARY);
            foreach (WindowInfo w in VisibleWindows())
            {
                if (w.Title.Length == 0) continue;
                IntPtr mon = Native.MonitorFromWindow(w.Handle, Native.MONITOR_DEFAULTTONEAREST);
                MONITORINFOEX mi = MonitorInfo(mon);
                Console.WriteLine("0x" + w.Handle.ToInt64().ToString("X8") + "  " + w.Process + "  [" + w.Class + "]  '"
                    + w.Title + "'  " + w.Rect + (w.Minimized ? "  minimized" : "") + (w.Maximized ? "  maximized" : "")
                    + "  on " + mi.szDevice + (mon == primary ? " (primary)" : "")
                    + (IsAppWindow(w) ? "" : "  [not an app window]"));
            }
            return 0;
        }

        static int BringSteam()
        {
            WindowInfo w = PickSteam(VisibleWindows(), false);
            if (w == null)
            {
                bool running = System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;
                string what = running ? "Steam is running without a visible window" : "Steam isn't running";
                if (DryRun)
                {
                    Console.WriteLine("RESULT: " + what + " - would ask it to open its main window.");
                    return 0;
                }
                Console.WriteLine(what + " - asking it to open its main window...");
                System.Diagnostics.Process.Start("steam://open/main");
                DateTime deadline = DateTime.UtcNow.AddSeconds(running ? 15 : 45);
                while (w == null && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(500);
                    w = PickSteam(VisibleWindows(), false);
                }
                if (w == null) w = PickSteam(VisibleWindows(), true);
                if (w == null)
                {
                    Console.WriteLine("RESULT: Couldn't find a Steam window to move.");
                    return 2;
                }
                // Steam positions a freshly opened window itself; let that settle first.
                Thread.Sleep(1500);
                w = Describe(w.Handle, new Dictionary<uint, string>());
            }
            return Bring(w, "Steam");
        }

        static int BringProcess(string process)
        {
            if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                process = process.Substring(0, process.Length - 4);
            WindowInfo w = PickLargest(VisibleWindows(), process);
            if (w == null)
            {
                Console.WriteLine("RESULT: No visible window found for process '" + process + "'.");
                return 2;
            }
            return Bring(w, process);
        }

        // ---------- moving ----------

        static MONITORINFOEX MonitorInfo(IntPtr mon)
        {
            MONITORINFOEX mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            Native.GetMonitorInfo(mon, ref mi);
            return mi;
        }

        static bool SameRect(RECT a, RECT b)
        {
            return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
        }

        // Borderless window exactly covering its monitor (Big Picture, borderless games).
        static bool IsFullscreenLike(WindowInfo w)
        {
            if (w.Minimized || w.Maximized) return false;
            if ((w.Style & Native.WS_CAPTION) == Native.WS_CAPTION) return false;
            IntPtr mon = Native.MonitorFromWindow(w.Handle, Native.MONITOR_DEFAULTTONEAREST);
            return SameRect(w.Rect, MonitorInfo(mon).rcMonitor);
        }

        static int Bring(WindowInfo w, string appName)
        {
            IntPtr targetMon = Native.MonitorFromPoint(Target, Native.MONITOR_DEFAULTTOPRIMARY);
            MONITORINFOEX mi = MonitorInfo(targetMon);
            IntPtr srcMon = Native.MonitorFromWindow(w.Handle, Native.MONITOR_DEFAULTTONEAREST);
            bool fullscreen = IsFullscreenLike(w);
            string targetDesc = mi.szDevice + " " + mi.rcMonitor;

            Console.WriteLine("Window: '" + w.Title + "' (" + w.Process + ", 0x" + w.Handle.ToInt64().ToString("X")
                + ") " + w.Rect + (w.Minimized ? " minimized" : "") + (w.Maximized ? " maximized" : "")
                + (fullscreen ? " borderless-fullscreen" : "") + " on " + MonitorInfo(srcMon).szDevice);
            Console.WriteLine("Target: " + targetDesc);

            bool alreadyThere = srcMon == targetMon && !w.Minimized
                && (fullscreen ? SameRect(w.Rect, mi.rcMonitor) : w.Maximized);

            if (DryRun)
            {
                Console.WriteLine("RESULT: Would " + (alreadyThere ? "bring " + appName + " to the front (already in place)"
                    : "move " + appName + " to " + mi.szDevice + (fullscreen ? " (fullscreen)" : " and maximize it")) + ".");
                return 0;
            }

            bool ok = alreadyThere;
            for (int attempt = 0; attempt < 3 && !ok; attempt++)
            {
                if (attempt > 0) Thread.Sleep(1000);
                Place(w.Handle, fullscreen, mi);
                Thread.Sleep(400);
                ok = Verify(w.Handle, fullscreen, targetMon, mi);
            }
            if (!NoFocus) Focus(w.Handle);

            if (!ok)
            {
                WindowInfo now = Describe(w.Handle, new Dictionary<uint, string>());
                Console.WriteLine("RESULT: Tried to move " + appName + ", but it ended up at " + now.Rect
                    + (now.Maximized ? " (maximized)" : "") + ".");
                return 3;
            }
            string where = Target.X == 0 && Target.Y == 0 ? "the main screen" : mi.szDevice;
            Console.WriteLine("RESULT: " + (alreadyThere
                ? appName + " was already on " + where + " - brought it to the front."
                : appName + " moved to " + where + (fullscreen ? " (fullscreen)." : " and maximized.")));
            return 0;
        }

        static void Place(IntPtr h, bool fullscreen, MONITORINFOEX mi)
        {
            if (fullscreen)
            {
                Native.SetWindowPos(h, IntPtr.Zero, mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Width, mi.rcMonitor.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
                return;
            }
            if (Native.IsIconic(h) || Native.IsZoomed(h))
            {
                Native.ShowWindow(h, Native.SW_RESTORE);
                Thread.Sleep(150);
            }
            RECT r;
            Native.GetWindowRect(h, out r);
            RECT work = mi.rcWork;
            int width = Math.Min(r.Width >= 400 ? r.Width : 1280, work.Width);
            int height = Math.Min(r.Height >= 300 ? r.Height : 800, work.Height);
            int x = work.Left + (work.Width - width) / 2;
            int y = work.Top + (work.Height - height) / 2;
            Native.SetWindowPos(h, IntPtr.Zero, x, y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Native.ShowWindow(h, Native.SW_MAXIMIZE);
        }

        static bool Verify(IntPtr h, bool fullscreen, IntPtr targetMon, MONITORINFOEX mi)
        {
            if (!Native.IsWindow(h)) return false;
            if (Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST) != targetMon) return false;
            if (fullscreen)
            {
                RECT r;
                Native.GetWindowRect(h, out r);
                return SameRect(r, mi.rcMonitor);
            }
            return Native.IsZoomed(h);
        }

        static void Focus(IntPtr h)
        {
            if (Native.GetForegroundWindow() == h) return;
            // A zero-distance mouse event counts as user input, which lets this process
            // hand the foreground to another window.
            Native.mouse_event(Native.MOUSEEVENTF_MOVE, 0, 0, 0, UIntPtr.Zero);
            if (Native.SetForegroundWindow(h) && Native.GetForegroundWindow() == h) return;

            IntPtr fg = Native.GetForegroundWindow();
            uint pid;
            uint fgThread = Native.GetWindowThreadProcessId(fg, out pid);
            uint me = Native.GetCurrentThreadId();
            if (fgThread != 0 && fgThread != me)
            {
                Native.AttachThreadInput(me, fgThread, true);
                Native.BringWindowToTop(h);
                Native.SetForegroundWindow(h);
                Native.AttachThreadInput(me, fgThread, false);
            }
        }
    }
}
