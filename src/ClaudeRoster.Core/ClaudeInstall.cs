namespace ClaudeRoster.Core;

/// <summary>Where the Claude desktop app keeps its data and how to start it again.</summary>
/// <param name="DataRoot">The app's Electron user-data folder.</param>
/// <param name="PackageFamilyName">Set for the Microsoft Store (MSIX) install.</param>
/// <param name="LauncherPath">Set for the classic installer, which starts from a plain exe.</param>
public sealed record ClaudeInstall(string DataRoot, string? PackageFamilyName, string? LauncherPath)
{
    /// <summary>Points the roster at another data folder; used by tests and dry runs.</summary>
    public const string DataRootOverrideVariable = "CLAUDE_ROSTER_CLAUDE_DATA";

    /// <summary>Newer Claude versions keep their data under %LOCALAPPDATA%\Claude-Data\&lt;install id&gt;.</summary>
    public const string MovedDataFolderName = "Claude-Data";

    /// <summary>Claude leaves ".user-data-left.&lt;install id&gt;" in the old folder after moving its data out.</summary>
    public const string MovedMarkerPrefix = ".user-data-left.";

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
        var moved = Path.Combine(localAppData, MovedDataFolderName);

        // Store install: the data lives in Claude-Data\<package family name, lower case>, or, before Claude moved
        // it, in the package's LocalCache (Windows redirects the app's %APPDATA% writes there).
        var packages = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateDirectories(packages, PackageName + "_*"))
            {
                var familyName = Path.GetFileName(package);
                var dataRoot = ResolveDataRoot(Path.Combine(package, "LocalCache", "Roaming", "Claude"), moved, familyName);
                if (dataRoot is not null)
                {
                    return new ClaudeInstall(dataRoot, familyName, null);
                }
            }
        }

        var classic = ResolveDataRoot(Path.Combine(roamingAppData, "Claude"), moved, movedName: null);
        if (classic is not null)
        {
            var launcher = Path.Combine(localAppData, "AnthropicClaude", "claude.exe");
            return new ClaudeInstall(classic, null, File.Exists(launcher) ? launcher : null);
        }

        return null;
    }

    /// <summary>
    /// The folder Claude really uses. The old one stays behind after a move, its sign-in frozen in time, so reading
    /// or switching it would act on an account Claude no longer looks at.
    /// </summary>
    private static string? ResolveDataRoot(string legacy, string moved, string? movedName)
    {
        var names = new List<string>();
        if (movedName is not null)
        {
            names.Add(movedName.ToLowerInvariant());
        }

        var legacyExists = Directory.Exists(legacy);
        var marked = false;
        if (legacyExists)
        {
            foreach (var marker in Directory.EnumerateFiles(legacy, MovedMarkerPrefix + "*"))
            {
                marked = true;
                names.Add(Path.GetFileName(marker)[MovedMarkerPrefix.Length..]);
            }
        }

        foreach (var name in names)
        {
            // The name comes from a file name; never let it climb out of Claude-Data.
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            {
                continue;
            }

            var candidate = Path.Combine(moved, name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return legacyExists && !marked ? legacy : null;
    }
}
