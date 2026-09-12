# Glowpilot — Linux Installer

Installs Glowpilot on Linux without requiring `sudo`. Requires .NET 8+ and Python 3.

## Prerequisites

- [.NET 8 or newer](https://dot.net/download) — `dotnet --version` must report `8.x` or higher
- Python 3 — used for merging JSON hook config
- `curl` — used for the smoke test
- Systemd user session (optional — falls back to `~/.profile` if unavailable)

## Usage

Run from the repository root:

```bash
bash installer/linux/install.sh
```

Or make the script executable and run directly:

```bash
chmod +x installer/linux/install.sh
./installer/linux/install.sh
```

## What the installer does

1. **Checks requirements** — verifies `dotnet` (>=8) and `python3` are available.
2. **Publishes binaries** — detects architecture (`x86_64` → `linux-x64`, `aarch64` → `linux-arm64`) and publishes self-contained executables to `~/.local/share/glowpilot/bin/`.
3. **Installs systemd unit** — writes `~/.config/systemd/user/glowpilot.service`.
4. **Enables and starts the service** — runs `systemctl --user enable --now glowpilot`. Falls back to appending a start line to `~/.profile` if systemd is unavailable (e.g. inside a container without D-Bus).
5. **Registers Claude Code hooks** — merges hook entries into `~/.claude/settings.json` for all four events: `PreToolUse`, `PostToolUse`, `Notification`, and `Stop`. Existing settings and other hooks are preserved; entries are not duplicated on re-install.
6. **Smoke tests** — waits up to 10 seconds for the service to respond on `http://localhost:57491/status`.

## Re-running (idempotent)

The installer is safe to run again after updates. Binaries are overwritten, the systemd unit is replaced, hooks are not duplicated, and `systemctl enable --now` is a no-op if already enabled.

## Installed paths

| Path | Description |
|------|-------------|
| `~/.local/share/glowpilot/bin/service/Glowpilot.Service` | Service binary |
| `~/.local/share/glowpilot/bin/claude-code/glowpilot-claude-code` | Claude Code hook adapter |
| `~/.local/share/glowpilot/bin/copilot/glowpilot-copilot` | GitHub Copilot CLI hook adapter |
| `~/.config/systemd/user/glowpilot.service` | Systemd user unit |
| `~/.claude/settings.json` | Claude Code settings (merged, not replaced) |

## Useful commands

```bash
# Check service status
systemctl --user status glowpilot

# Restart after update
systemctl --user restart glowpilot

# View live logs
journalctl --user -u glowpilot -f

# Stop the service
systemctl --user stop glowpilot

# Uninstall (manual)
systemctl --user disable --now glowpilot
rm ~/.config/systemd/user/glowpilot.service
rm -rf ~/.local/share/glowpilot
```

## Running tests

```bash
bash installer/linux/install.tests.sh
```

Tests cover: architecture detection, JSON hook merging (no duplicates, preserves keys), and systemctl mock verification.
