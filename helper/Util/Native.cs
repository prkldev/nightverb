using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Nightverb.Util;

internal static class Native
{
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);

    [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    // Same as MessageBox, but closes itself after the timeout (user32 export since Windows XP).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxTimeoutW(IntPtr hwnd, string text, string caption, uint type, ushort languageId, uint milliseconds);

    public const uint MB_OK = 0, MB_ICONINFO = 0x40, MB_ICONERROR = 0x10, MB_YESNO = 0x4, MB_ICONQUESTION = 0x20;
    public const uint MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
    public const int IDYES = 6;

    // ---------- process tree ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);

    public readonly record struct ProcInfo(int Pid, int ParentPid, string Exe);

    public static Dictionary<int, ProcInfo> SnapshotProcesses()
    {
        var result = new Dictionary<int, ProcInfo>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return result;
            do
            {
                result[(int)e.th32ProcessID] = new ProcInfo((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile);
            } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return result;
    }

    /// <summary>
    /// Walk up from the given process to the top-most firefox.exe ancestor
    /// (the Firefox main process, which is the one that outputs audio).
    /// </summary>
    public static int FindFirefoxMainAncestor(int pid)
    {
        var procs = SnapshotProcesses();
        int best = 0;
        int cur = pid;
        var seen = new HashSet<int>();
        while (procs.TryGetValue(cur, out var p) && seen.Add(cur))
        {
            if (p.Exe.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)) best = cur;
            else if (best != 0) break;
            if (!procs.TryGetValue(p.ParentPid, out var parent)) break;
            // Guard against PID reuse: a parent can't be newer than the child,
            // but toolhelp doesn't give times, so just stop at non-firefox once found.
            cur = parent.Pid;
        }
        return best;
    }

    /// <summary>Any running Firefox main process (one whose parent isn't firefox.exe).</summary>
    public static int FindAnyFirefoxMain()
    {
        var procs = SnapshotProcesses();
        foreach (var p in procs.Values)
        {
            if (!p.Exe.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (procs.TryGetValue(p.ParentPid, out var parent) &&
                parent.Exe.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)) continue;
            return p.Pid;
        }
        return 0;
    }

    // ---------- detached process launch ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmdLine, IntPtr procAttr, IntPtr threadAttr,
        bool inheritHandles, uint flags, IntPtr env, string? cwd, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint DETACHED_PROCESS = 0x00000008;

    /// <summary>
    /// Start a process that does not inherit our handles and, if allowed,
    /// leaves the job object Firefox runs native helpers in (so it isn't
    /// killed when the popup closes). Returns the new PID.
    /// </summary>
    public static int StartDetached(string exe, string args)
    {
        string cmd = $"\"{exe}\" {args}";
        string cwd = System.IO.Path.GetDirectoryName(exe)!;
        uint baseFlags = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP | DETACHED_PROCESS;
        foreach (uint flags in new[] { baseFlags | CREATE_BREAKAWAY_FROM_JOB, baseFlags })
        {
            var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
            if (CreateProcessW(exe, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, cwd, ref si, out var pi))
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            int err = Marshal.GetLastWin32Error();
            if (err != 5 /* ACCESS_DENIED: breakaway not allowed, retry without */) throw new Win32Exception(err);
        }
        throw new Win32Exception(5);
    }
}
