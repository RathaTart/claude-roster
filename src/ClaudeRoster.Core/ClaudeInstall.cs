namespace ClaudeRoster.Core;

/// <summary>Where the Claude desktop app keeps its data and how to start it again.</summary>
/// <param name="DataRoot">The app's Electron user-data folder.</param>
/// <param name="PackageFamilyName">Set for the Microsoft Store (MSIX) install.</param>
/// <param name="LauncherPath">Set for the classic installer, which starts from a plain exe.</param>
public sealed record ClaudeInstall(string DataRoot, string? PackageFamilyName, string? LauncherPath)
{
    /// <summary>Points the roster at another data folder; used by tests and dry runs.</summary>
    public const string DataRootOverrideVariable = "CLAUDE_ROSTER_CLAUDE_DATA";

    private const string PackageName = "AnthropicPBC.Claude";

    public string? DefaultAppUserModelId => PackageFamilyName is null ? null : PackageFamilyName + "!Claude";

    public static ClaudeInstall? Locate(string? localAppData = null, string? roamingAppData = null)
    {
        var overridden = Environment.GetEnvironmentVariable(DataRootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new ClaudeInstall(Path.GetFullPath(overridden), null, null);
        }

        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        roamingAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Store install: Windows redirects the app's %APPDATA% writes into the package's LocalCache.
        var packages = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateDirectories(packages, PackageName + "_*"))
            {
                var dataRoot = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                if (Directory.Exists(dataRoot))
                {
                    return new ClaudeInstall(dataRoot, Path.GetFileName(package), null);
                }
            }
        }

        var classic = Path.Combine(roamingAppData, "Claude");
        if (Directory.Exists(classic))
        {
            var launcher = Path.Combine(localAppData, "AnthropicClaude", "claude.exe");
            return new ClaudeInstall(classic, null, File.Exists(launcher) ? launcher : null);
        }

        return null;
    }
}
