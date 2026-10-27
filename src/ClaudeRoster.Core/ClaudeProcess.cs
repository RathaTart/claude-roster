using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeRoster.Core;

/// <param name="ExecutablePath">The main Claude.exe, used to relaunch the classic install.</param>
/// <param name="AppUserModelId">The Store app id, used to relaunch the MSIX install.</param>
/// <param name="CodeSessionCount">Claude Code sessions the app is running; they stop when it closes.</param>
public sealed record ClaudeRunState(bool IsRunning, string? ExecutablePath, string? AppUserModelId, int CodeSessionCount)
{
    public static readonly ClaudeRunState NotRunning = new(false, null, null, 0);
}

public interface IClaudeProcess
{
    ClaudeRunState Capture();
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task OpenAsync(ClaudeRunState previous, CancellationToken cancellationToken = default);
    bool IsDataRootInUse();
}

/// <summary>Finds, closes and reopens the Claude desktop app. Only ever touches the app's own Claude.exe processes.</summary>
public sealed class ClaudeProcess(ClaudeInstall install) : IClaudeProcess
{
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ForcedCloseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RelaunchDelay = TimeSpan.FromSeconds(2);
    private const int LaunchAttempts = 2;

    /// <summary>
    /// True only for the desktop app itself. The Claude Code CLI is also named claude.exe (inside the app's data
    /// folder, VS Code, npm...), and must never be mistaken for it.
    /// </summary>
    public static bool IsDesktopExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\');
        if (!Path.GetFileName(normalized).Equals("Claude.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Store install: ...\WindowsApps\AnthropicPBC.Claude_<version>_<arch>__<publisher>\app\Claude.exe
        if (normalized.Contains(@"\WindowsApps\AnthropicPBC.Claude_", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.EndsWith(@"\app\Claude.exe", StringComparison.OrdinalIgnoreCase);
        }

        // Classic install: %LOCALAPPDATA%\AnthropicClaude\claude.exe and ...\AnthropicClaude\app-<version>\claude.exe
        var directory = Path.GetDirectoryName(normalized) ?? "";
        var parent = Path.GetFileName(directory);
        if (parent.Equals("AnthropicClaude", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return parent.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(Path.GetDirectoryName(directory) ?? "").Equals("AnthropicClaude", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Only ids for the Claude Store app are launched, so nothing else can ride along to explorer.exe.</summary>
    public static bool IsClaudeAppUserModelId(string? id) =>
        id is not null
        && id.StartsWith("AnthropicPBC.Claude_", StringComparison.OrdinalIgnoreCase)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '!' or '-');

    /// <summary>A cheaper check than <see cref="Capture"/> for polling.</summary>
    public bool IsRunning() => FindDesktop(NativeProcesses.Snapshot()).Count > 0;

    /// <summary>The desktop app's own processes, for finding its windows.</summary>
    public IReadOnlyList<int> DesktopProcessIds() => FindDesktop(NativeProcesses.Snapshot()).Select(process => process.Id).ToList();

    public ClaudeRunState Capture()
    {
        var all = NativeProcesses.Snapshot();
        var desktop = FindDesktop(all);
        if (desktop.Count == 0)
        {
            return ClaudeRunState.NotRunning;
        }

        var ids = desktop.Select(process => process.Id).ToHashSet();
        var main = desktop.FirstOrDefault(process => !ids.Contains(process.ParentId)) ?? desktop[0];
        var sessions = all.Count(process =>
            process.ParentId == main.Id
            && !ids.Contains(process.Id)
            && process.Name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase));
        var appId = NativeProcesses.ReadAppUserModelId(main.Id) ?? install.DefaultAppUserModelId;
        return new ClaudeRunState(true, main.ImagePath, appId, sessions);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (LaunchContext.IsStartedFromClaude())
        {
            throw new ClaudeRosterException(
                "Claude Roster was started from inside Claude, and closing Claude would stop it mid-switch. " +
                "Start it from File Explorer, the Start menu or a normal terminal.");
        }

        var desktop = FindDesktop(NativeProcesses.Snapshot());
        if (desktop.Count == 0 && !IsDataRootInUse())
        {
            return;
        }

        NativeProcesses.PostCloseToWindows(desktop.Select(process => process.Id).ToHashSet());
        if (await WaitUntilClosedAsync(GracefulCloseTimeout, cancellationToken))
        {
            return;
        }

        // Closing the window can leave Claude running in the tray, so end the app's remaining processes.
        foreach (var process in FindDesktop(NativeProcesses.Snapshot()))
        {
            try
            {
                using var handle = Process.GetProcessById(process.Id);
                handle.Kill(entireProcessTree: false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
        }

        if (!await WaitUntilClosedAsync(ForcedCloseTimeout, cancellationToken))
        {
            throw new ClaudeRosterException("Claude didn't close. Quit it from its system tray icon, then try again.");
        }
    }

    /// <summary>
    /// Starts Claude and waits for its window. Claude can quit straight after starting (the instance it replaces
    /// still letting go of its data folder), so a start that dies before showing a window is tried once more.
    /// </summary>
    public async Task OpenAsync(ClaudeRunState previous, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            Launch(previous);
            var started = await WaitForStartAsync(cancellationToken);
            if (started)
            {
                return;
            }

            if (attempt >= LaunchAttempts)
            {
                throw new ClaudeRosterException("Claude didn't start. Open it from the Start menu.");
            }

            await Task.Delay(RelaunchDelay, cancellationToken);
        }
    }

    /// <summary>True when Claude shows a window, or keeps running without one (it may have started in the tray).</summary>
    private static async Task<bool> WaitForStartAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan? firstSeen = null;
        while (true)
        {
            var running = FindDesktop(NativeProcesses.Snapshot()).Select(process => process.Id).ToHashSet();
            if (running.Count > 0)
            {
                firstSeen ??= clock.Elapsed;
                if (NativeProcesses.HasVisibleWindow(running) || clock.Elapsed - firstSeen >= WindowTimeout)
                {
                    return true;
                }
            }
            else if (firstSeen is not null || clock.Elapsed >= StartupTimeout)
            {
                // It started and quit before showing a window, or never started at all.
                return false;
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    private void Launch(ClaudeRunState previous)
    {
        var appId = previous.AppUserModelId ?? install.DefaultAppUserModelId;
        var executable = previous.ExecutablePath ?? install.LauncherPath;
        if (IsClaudeAppUserModelId(appId))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $@"shell:AppsFolder\{appId}") { UseShellExecute = false })?.Dispose();
        }
        else if (IsDesktopExecutable(executable) && File.Exists(executable))
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            })?.Dispose();
        }
        else
        {
            throw new ClaudeRosterException("Couldn't work out how to start Claude. Open it from the Start menu.");
        }
    }

    /// <summary>Chromium keeps "lockfile" open for as long as anything is using the data folder.</summary>
    public bool IsDataRootInUse()
    {
        var lockPath = Path.Combine(install.DataRoot, "lockfile");
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private async Task<bool> WaitUntilClosedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (FindDesktop(NativeProcesses.Snapshot()).Count == 0 && !IsDataRootInUse())
            {
                return true;
            }

            if (clock.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    private static List<DesktopProcess> FindDesktop(IEnumerable<NativeProcess> processes) =>
        processes
            .Where(process => process.Name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase))
            .Select(process => new DesktopProcess(process.Id, process.ParentId, NativeProcesses.ImagePath(process.Id)))
            .Where(process => IsDesktopExecutable(process.ImagePath))
            .ToList();

    private sealed record DesktopProcess(int Id, int ParentId, string? ImagePath);
}
