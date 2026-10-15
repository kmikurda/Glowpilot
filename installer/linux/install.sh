#!/usr/bin/env bash
# install.sh — Glowpilot installer for Linux (no sudo required)
#
# Usage: bash installer/linux/install.sh
#        or: ./installer/linux/install.sh (after chmod +x)
#
# Must be run from the repository root, or the script will detect it automatically.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

INSTALL_BASE="$HOME/.local/share/glowpilot"
SYSTEMD_UNIT_DIR="$HOME/.config/systemd/user"
SYSTEMD_UNIT="$SYSTEMD_UNIT_DIR/glowpilot.service"
CLAUDE_SETTINGS="$HOME/.claude/settings.json"
PROFILE_FILE="$HOME/.profile"

SERVICE_PORT="57491"

# ==============================================================================
# Helper functions (also used by tests)
# ==============================================================================

# detect_rid [arch]
# Maps architecture string to .NET runtime identifier.
# Accepts optional argument for testability (defaults to uname -m output).
detect_rid() {
    local arch="${1:-$(uname -m)}"
    case "$arch" in
        x86_64)  echo "linux-x64" ;;
        aarch64) echo "linux-arm64" ;;
        *)
            echo "[FAIL] Unsupported architecture: $arch — only x86_64 and aarch64 are supported." >&2
            return 1
            ;;
    esac
}

# merge_hook_config <settings_json_path> <exe_path>
# Adds Glowpilot hook entries to all four Claude Code hook events
# (PreToolUse, PostToolUse, Notification, Stop) without duplicating
# existing entries. Preserves all other keys in the settings file.
# Requires python3.
merge_hook_config() {
    local settings_file="$1"
    local exe="$2"

    python3 - "$settings_file" "$exe" << 'PYEOF'
import json
import sys
import os

settings_file = sys.argv[1]
exe = sys.argv[2]

if os.path.exists(settings_file) and os.path.getsize(settings_file) > 0:
    with open(settings_file, "r") as f:
        try:
            data = json.load(f)
        except json.JSONDecodeError:
            data = {}
else:
    data = {}

hooks = data.setdefault("hooks", {})

for event in ["PreToolUse", "PostToolUse", "Notification", "Stop"]:
    entries = hooks.setdefault(event, [])
    # Check if our exe already appears in any hook entry for this event
    already_present = any(
        h.get("command") == exe
        for entry in entries
        for h in entry.get("hooks", [])
    )
    if not already_present:
        entries.append({
            "matcher": "",
            "hooks": [{"type": "command", "command": exe}]
        })

with open(settings_file, "w") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
PYEOF
}

# enable_service
# Reloads the systemd user daemon and enables + starts glowpilot.service.
enable_service() {
    systemctl --user daemon-reload
    systemctl --user enable --now glowpilot
}

# enable_service_with_fallback [profile_file]
# Tries systemd. If unavailable (no D-Bus, container, etc.), falls back to
# appending a start line to the shell profile. The profile_file argument is
# used by tests to inject a custom path (defaults to ~/.profile).
enable_service_with_fallback() {
    local profile="${1:-$PROFILE_FILE}"

    if systemctl --user is-system-running &>/dev/null || systemctl --user status &>/dev/null 2>&1; then
        enable_service
    else
        echo "[WARN] systemd user session unavailable — adding to $profile for session start"
        local start_line="$INSTALL_BASE/bin/service/Glowpilot.Service &"
        grep -qF "glowpilot" "$profile" 2>/dev/null \
            || echo "$start_line" >> "$profile"
    fi
}

# ==============================================================================
# Main installer (skipped when sourced in test mode)
# ==============================================================================

if [[ "${1:-}" == "--test-mode" ]]; then
    # Only export functions; do not run installation steps
    return 0 2>/dev/null || true
fi

# ---- Step 1: Check requirements ----

echo ""
echo "=== Glowpilot Linux Installer ==="
echo ""

echo "Checking requirements..."

if ! command -v dotnet &>/dev/null; then
    echo "[FAIL] dotnet not found. Install .NET 8+ from https://dot.net and re-run."
    exit 1
fi

DOTNET_VERSION=$(dotnet --version 2>/dev/null | head -1)
DOTNET_MAJOR=$(echo "$DOTNET_VERSION" | cut -d. -f1)

if [[ -z "$DOTNET_MAJOR" ]] || [[ "$DOTNET_MAJOR" -lt 8 ]]; then
    echo "[FAIL] dotnet $DOTNET_VERSION is too old. Glowpilot requires .NET 8 or newer."
    exit 1
fi

echo "[OK] dotnet $DOTNET_VERSION"

if ! command -v python3 &>/dev/null; then
    echo "[FAIL] python3 not found. Install Python 3 and re-run."
    exit 1
fi
echo "[OK] python3 found"

if ! command -v curl &>/dev/null; then
    echo "[FAIL] curl not found. Install curl and re-run."
    exit 1
fi
echo "[OK] curl found"

# ---- Step 2: Publish binaries ----

echo ""
echo "Publishing binaries..."

RID=$(detect_rid) || exit 1
echo "[OK] Architecture: $(uname -m) → $RID"

publish_project() {
    local name="$1"
    local csproj="$2"
    local out_dir="$3"

    echo "  Publishing $name..."
    if dotnet publish \
        --self-contained \
        -r "$RID" \
        -c Release \
        -o "$out_dir" \
        "$REPO_ROOT/$csproj" \
        --nologo \
        -v quiet 2>&1; then
        echo "[OK] Published $name → $out_dir"
    else
        echo "[FAIL] dotnet publish failed for $name"
        exit 1
    fi
}

mkdir -p "$INSTALL_BASE/bin/service"
mkdir -p "$INSTALL_BASE/bin/claude-code"
mkdir -p "$INSTALL_BASE/bin/copilot"

publish_project "Glowpilot.Service" \
    "service/Glowpilot.Service.csproj" \
    "$INSTALL_BASE/bin/service"

publish_project "glowpilot-claude-code" \
    "adapters/claude-code/Glowpilot.Adapters.ClaudeCode.csproj" \
    "$INSTALL_BASE/bin/claude-code"

publish_project "glowpilot-copilot" \
    "adapters/copilot/Glowpilot.Adapters.CopilotCli.csproj" \
    "$INSTALL_BASE/bin/copilot"

# ---- Step 3: Install systemd user unit ----

echo ""
echo "Installing systemd unit..."

mkdir -p "$SYSTEMD_UNIT_DIR"

cat > "$SYSTEMD_UNIT" << 'UNIT'
[Unit]
Description=Glowpilot Session Status Service
After=graphical-session.target

[Service]
ExecStart=%h/.local/share/glowpilot/bin/service/Glowpilot.Service
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
UNIT

echo "[OK] Systemd unit written: $SYSTEMD_UNIT"

# ---- Step 4: Enable and start service ----

echo ""
echo "Enabling service..."

enable_service_with_fallback

echo "[OK] Service enabled"

# ---- Step 5: Merge Claude Code hook config ----

echo ""
echo "Configuring Claude Code hooks..."

CLAUDE_EXE="$HOME/.local/share/glowpilot/bin/claude-code/glowpilot-claude-code"

mkdir -p "$(dirname "$CLAUDE_SETTINGS")"
if [[ ! -f "$CLAUDE_SETTINGS" ]]; then
    echo "{}" > "$CLAUDE_SETTINGS"
fi

if merge_hook_config "$CLAUDE_SETTINGS" "$CLAUDE_EXE"; then
    echo "[OK] Claude Code hooks merged: $CLAUDE_SETTINGS"
else
    echo "[FAIL] Failed to merge Claude Code hook config"
    exit 1
fi

# ---- Step 6: Smoke test ----

echo ""
echo "Smoke testing service (waiting up to 10s)..."

STATUS_URL="http://localhost:${SERVICE_PORT}/status"
SMOKE_OK=false

for i in $(seq 1 10); do
    if curl -sf "$STATUS_URL" &>/dev/null; then
        SMOKE_OK=true
        break
    fi
    sleep 1
done

if $SMOKE_OK; then
    echo "[OK] Service is responding at $STATUS_URL"
else
    echo "[FAIL] Service did not respond at $STATUS_URL within 10s"
    echo "       Check: systemctl --user status glowpilot"
    exit 1
fi

echo ""
echo "=== Glowpilot installed successfully ==="
echo ""
echo "The LED ring will reflect your AI coding session states."
echo "Claude Code hooks have been registered in $CLAUDE_SETTINGS"
echo ""
echo "Useful commands:"
echo "  systemctl --user status glowpilot"
echo "  systemctl --user restart glowpilot"
echo "  journalctl --user -u glowpilot -f"
