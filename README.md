<p align="center">
  <img src="docs/images/icon.png" width="96" alt="Claude Roster icon: two arrows on a blue tile">
</p>

<h1 align="center">Claude Roster</h1>

<p align="center">
  <b>Switch the Claude desktop app between your accounts from the Windows system tray.</b><br>
  Save your work and personal accounts once, then switch with a click instead of logging out and back in.
</p>

<p align="center">
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white">
  <img alt=".NET 9" src="https://img.shields.io/badge/.NET-9-512BD4?logo=dotnet&logoColor=white">
  <img alt="Local only" src="https://img.shields.io/badge/network-none-2F5D7C">
  <a href="https://github.com/RathaTart/claude-roster/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/RathaTart/claude-roster"></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-green"></a>
</p>

<p align="center">
  <img src="docs/images/menu.png" alt="The Claude Roster tray menu: signed in as Work, with Personal one click away">
  <br>
  <sub>The tray menu. The ✓ marks the account Claude is signed in to; click another one to switch.</sub>
</p>

> [!NOTE]
> Claude Roster is an unofficial community tool. It isn't affiliated with or endorsed by Anthropic.

## Features

- **One-click switching.** Claude closes, swaps to the other account's sign-in, and reopens. It takes a few seconds.
- **Saved sign-ins stay fresh.** Claude refreshes its tokens while you work, so every switch first copies the account you're leaving.
- **Hard to lose an account.** It backs up before every switch and rolls back automatically if anything fails. It refuses to switch away from an account you haven't saved, or from a sign-in it can't read.
- **Claude Code sessions stay with their account.** Each account sees its own sessions in the Code tab.
- **Local only.** No network calls and no telemetry. Saved sign-ins stay on your PC, readable only by your Windows user.
- **Scriptable.** A `claude-roster` command line covers everything the tray menu does.

## Requirements

- Windows 10 or 11 (x64)
- The Claude desktop app: the Microsoft Store version (tested) or the classic installer
- The [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0), which comes with the .NET 9 SDK

## Install

**[Download ClaudeRoster.exe](https://github.com/RathaTart/claude-roster/releases/latest/download/ClaudeRoster.exe)** from the latest release. Save it in a permanent folder and double-click it in File Explorer. There's no installer.

The [release page](https://github.com/RathaTart/claude-roster/releases/latest) also has the `claude-roster.exe` command line, a ZIP with both programs plus the README and license, and `SHA256SUMS.txt` to verify any download:

```powershell
Get-FileHash .\ClaudeRoster.exe -Algorithm SHA256
```

> [!NOTE]
> Releases aren't code-signed, so the first launch may show **Windows protected your PC**. Check the file's hash against `SHA256SUMS.txt`, then choose **More info → Run anyway**. Prefer to build it yourself? See [Build from source](#build-from-source).

The icon appears in the system tray next to the clock. On Windows 11 it may be hidden under the **^** arrow. To keep it on the taskbar, open **Settings → Personalization → Taskbar → Other system tray icons** and turn **ClaudeRoster** on.

> [!IMPORTANT]
> Start Claude Roster from File Explorer or the Start menu, **not from a terminal inside a Claude Code session**. Switching closes Claude, which would also stop anything started from inside it. Claude Roster detects this and refuses to switch.

## How to use

Left- or right-click the tray icon to open the menu.

### 1. Save the account you're signed in to

The first time, the menu shows that Claude's account isn't saved yet. Choose **Save current account…** and give it a name.

<table>
  <tr>
    <td valign="top"><img src="docs/images/first-run.png" alt="Tray menu on first run: Signed in: account not saved yet, with Save current account"></td>
    <td valign="top"><img src="docs/images/save-account.png" alt="The Save current account dialog with the name Work"></td>
  </tr>
</table>

### 2. Add your other account

Choose **Add another account…**. Claude closes and reopens on its sign-in screen. Sign in with the other account, and Claude Roster asks you to name it.

<table>
  <tr>
    <td valign="top"><img src="docs/images/confirm-add.png" alt="Confirmation: Claude will close and reopen on its sign-in screen"></td>
    <td valign="top"><img src="docs/images/name-new-account.png" alt="Naming the newly signed-in account Personal"></td>
  </tr>
</table>

### 3. Switch

Click an account in the menu and confirm. Claude closes and reopens signed in as that account. The confirmation tells you if any Claude Code sessions are running, because they stop when Claude closes.

<p><img src="docs/images/confirm-switch.png" alt="Confirmation: Switch Claude to Personal? Claude will close and reopen. 1 running Claude Code session will stop."></p>

### Rename or remove

**Manage accounts** renames or removes saved accounts. Removing an account only deletes its saved sign-in on this PC; the Claude account itself isn't affected. You can't remove the account that's signed in right now.

<p><img src="docs/images/manage.png" alt="Manage accounts submenu with Rename and Remove"></p>

### Good to know

> [!WARNING]
> **Don't use Claude's own Log out for a saved account.** Logging out ends that session on Anthropic's side, so the saved copy stops working too. To add or change accounts, use **Add another account…** instead.

- **Running Claude Code sessions stop when you switch**, because Claude closes. They're still there when you switch back.
- **Start with Windows** is off by default. When on, it adds an entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **The terminal `claude` CLI isn't covered.** It keeps its own login in `~\.claude`. To use a second account there, give it a separate config folder: `$env:CLAUDE_CONFIG_DIR = "$HOME\.claude-personal"; claude`.
- **Work accounts follow work rules.** If one of your accounts belongs to an employer, check its policy on personal AI accounts on work machines. Each account stays under its own plan, limits and terms.

## How it works

The Claude desktop app is an Electron app. Its sign-in lives in a handful of files in its data folder. Claude Roster copies exactly those in and out, and leaves everything else alone.

<details>
<summary><b>What gets saved and swapped</b></summary>

<br>

| Saved per account | Why |
| --- | --- |
| `Network\Cookies` | the claude.ai session |
| `Local Storage`, `Session Storage`, `IndexedDB`, `WebStorage`, `blob_storage` | claude.ai's local state for that account |
| `Local State` | the key that encrypts the cookies and tokens; each saved copy keeps the key its data was encrypted with |
| `config.json` keys `oauth:*`, `lastKnownAccountUuid`, `bootSidebarPlaceholder` | Claude Code tokens, the account id, and the cached sidebar |

**Left alone:** app settings, MCP servers, extensions, caches, and Claude Code sessions. Claude already keeps Code sessions in a separate folder per account.

Run `claude-roster plan` to see this list with sizes for your own install. It changes nothing.

</details>

<details>
<summary><b>What happens during a switch</b></summary>

<br>

1. Check that the account signed in now is saved. If it isn't, stop, so it can't be lost.
2. Close Claude. It asks politely first, then force-closes after a few seconds, because Claude keeps running in the tray. Only the desktop app's own `Claude.exe` processes are touched, never a `claude.exe` CLI.
3. Back up the live sign-in to `backups\`. The last 5 backups are kept.
4. Copy the live sign-in into the profile it belongs to, so its saved copy has the latest tokens.
5. Put the target account's sign-in in place. If this fails, the backup is restored.
6. Reopen Claude.

Accounts are recognised by Claude's `lastKnownAccountUuid`. If Claude holds tokens but that id can't be read (for example, after an update changes the format), Claude Roster refuses to switch rather than guess.

</details>

## Privacy and security

Everything is stored under `%APPDATA%\ClaudeRoster`. The folder is restricted to your Windows user, SYSTEM and Administrators.

| Path | Contents |
| --- | --- |
| `profiles.json` | account names and ids, no secrets |
| `profiles\<id>\session\` | each account's saved sign-in |
| `backups\` | the live sign-in from before each of the last 5 switches |

> [!CAUTION]
> **Treat saved sign-ins like passwords.** Don't sync this folder to cloud storage, share it, or attach it to a bug report. Anyone who can read it as your Windows user can use those sessions. The encryption is tied to your Windows account, so the files don't work on another PC or for another user.

Claude Roster makes no network requests and sends nothing anywhere.

**To uninstall:** turn off **Start with Windows** if you enabled it, choose **Exit** from the menu, then delete `ClaudeRoster.exe` and `%APPDATA%\ClaudeRoster`.

## Command line

`claude-roster.exe` drives the same logic from a terminal. [Download it](https://github.com/RathaTart/claude-roster/releases/latest/download/claude-roster.exe) from the latest release, or build it from source.

| Command | What it does |
| --- | --- |
| `claude-roster status` | what Claude is signed in to right now |
| `claude-roster list` | saved accounts (`*` = signed in) |
| `claude-roster plan` | dry run: what a switch copies |
| `claude-roster save <name>` | save the account Claude is signed in to |
| `claude-roster switch <name>` | close Claude, swap accounts, reopen |
| `claude-roster add` | save the current account, then open Claude's sign-in screen |
| `claude-roster rename <name> <new>` | rename a saved account |
| `claude-roster remove <name>` | forget a saved account (not the signed-in one) |

`status`, `list` and `plan` are read-only. `switch`, `add` and `remove` ask first; pass `--yes` to skip the prompt.

```text
> claude-roster status
Claude data     C:\Users\you\AppData\Local\Packages\AnthropicPBC.Claude_fnn82j28hfe8t\LocalCache\Roaming\Claude
Store package   AnthropicPBC.Claude_fnn82j28hfe8t
Claude running  yes, 1 Claude Code session(s) open
Signed in       Work (11111111…)
Saved accounts  2
Roster data     C:\Users\you\AppData\Roaming\ClaudeRoster
```

## Build from source

Needs the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
git clone https://github.com/RathaTart/claude-roster.git
```

```powershell
dotnet test claude-roster\ClaudeRoster.sln -c Release
```

```powershell
dotnet publish claude-roster\src\ClaudeRoster -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o claude-roster\publish
```

```powershell
dotnet publish claude-roster\src\ClaudeRoster.Cli -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o claude-roster\publish
```

Both programs end up in `claude-roster\publish`.

| Project | What it is |
| --- | --- |
| `src/ClaudeRoster.Core` | finding Claude, snapshots, process control, the switch sequence |
| `src/ClaudeRoster` | the tray app (WinForms) |
| `src/ClaudeRoster.Cli` | `claude-roster.exe` |
| `tests/ClaudeRoster.Tests` | xUnit tests against fake Claude data folders; they never touch the real one |
| `tools/Screenshots` | regenerates the images in `docs/images` with sample accounts |

The tests and the screenshot tool run on throwaway data. For manual testing, `CLAUDE_ROSTER_CLAUDE_DATA` points Claude Roster at a different Claude data folder, and `CLAUDE_ROSTER_HOME` sets where it keeps its own data.

## Limitations

- Claude's sign-in storage isn't a documented interface, so a Claude update can change it. When that happens, expect Claude Roster to refuse to switch rather than corrupt anything; the storage list in `SessionSnapshotter` and `ClaudeConfig` then needs updating.
- One account is active at a time. It doesn't run two Claude windows side by side.
- The Microsoft Store install is tested. The classic installer (`%LOCALAPPDATA%\AnthropicClaude`) is supported by path rules but hasn't been tested.

## Credits

- The approach follows [roster-companion](https://github.com/tanagornf/roster-companion) by tanagornf, which does the same job for the ChatGPT desktop app.

## License

[MIT](LICENSE) © 2026 RathaTart
