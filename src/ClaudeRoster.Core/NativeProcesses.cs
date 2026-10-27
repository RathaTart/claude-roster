using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClaudeRoster.Core;

internal sealed record NativeProcess(int Id, int ParentId, string Name);

/// <summary>Win32 process and window queries .NET doesn't expose (parent ids, image paths of packaged apps, AUMIDs).</summary>
internal static class NativeProcesses
{
    private const uint SnapProcess = 0x2;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint WmClose = 0x0010;
    private const int ErrorInsufficientBuffer = 122;
    private static readonly nint InvalidHandle = -1;

    public static List<NativeProcess> Snapshot()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            throw new Win32Exception();
        }

        try
        {
            var result = new List<NativeProcess>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            {
                result.Add(new NativeProcess((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            }

            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public static string? ImagePath(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static string? ReadAppUserModelId(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            uint length = 0;
            if (GetApplicationUserModelId(process, ref length, null) != ErrorInsufficientBuffer || length == 0)
            {
                return null;
            }

            var buffer = new char[length];
            return GetApplicationUserModelId(process, ref length, buffer) == 0
                ? new string(buffer, 0, (int)length - 1)
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Asks each visible top-level window of the given processes to close, as clicking its X would.</summary>
    public static void PostCloseToWindows(IReadOnlySet<int> processIds)
    {
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (processIds.Contains((int)owner) && IsWindowVisible(window))
            {
                PostMessageW(window, WmClose, 0, 0);
            }

            return true;
        }, 0);
    }

    /// <summary>True once any of the given processes shows a titled top-level window.</summary>
    public static bool HasVisibleWindow(IReadOnlySet<int> processIds)
    {
        var found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (processIds.Contains((int)owner) && IsWindowVisible(window) && GetWindowTextLengthW(window) > 0)
            {
                found = true;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, char[] buffer, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(nint process, ref uint length, char[]? buffer);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(nint window);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}

public static class LaunchContext
{
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>
    /// True when this process was started, directly or not, by the Claude desktop app, e.g. from a terminal in a
    /// Claude Code session. Switching closes Claude, which would take this process down with it mid-switch.
    /// </summary>
    public static bool IsStartedFromClaude() => IsInsideClaudePackage() || HasClaudeAncestor();

    /// <summary>Claude runs its Code sessions outside its package, so this alone misses most cases.</summary>
    private static bool IsInsideClaudePackage()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    private static bool HasClaudeAncestor()
    {
        var processes = NativeProcesses.Snapshot().ToDictionary(process => process.Id);
        var visited = new HashSet<int>();
        var current = Environment.ProcessId;
        while (processes.TryGetValue(current, out var process) && visited.Add(current))
        {
            if (process.Id != Environment.ProcessId && ClaudeProcess.IsDesktopExecutable(NativeProcesses.ImagePath(process.Id)))
            {
                return true;
            }

            current = process.ParentId;
        }

        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, char[]? buffer);
}
