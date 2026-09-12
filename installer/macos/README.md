# Glowpilot — macOS Installer

Installs Glowpilot as a per-user launchd service. No `sudo` required.

## Prerequisites

- .NET 8 SDK or newer — <https://dot.net/download>
- `jq` — `brew install jq`

## Install

```bash
bash installer/macos/install.sh
```

The script is idempotent: running it again re-publishes the binaries, reinstalls the launchd plist, and re-merges the Claude Code hook configuration without duplicating entries.

## What the installer does

1. Verifies `dotnet --version` ≥ 8.0.
2. Publishes self-contained binaries for the detected architecture (`osx-arm64` on Apple Silicon, `osx-x64` on Intel) to `~/.local/share/glowpilot/bin/`.
3. Writes a launchd plist to `~/Library/LaunchAgents/com.glowpilot.service.plist` so the Service starts at login and is kept alive.
4. Loads (or reloads) the launchd agent via `launchctl`.
5. Merges Glowpilot hook entries into your Claude Code `settings.json` without overwriting other settings.
6. Smoke-tests the Service by polling `http://localhost:57491/status` for up to 10 seconds.

## Uninstall

```bash
launchctl unload -w ~/Library/LaunchAgents/com.glowpilot.service.plist
rm ~/Library/LaunchAgents/com.glowpilot.service.plist
rm -rf ~/.local/share/glowpilot
```

To remove the hook entries from Claude Code, edit `~/.claude/settings.json` and remove the `glowpilot-claude-code` entries from the `hooks` object.

## Running the tests

```bash
bash installer/macos/install.tests.sh
```

Requires `jq` and a POSIX shell. No network access or actual `dotnet` needed.
