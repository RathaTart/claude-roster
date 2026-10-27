using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRoster.Core;

/// <summary>The roster-usage Claude Code add-on, as far as uninstalling Claude Roster goes: is it there, and remove it.</summary>
public static class RosterAddOn
{
    public const string PluginName = "roster-usage";
    public const string MarketplaceName = "claude-roster";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Claude Code's folder: ~\.claude, or CLAUDE_CONFIG_DIR when set.</summary>
    public static string DefaultClaudeFolder()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
    }

    /// <summary>Whether Claude Code lists the add-on as installed, from any marketplace.</summary>
    public static bool IsInstalled(string claudeFolder) =>
        Read(Path.Combine(claudeFolder, "plugins", "installed_plugins.json"))?["plugins"] is JsonObject plugins
        && plugins.Any(plugin => plugin.Key.StartsWith(PluginName + "@", StringComparison.Ordinal));

    /// <summary>Whether this project's marketplace is still added to Claude Code.</summary>
    public static bool HasMarketplace(string claudeFolder) =>
        Read(Path.Combine(claudeFolder, "plugins", "known_marketplaces.json")) is JsonObject marketplaces
        && marketplaces.ContainsKey(MarketplaceName);

    /// <summary>
    /// Uninstalls the add-on and this project's marketplace through Claude Code's own command line, then deletes the
    /// figures it saved. Claude Code keeps its own records, so it does the removing.
    /// </summary>
    /// <returns>Null when it's gone, else what went wrong.</returns>
    public static async Task<string?> RemoveAsync(string claudeFolder, CancellationToken cancellationToken = default)
    {
        if (IsInstalled(claudeFolder) && !await RunClaudeAsync(["plugin", "uninstall", PluginName], cancellationToken))
        {
            return $"Couldn't uninstall the {PluginName} add-on from Claude Code. Run: claude plugin uninstall {PluginName}";
        }

        if (HasMarketplace(claudeFolder))
        {
            // Only a leftover listing if this fails; the add-on itself is already gone.
            await RunClaudeAsync(["plugin", "marketplace", "remove", MarketplaceName], cancellationToken);
        }

        var store = Path.Combine(claudeFolder, "plugins", "store");
        if (Directory.Exists(store))
        {
            foreach (var file in Directory.EnumerateFiles(store, PlanUsage.AddOnStorePattern))
            {
                File.Delete(file);
            }
        }

        return null;
    }

    /// <summary>
    /// Runs <c>claude</c> with fixed arguments, through cmd because npm installs it as claude.cmd. Nothing in the
    /// arguments comes from outside.
    /// </summary>
    private static async Task<bool> RunClaudeAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in new[] { "/d", "/c", "claude" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private static JsonNode? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
