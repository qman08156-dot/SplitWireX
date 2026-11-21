#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SplitWireTurkey.SplitWireX
{
    /// <summary>
    /// SplitWireX: everything that keeps SplitWire from popping up terminal windows.
    ///  - Tracks "run once" (one-time) processes that used to live in a visible console,
    ///    so the panel can show them and stop them.
    ///  - Rewrites the auto-scan launcher so bash runs inside our hidden console.
    ///  - A safety-net hook that hides any classic console window spawned by our own
    ///    child processes the instant it is created.
    /// </summary>
    public static class XConsole
    {
        public sealed class OneTimeRun
        {
            public string Name { get; init; } = "";
            public int Pid { get; init; }
            public DateTime StartTime { get; init; }
        }

        private static readonly object _lock = new();
        private static readonly List<OneTimeRun> _runs = new();

        /// <summary>Raised (on any thread) when the set of one-time runs changes.</summary>
        public static event Action? RunsChanged;

        /// <summary>
        /// Registers a hidden "run once" process. Closes its stdin so that a trailing
        /// "pause" in the batch file returns immediately instead of waiting forever.
        /// </summary>
        public static void TrackOneTime(string name, Process? process)
        {
            if (process == null) return;
            try { if (process.StartInfo.RedirectStandardInput) process.StandardInput.Close(); } catch { }

            OneTimeRun run;
            try
            {
                run = new OneTimeRun { Name = name, Pid = process.Id, StartTime = process.StartTime };
            }
            catch { return; }

            lock (_lock)
            {
                _runs.RemoveAll(r => r.Name == name);
                _runs.Add(run);
            }
            RunsChanged?.Invoke();
        }

        /// <summary>Currently alive one-time runs (dead ones are pruned).</summary>
        public static List<OneTimeRun> GetActiveRuns()
        {
            bool changed = false;
            List<OneTimeRun> alive;
            lock (_lock)
            {
                changed = _runs.RemoveAll(r => !IsAlive(r)) > 0;
                alive = _runs.ToList();
            }
            if (changed) RunsChanged?.Invoke();
            return alive;
        }

        public static void StopRun(string name)
        {
            List<OneTimeRun> toStop;
            lock (_lock)
            {
                toStop = _runs.Where(r => r.Name == name).ToList();
                _runs.RemoveAll(r => r.Name == name);
            }
            foreach (var r in toStop)
            {
                try
                {
                    if (!IsAlive(r)) continue;
                    using var p = Process.GetProcessById(r.Pid);
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) { Debug.WriteLine($"SplitWireX StopRun: {ex.Message}"); }
            }
            RunsChanged?.Invoke();
        }

        public static void StopAllRuns()
        {
            List<string> names;
            lock (_lock) names = _runs.Select(r => r.Name).Distinct().ToList();
            foreach (var n in names) StopRun(n);
        }

        private static bool IsAlive(OneTimeRun r)
        {
            try
            {
                using var p = Process.GetProcessById(r.Pid);
                return !p.HasExited && Math.Abs((p.StartTime - r.StartTime).TotalSeconds) < 2;
            }
            catch { return false; }
        }

        /// <summary>
        /// The auto-scan (blockcheck) launcher starts bash through tools\elevator.exe, which
        /// creates a brand-new, visible console window. SplitWire already runs as admin, so the
        /// elevator is not needed: run bash directly inside our hidden console instead.
        /// Output goes to nul because the script already tees everything into blockcheck.log
        /// (and the caller never drains its redirected pipes).
        /// </summary>
        public static void PrepareHiddenBlockcheck(string cmdPath)
        {
            try
            {
                if (!File.Exists(cmdPath)) return;
                var text = File.ReadAllText(cmdPath);
                const string elevated = "\"%~dp0..\\tools\\elevator\" ..\\cygwin\\bin\\bash -i \"%P%\"";
                const string hidden = "..\\cygwin\\bin\\bash -i \"%P%\" >nul 2>&1 <nul";
                if (text.Contains(elevated))
                {
                    File.WriteAllText(cmdPath, text.Replace(elevated, hidden));
                }
            }
            catch (Exception ex) { Debug.WriteLine($"SplitWireX PrepareHiddenBlockcheck: {ex.Message}"); }
        }

        // ------------------------------------------------------------------
        // Safety net: hide console windows created by our descendant processes.
        // ------------------------------------------------------------------

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const int SW_HIDE = 0;

        private static IntPtr _hook = IntPtr.Zero;
        private static WinEventDelegate? _hookProc; // keep alive

        /// <summary>Must be called on a thread with a message loop (the WPF UI thread).</summary>
        public static void StartConsoleGuard()
        {
            if (_hook != IntPtr.Zero) return;
            _hookProc = OnWinEvent;
            _hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        public static void StopConsoleGuard()
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        private static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero || idObject != 0 || idChild != 0) return;
            try
            {
                var sb = new StringBuilder(64);
                GetClassName(hwnd, sb, sb.Capacity);
                if (sb.ToString() != "ConsoleWindowClass") return;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0 && IsOurDescendant(pid))
                {
                    ShowWindow(hwnd, SW_HIDE);
                }
            }
            catch { }
        }

        private static bool IsOurDescendant(uint pid)
        {
            uint self = (uint)Environment.ProcessId;
            var parents = new Dictionary<uint, uint>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return false;
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (Process32FirstW(snap, ref e))
                {
                    do { parents[e.th32ProcessID] = e.th32ParentProcessID; }
                    while (Process32NextW(snap, ref e));
                }
            }
            finally { CloseHandle(snap); }

            uint cur = pid;
            for (int depth = 0; depth < 32; depth++)
            {
                if (!parents.TryGetValue(cur, out uint parent) || parent == 0 || parent == cur) return false;
                if (parent == self) return true;
                cur = parent;
            }
            return false;
        }
    }
}
