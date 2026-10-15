#!/usr/bin/env bash
# Tests for install.sh helper functions.
# Run: bash installer/macos/install.tests.sh
# Requires: jq (available on macOS via Homebrew or Xcode CLT)

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Source helpers from install.sh without executing main.
# shellcheck source=install.sh
source "$SCRIPT_DIR/install.sh"

# ---------------------------------------------------------------------------
# Minimal test harness
# ---------------------------------------------------------------------------

PASS=0
FAIL=0

assert_equals() {
    local desc="$1" expected="$2" actual="$3"
    if [ "$expected" = "$actual" ]; then
        echo "[PASS] $desc"
        PASS=$((PASS + 1))
    else
        echo "[FAIL] $desc"
        echo "       expected: $expected"
        echo "       actual:   $actual"
        FAIL=$((FAIL + 1))
    fi
}

assert_contains() {
    local desc="$1" needle="$2" haystack="$3"
    if echo "$haystack" | grep -qF "$needle"; then
        echo "[PASS] $desc"
        PASS=$((PASS + 1))
    else
        echo "[FAIL] $desc"
        echo "       expected to contain: $needle"
        echo "       in: $haystack"
        FAIL=$((FAIL + 1))
    fi
}

assert_not_contains() {
    local desc="$1" needle="$2" haystack="$3"
    if ! echo "$haystack" | grep -qF "$needle"; then
        echo "[PASS] $desc"
        PASS=$((PASS + 1))
    else
        echo "[FAIL] $desc"
        echo "       expected NOT to contain: $needle"
        echo "       in: $haystack"
        FAIL=$((FAIL + 1))
    fi
}

assert_file_exists() {
    local desc="$1" path="$2"
    if [ -f "$path" ]; then
        echo "[PASS] $desc"
        PASS=$((PASS + 1))
    else
        echo "[FAIL] $desc — file not found: $path"
        FAIL=$((FAIL + 1))
    fi
    return 0
}

# ---------------------------------------------------------------------------
# Helpers for test isolation
# ---------------------------------------------------------------------------

make_tmp_dir() {
    mktemp -d
}

# ---------------------------------------------------------------------------
# Test 1: detect_arch — arm64 → osx-arm64
# ---------------------------------------------------------------------------
test_detect_arch_arm64() {
    local result
    result=$(ARCH_OVERRIDE="arm64" detect_arch)
    assert_equals "detect_arch: arm64 maps to osx-arm64" "osx-arm64" "$result"
}

# ---------------------------------------------------------------------------
# Test 2: detect_arch — x86_64 → osx-x64
# ---------------------------------------------------------------------------
test_detect_arch_x86_64() {
    local result
    result=$(ARCH_OVERRIDE="x86_64" detect_arch)
    assert_equals "detect_arch: x86_64 maps to osx-x64" "osx-x64" "$result"
}

# ---------------------------------------------------------------------------
# Test 3: detect_arch — any unknown → osx-x64 (safe fallback)
# ---------------------------------------------------------------------------
test_detect_arch_unknown_fallback() {
    local result
    result=$(ARCH_OVERRIDE="i686" detect_arch)
    assert_equals "detect_arch: unknown arch falls back to osx-x64" "osx-x64" "$result"
}

# ---------------------------------------------------------------------------
# Test 4: merge_hook_config — adds all four hook entries to empty settings
# ---------------------------------------------------------------------------
test_merge_hook_adds_entries_to_empty_settings() {
    local tmp_dir
    tmp_dir=$(make_tmp_dir)
    local settings="$tmp_dir/settings.json"
    local adapter_path="/home/user/.local/share/glowpilot/bin/claude-code/glowpilot-claude-code"

    # settings.json does not exist yet
    merge_hook_config "$settings" "$adapter_path"

    assert_file_exists "merge_hook_config: creates settings.json" "$settings"

    local content
    content=$(cat "$settings")

    for hook in PreToolUse PostToolUse Notification Stop; do
        assert_contains "merge_hook_config: $hook entry present" \
            "\"$hook\"" "$content"
    done

    assert_contains "merge_hook_config: adapter command present" \
        "$adapter_path" "$content"

    rm -rf "$tmp_dir"
}

# ---------------------------------------------------------------------------
# Test 5: merge_hook_config — does NOT duplicate an existing entry
# ---------------------------------------------------------------------------
test_merge_hook_no_duplicate() {
    local tmp_dir
    tmp_dir=$(make_tmp_dir)
    local settings="$tmp_dir/settings.json"
    local adapter_path="/home/user/.local/share/glowpilot/bin/claude-code/glowpilot-claude-code"

    # Pre-populate settings.json with the adapter already present.
    cat > "$settings" <<EOF
{
  "hooks": {
    "PreToolUse":  [{"matcher":"","hooks":[{"type":"command","command":"$adapter_path"}]}],
    "PostToolUse": [{"matcher":"","hooks":[{"type":"command","command":"$adapter_path"}]}],
    "Notification":[{"matcher":"","hooks":[{"type":"command","command":"$adapter_path"}]}],
    "Stop":        [{"matcher":"","hooks":[{"type":"command","command":"$adapter_path"}]}]
  }
}
EOF

    merge_hook_config "$settings" "$adapter_path"

    # Count using jq — independent of whitespace formatting in the output file.
    local count
    count=$(jq --arg cmd "$adapter_path" '
        [ .hooks | to_entries[] | .value[]? | .hooks[]? | select(.command == $cmd) ] | length
    ' "$settings")
    assert_equals "merge_hook_config: adapter appears exactly 4 times (one per hook)" "4" "$count"

    rm -rf "$tmp_dir"
}

# ---------------------------------------------------------------------------
# Test 6: merge_hook_config — preserves existing unrelated keys in settings.json
# ---------------------------------------------------------------------------
test_merge_hook_preserves_other_keys() {
    local tmp_dir
    tmp_dir=$(make_tmp_dir)
    local settings="$tmp_dir/settings.json"
    local adapter_path="/home/user/.local/share/glowpilot/bin/claude-code/glowpilot-claude-code"

    cat > "$settings" <<'EOF'
{
  "theme": "dark",
  "model": "claude-opus-4-5",
  "hooks": {
    "PreToolUse": [{"matcher":"Bash","hooks":[{"type":"command","command":"/some/other/tool"}]}]
  }
}
EOF

    merge_hook_config "$settings" "$adapter_path"

    local content
    content=$(cat "$settings")

    assert_contains "merge_hook_config: preserves theme key" '"theme"' "$content"
    assert_contains "merge_hook_config: preserves model key" '"model"' "$content"
    assert_contains "merge_hook_config: preserves existing Bash matcher" '/some/other/tool' "$content"
    assert_contains "merge_hook_config: adds new adapter" "$adapter_path" "$content"

    rm -rf "$tmp_dir"
}

# ---------------------------------------------------------------------------
# Test 7: launchctl mock — load is called with the correct plist path
# ---------------------------------------------------------------------------
test_launchctl_load_called() {
    local tmp_dir
    tmp_dir=$(make_tmp_dir)
    local plist_path="$tmp_dir/com.glowpilot.service.plist"
    local call_log="$tmp_dir/launchctl_calls.log"

    # Create a minimal plist so the function can unload/load it.
    cat > "$plist_path" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>Label</key><string>com.glowpilot.service</string></dict></plist>
EOF

    # Place a fake launchctl in a temp bin dir that logs its invocations.
    local fake_bin="$tmp_dir/bin"
    mkdir -p "$fake_bin"
    cat > "$fake_bin/launchctl" <<EOF
#!/usr/bin/env bash
echo "\$@" >> "$call_log"
exit 0
EOF
    chmod +x "$fake_bin/launchctl"

    # Run load_launchd_service with the fake launchctl on PATH.
    PATH="$fake_bin:$PATH" load_launchd_service "$plist_path"

    assert_file_exists "launchctl mock: call log created" "$call_log"

    local calls
    calls=$(cat "$call_log")

    assert_contains "launchctl: unload called before load" \
        "unload" "$calls"
    assert_contains "launchctl: load -w called" \
        "load -w" "$calls"
    assert_contains "launchctl: plist path passed to load" \
        "$plist_path" "$calls"

    rm -rf "$tmp_dir"
}

# ---------------------------------------------------------------------------
# Run all tests
# ---------------------------------------------------------------------------

echo "=== Glowpilot macOS installer — unit tests ==="
echo ""

test_detect_arch_arm64
test_detect_arch_x86_64
test_detect_arch_unknown_fallback
test_merge_hook_adds_entries_to_empty_settings
test_merge_hook_no_duplicate
test_merge_hook_preserves_other_keys
test_launchctl_load_called

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="

[ "$FAIL" -eq 0 ]
