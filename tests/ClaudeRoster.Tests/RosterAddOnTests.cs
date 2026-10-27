using ClaudeRoster.Core;

namespace ClaudeRoster.Tests;

public sealed class RosterAddOnTests
{
    [Fact]
    public void The_add_on_and_its_marketplace_are_found_from_any_marketplace()
    {
        using var temp = new TempDirectory();
        FakeClaude.Write(temp.Root, @"plugins\installed_plugins.json",
            """{"version":2,"plugins":{"cc-hud@cc-hud":[],"roster-usage@RathaTart-claude-roster":[]}}""");
        FakeClaude.Write(temp.Root, @"plugins\known_marketplaces.json", """{"claude-roster":{},"cc-hud":{}}""");

        Assert.True(RosterAddOn.IsInstalled(temp.Root));
        Assert.True(RosterAddOn.HasMarketplace(temp.Root));
    }

    [Fact]
    public void Without_Claude_Code_or_the_add_on_there_is_nothing_to_remove()
    {
        using var temp = new TempDirectory();
        Assert.False(RosterAddOn.IsInstalled(temp.Root));
        Assert.False(RosterAddOn.HasMarketplace(temp.Root));

        FakeClaude.Write(temp.Root, @"plugins\installed_plugins.json", """{"plugins":{"roster-usage-other@x":[]}}""");
        Assert.False(RosterAddOn.IsInstalled(temp.Root));
    }

    [Fact]
    public async Task Removing_deletes_only_the_add_ons_saved_figures()
    {
        using var temp = new TempDirectory();
        FakeClaude.Write(temp.Root, @"plugins\store\roster-usage_claude-roster-1.json", "{}");
        FakeClaude.Write(temp.Root, @"plugins\store\cc-hud_cc-hud-2.json", "{}");

        // Nothing is installed here, so no Claude Code command runs.
        Assert.Null(await RosterAddOn.RemoveAsync(temp.Root));

        Assert.False(FakeClaude.Exists(temp.Root, @"plugins\store\roster-usage_claude-roster-1.json"));
        Assert.True(FakeClaude.Exists(temp.Root, @"plugins\store\cc-hud_cc-hud-2.json"));
    }
}
