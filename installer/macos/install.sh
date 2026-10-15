#!/usr/bin/env bash
# Glowpilot macOS installer — no sudo required.
# Supports Apple Silicon (osx-arm64) and Intel (osx-x64).
#
# Usage: bash installer/macos/install.sh
#
# Idempotent: safe to re-run; re-publishes binaries, reinstalls the launchd
# agent, and merges (never overwrites) Claude Code hook configuration.

set -euo pipefail

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

readonly INSTALL_BASE="${HOME}/.local/share/glowpilot"
readonly BIN_DIR="${INSTALL_BASE}/bin"
readonly LOG_DIR="${INSTALL_BASE}/logs"
readonly PLIST_LABEL="com.glowpilot.service"
readonly PLIST_PATH="${HOME}/Library/LaunchAgents/${PLIST_LABEL}.plist"
readonly SERVICE_PORT=57491

# ---------------------------------------------------------------------------
# Helpers sourced by install.tests.sh
# ---------------------------------------------------------------------------

# Returns the dotnet runtime identifier for the current (or overridden) arch.
# ARCH_OVERRIDE may be set by tests to inject a fake arch without mocking uname.
detect_arch() {
    local arch
    arch="${ARCH_OVERRIDE:-$(uname -m)}"
    case "$arch" in
        arm64) echo "osx-arm64" ;;
        *)     echo "osx-x64" ;;
    esac
}

# Merge Glowpilot Claude Code hook entries into a Claude settings.json file.
#
# Arguments:
#   $1 — path to settings.json (created if absent)
#   $2 — absolute path to the glowpilot-claude-code binary
#
# Behaviour:
#   • Creates the file with '{}' if it does not exist.
#   • For each of the four hook events (PreToolUse, PostToolUse, Notification,
#     Stop) appends a hook entry unless the adapter command is already listed.
#   • All other keys and existing hook entries are preserved.
#
# Requires: jq
merge_hook_config() {
    local settings_file="$1"
    local adapter_path="$2"

    # Create the file if absent.
    if [ ! -f "$settings_file" ]; then
        mkdir -p "$(dirname "$settings_file")"
        printf '{}\n' > "$settings_file"
    fi

    local hooks=("PreToolUse" "PostToolUse" "Notification" "Stop")

    local current_json
    current_json=$(cat "$settings_file")

    for hook in "${hooks[@]}"; do
        # Check whether the adapter command is already present in this hook array.
        local already_present
        already_present=$(
            printf '%s' "$current_json" \
            | jq --arg hook "$hook" --arg cmd "$adapter_path" '
                (.hooks[$hook] // [])
                | map(.hooks // [] | map(.command) | any(. == $cmd))
                | any
            '
        )

        if [ "$already_present" != "true" ]; then
            current_json=$(
                printf '%s' "$current_json" \
                | jq \
                    --arg hook "$hook" \
                    --arg cmd  "$adapter_path" '
                    .hooks[$hook] //= []
                    | .hooks[$hook] += [
                        {
                          "matcher": "",
                          "hooks": [{"type": "command", "command": $cmd}]
                        }
                      ]
                '
            )
        fi
    done

    printf '%s\n' "$current_json" > "$settings_file"
}

# Unload the launchd agent (if loaded) then load it with -w (enabled at login).
#
# Arguments:
#   $1 — absolute path to the plist file
load_launchd_service() {
    local plist="$1"

    # Attempt unload; ignore errors (not loaded yet on first install).
    launchctl unload "$plist" 2>/dev/null || true

    launchctl load -w "$plist"
}

# ---------------------------------------------------------------------------
# Step functions (each prints [OK] / [FAIL] and exits on critical failure)
# ---------------------------------------------------------------------------

step_check_dotnet() {
    echo "--- Checking .NET SDK version..."
    if ! command -v dotnet &>/dev/null; then
        echo "[FAIL] dotnet not found. Install .NET 8+ from https://dot.net/download"
        exit 1
    fi

    local version_str major
    version_str=$(dotnet --version 2>/dev/null)
    major=$(printf '%s' "$version_str" | cut -d. -f1)

    if [ "${major:-0}" -lt 8 ]; then
        echo "[FAIL] .NET ${version_str} is too old. Glowpilot requires .NET 8 or newer."
        exit 1
    fi

    echo "[OK] .NET ${version_str} found."
}

step_publish_binaries() {
    local rid="$1"
    # Resolve the repo root: two directories up from this script.
    local script_dir
    script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    local repo_root
    repo_root="$(cd "${script_dir}/../.." && pwd)"

    echo "--- Publishing binaries for ${rid}..."

    mkdir -p "${LOG_DIR}"

    local projects=(
        "service/Glowpilot.Service.csproj:service"
        "adapters/claude-code/Glowpilot.Adapters.ClaudeCode.csproj:claude-code"
        "adapters/copilot/Glowpilot.Adapters.CopilotCli.csproj:copilot"
    )

    for entry in "${projects[@]}"; do
        local csproj="${entry%%:*}"
        local subdir="${entry##*:}"
        local out_dir="${BIN_DIR}/${subdir}"
        mkdir -p "$out_dir"

        echo "  Publishing ${csproj} → ${out_dir}"
        dotnet publish \
            "${repo_root}/${csproj}" \
            --self-contained \
            -r "$rid" \
            -c Release \
            -o "$out_dir" \
            --nologo \
            -v quiet
    done

    echo "[OK] Binaries published to ${BIN_DIR}."
}

step_install_plist() {
    echo "--- Installing launchd plist..."

    mkdir -p "$(dirname "$PLIST_PATH")"

    cat > "$PLIST_PATH" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${PLIST_LABEL}</string>
  <key>ProgramArguments</key>
  <array>
    <string>${BIN_DIR}/service/Glowpilot.Service</string>
  </array>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>StandardOutPath</key>
  <string>${LOG_DIR}/service.log</string>
  <key>StandardErrorPath</key>
  <string>${LOG_DIR}/service.err</string>
</dict>
</plist>
EOF

    echo "[OK] Plist written to ${PLIST_PATH}."
}

step_load_service() {
    echo "--- Loading launchd service..."
    if load_launchd_service "$PLIST_PATH"; then
        echo "[OK] Service loaded (${PLIST_LABEL})."
    else
        echo "[FAIL] launchctl load failed. Check ${LOG_DIR}/service.err for details."
        exit 1
    fi
}

step_merge_hook_config() {
    echo "--- Merging Claude Code hook configuration..."

    if ! command -v jq &>/dev/null; then
        echo "[FAIL] jq not found. Install it with: brew install jq"
        exit 1
    fi

    # Claude Code may keep settings in either of these locations.
    local settings_file=""
    local candidates=(
        "${HOME}/.claude/settings.json"
        "${HOME}/Library/Application Support/Claude/settings.json"
    )
    for candidate in "${candidates[@]}"; do
        if [ -f "$candidate" ]; then
            settings_file="$candidate"
            break
        fi
    done
    # Default to the standard XDG-ish location if neither exists yet.
    if [ -z "$settings_file" ]; then
        settings_file="${HOME}/.claude/settings.json"
    fi

    local adapter_bin="${BIN_DIR}/claude-code/glowpilot-claude-code"

    merge_hook_config "$settings_file" "$adapter_bin"

    echo "[OK] Hook config merged into ${settings_file}."
}

step_smoke_test() {
    echo "--- Waiting for Service to become ready (max 10 s)..."

    local elapsed=0
    while [ "$elapsed" -lt 10 ]; do
        if curl -sf "http://localhost:${SERVICE_PORT}/status" &>/dev/null; then
            echo "[OK] Service is responding on port ${SERVICE_PORT}."
            return 0
        fi
        sleep 1
        elapsed=$((elapsed + 1))
    done

    echo "[FAIL] Service did not respond on http://localhost:${SERVICE_PORT}/status within 10 s."
    echo "       Check logs: ${LOG_DIR}/service.err"
    exit 1
}

# ---------------------------------------------------------------------------
# Main (only runs when the script is executed directly, not sourced)
# ---------------------------------------------------------------------------

main() {
    echo "=== Glowpilot macOS Installer ==="
    echo ""

    local rid
    rid=$(detect_arch)
    echo "Detected architecture: ${rid}"
    echo ""

    step_check_dotnet
    step_publish_binaries "$rid"
    step_install_plist
    step_load_service
    step_merge_hook_config
    step_smoke_test

    echo ""
    echo "=== Glowpilot installed successfully! ==="
    echo "    Service logs: ${LOG_DIR}/service.log"
    echo "    To uninstall: launchctl unload -w ${PLIST_PATH}"
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi
