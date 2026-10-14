using System.Text.Json.Nodes;
using ClaudeRoster.Core;

namespace ClaudeRoster.Tests;

public sealed class RecognitionTests
{
    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\AnthropicPBC.Claude_2.31226.1.0_x64__fnn82j28hfe8t\app\Claude.exe", true)]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\claude.exe", true)]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\app-0.9.0\claude.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\AnthropicPBC.Claude_2.31226.1.0_x64__fnn82j28hfe8t\app\resources\claude.exe", false)]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\app-0.9.0\resources\claude.exe", false)]
    [InlineData(@"C:\Users\me\AppData\Roaming\Claude\claude-code\2.1.295\d2e29494ec83\claude.exe", false)]
    [InlineData(@"C:\Users\me\.vscode\extensions\anthropic.claude-code-2.1.295-win32-x64\resources\native-binary\claude.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\AnthropicPBC.Claude_2.31226.1.0_x64__fnn82j28hfe8t\app\Updater.exe", false)]
    [InlineData(null, false)]
    public void Only_the_desktop_app_counts_as_Claude(string? path, bool expected) =>
        Assert.Equal(expected, ClaudeProcess.IsDesktopExecutable(path));

    [Theory]
    [InlineData("AnthropicPBC.Claude_fnn82j28hfe8t!Claude", true)]
    [InlineData("AnthropicPBC.Claude_fnn82j28hfe8t!Claude --flag", false)]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", false)]
    [InlineData(null, false)]
    public void Only_Claude_store_ids_are_launched(string? appId, bool expected) =>
        Assert.Equal(expected, ClaudeProcess.IsClaudeAppUserModelId(appId));

    [Theory]
    [InlineData("oauth:tokenCache", true)]
    [InlineData("oauth:tokenCacheV2", true)]
    [InlineData("lastKnownAccountUuid", true)]
    [InlineData("bootSidebarPlaceholder", true)]
    [InlineData("locale", false)]
    [InlineData("windowSizeWasSignedIn", false)]
    [InlineData("dxt:allowlistEnabled:0f0f0f0f-1234-4abc-8def-0123456789ab", false)]
    public void Account_keys_are_told_apart_from_device_settings(string key, bool expected) =>
        Assert.Equal(expected, ClaudeConfig.IsAccountKey(key));

    [Fact]
    public void Account_id_must_look_like_an_id()
    {
        Assert.Null(ClaudeConfig.ReadAccountUuid(new JsonObject { ["lastKnownAccountUuid"] = "not-an-id" }));
        Assert.Null(ClaudeConfig.ReadAccountUuid(new JsonObject { ["lastKnownAccountUuid"] = 42 }));
        Assert.Null(ClaudeConfig.ReadAccountUuid(new JsonObject()));
        Assert.Equal(
            FakeClaude.AccountA,
            ClaudeConfig.ReadAccountUuid(new JsonObject { ["lastKnownAccountUuid"] = FakeClaude.AccountA }));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData(@"..\..\Windows", false)]
    [InlineData("", false)]
    public void Profile_ids_cannot_escape_the_roster_folder(string id, bool expected) =>
        Assert.Equal(expected, RosterPaths.IsValidProfileId(id));
}
