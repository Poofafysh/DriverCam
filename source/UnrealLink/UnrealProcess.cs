using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UnrealLink
{
    /// <summary>
    /// Optional: starts Unreal when [Launch] AutoLaunch is on, Command is set and no Unreal answers (at most once a
    /// minute), and ends it on quit only if this plugin started it. The launcher (e.g. powershell running
    /// tools/unreallink-start.ps1, which starts Unreal with Start-Process and exits) is remembered by pid (every launch);
    /// the Unreal that then writes its pid into the shared memory (U_Pid) is claimed only when its parent process (or
    /// the parent's parent, up to 3 levels, for a cmd wrapper) is one of those launchers and it started while that
    /// launcher was running.
    /// Any other Unreal or editor (started by hand, PIE, an editor opened after an AutoLaunch attempt) is never claimed
    /// and never ended. Off by default: tools/unreallink-start.ps1 by hand.
    /// </summary>
    internal static class UnrealProcess
    {
        private static readonly List<Process> _launchers = new List<Process>();   // every launcher this plugin started
        private static Process _ue;                                               // the Unreal one of them brought up

        internal static void MaybeLaunch(float now, ref float next)
        {
            if (!Plugin.AutoLaunch.Value || string.IsNullOrWhiteSpace(Plugin.LaunchCommand.Value) || now < next) return;
            next = now + 60f;
            foreach (var l in _launchers) { if (!Exited(l)) return; }   // still starting (Unreal takes 20-40 s)
            try
            {
                var psi = new ProcessStartInfo(Plugin.LaunchCommand.Value, Plugin.LaunchArguments.Value ?? "") { UseShellExecute = false, CreateNoWindow = true };
                var p = Process.Start(psi);
                if (p != null) _launchers.Add(p);   // its handle stays open: start / exit times readable after it exits
                Plugin.Log.LogInfo($"[UnrealLink] started '{Plugin.LaunchCommand.Value}' (pid {(p != null ? p.Id : 0)}) to bring Unreal up");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[UnrealLink] could not start '{Plugin.LaunchCommand.Value}': {e.Message}"); }
        }

        /// <summary>
        /// Unreal wrote a new pid into the shared memory (called only when it changes). Claimed (ended by StopIfOurs)
        /// only when it is a child of a launcher this plugin started.
        /// </summary>
        internal static void NotePid(uint pid)
        {
            if (pid == 0 || _launchers.Count == 0) return;
            if (_ue != null)
            {
                if (!Exited(_ue)) return;   // ours is still running; another Unreal is not ours
                _ue.Dispose(); _ue = null;
            }
            Process p = null;
            try
            {
                int launcher = LauncherAncestor((int)pid);
                if (launcher == 0) return;
                p = Process.GetProcessById((int)pid);
                _ue = p; p = null;
                Plugin.Log.LogInfo($"[UnrealLink] Unreal pid {pid} was started by AutoLaunch (launcher pid {launcher}): it is closed when the game quits");
            }
            catch { /* gone, or no access: not ours */ }
            finally { p?.Dispose(); }
        }

        internal static void StopIfOurs()
        {
            try { if (_ue != null && !_ue.HasExited) _ue.Kill(true); } catch { /* gone */ }
            foreach (var l in _launchers)
            {
                try { if (!l.HasExited) l.Kill(true); } catch { /* gone */ }
                l.Dispose();
            }
            _launchers.Clear();
            _ue?.Dispose();
            _ue = null;
        }

        private static bool Exited(Process p)
        {
            try { return p.HasExited; } catch { return true; }   // no access: treat as gone
        }

        /// <summary>
        /// The pid of the launcher this plugin started that is pid's parent (up to 3 levels up), else 0. A launcher pid
        /// counts only if the candidate started while that launcher was running (a reused pid never matches).
        /// </summary>
        private static int LauncherAncestor(int pid)
        {
            var parents = ParentMap();
            if (parents == null) return 0;
            DateTime childStart;
            try { using (var c = Process.GetProcessById(pid)) childStart = c.StartTime; } catch { return 0; }
            int cur = pid;
            for (int depth = 0; depth < 3; depth++)
            {
                if (!parents.TryGetValue(cur, out int parent) || parent == 0 || parent == cur) return 0;
                foreach (var l in _launchers)
                {
                    // the child started while that launcher ran (between its start and exit): a reused pid never matches
                    try { if (l.Id == parent && l.StartTime <= childStart && (!l.HasExited || childStart <= l.ExitTime)) return parent; }
                    catch { /* no start time: not a match */ }
                }
                cur = parent;
            }
            return 0;
        }

        // Toolhelp32 snapshot: pid -> parent pid (th32ParentProcessID). Called only when Unreal's pid changes.
        private const uint TH32CS_SNAPPROCESS = 0x2;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

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

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);

        private static Dictionary<int, int> ParentMap()
        {
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == InvalidHandle) return null;
            try
            {
                var map = new Dictionary<int, int>();
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                return map;
            }
            finally { CloseHandle(snap); }
        }
    }
}
