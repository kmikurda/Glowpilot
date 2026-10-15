#!/usr/bin/env bash
# install.tests.sh — unit tests for install.sh helper functions
# Run: bash installer/linux/install.tests.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---- minimal test harness ----

PASS=0
FAIL=0
ERRORS=()

assert_equals() {
    local expected="$1" actual="$2" label="${3:-assertion}"
    if [[ "$expected" == "$actual" ]]; then
        echo "[PASS] $label"
        (( PASS++ )) || true
    else
        echo "[FAIL] $label"
        echo "       expected: $expected"
        echo "       actual:   $actual"
        ERRORS+=("$label")
        (( FAIL++ )) || true
    fi
}

assert_contains() {
    local needle="$1" haystack="$2" label="${3:-contains}"
    if [[ "$haystack" == *"$needle"* ]]; then
        echo "[PASS] $label"
        (( PASS++ )) || true
    else
        echo "[FAIL] $label — '$needle' not found in '$haystack'"
        ERRORS+=("$label")
        (( FAIL++ )) || true
    fi
}

assert_not_contains() {
    local needle="$1" haystack="$2" label="${3:-not-contains}"
    if [[ "$haystack" != *"$needle"* ]]; then
        echo "[PASS] $label"
        (( PASS++ )) || true
    else
        echo "[FAIL] $label — '$needle' unexpectedly found in '$haystack'"
        ERRORS+=("$label")
        (( FAIL++ )) || true
    fi
}

assert_file_contains() {
    local file="$1" needle="$2" label="${3:-file-contains}"
    local content
    content=$(cat "$file")
    assert_contains "$needle" "$content" "$label"
}

# ---- source helper functions from install.sh ----
# install.sh exports its functions when sourced in test mode
# shellcheck source=install.sh
source "$SCRIPT_DIR/install.sh" --test-mode

# ==============================================================================
# Tests: detect_rid
# ==============================================================================

echo ""
echo "=== detect_rid ==="

test_detect_rid_x86_64() {
    local result
    result=$(detect_rid "x86_64")
    assert_equals "linux-x64" "$result" "detect_rid: x86_64 → linux-x64"
}

test_detect_rid_aarch64() {
    local result
    result=$(detect_rid "aarch64")
    assert_equals "linux-arm64" "$result" "detect_rid: aarch64 → linux-arm64"
}

test_detect_rid_unknown_fails() {
    local exit_code=0
    detect_rid "s390x" >/dev/null 2>&1 || exit_code=$?
    if [[ $exit_code -ne 0 ]]; then
        echo "[PASS] detect_rid: unknown arch → non-zero exit"
        (( PASS++ )) || true
    else
        echo "[FAIL] detect_rid: unknown arch should fail"
        ERRORS+=("detect_rid: unknown arch should fail")
        (( FAIL++ )) || true
    fi
}

test_detect_rid_x86_64
test_detect_rid_aarch64
test_detect_rid_unknown_fails

# ==============================================================================
# Tests: merge_hook_config
# ==============================================================================

echo ""
echo "=== merge_hook_config ==="

TMPDIR_TESTS=$(mktemp -d)
trap 'rm -rf "$TMPDIR_TESTS"' EXIT

test_merge_hook_adds_entry_to_empty_file() {
    local settings="$TMPDIR_TESTS/settings_empty.json"
    echo '{}' > "$settings"
    merge_hook_config "$settings" "/home/user/.local/share/glowpilot/bin/claude-code/glowpilot-claude-code"
    assert_file_contains "$settings" "glowpilot-claude-code" \
        "merge_hook: adds entry to empty settings"
    assert_file_contains "$settings" "PreToolUse" \
        "merge_hook: adds PreToolUse key"
    assert_file_contains "$settings" "PostToolUse" \
        "merge_hook: adds PostToolUse key"
    assert_file_contains "$settings" "Notification" \
        "merge_hook: adds Notification key"
    assert_file_contains "$settings" "Stop" \
        "merge_hook: adds Stop key"
}

test_merge_hook_no_duplicate() {
    local settings="$TMPDIR_TESTS/settings_dup.json"
    local exe="/home/user/.local/share/glowpilot/bin/claude-code/glowpilot-claude-code"
    echo '{}' > "$settings"
    merge_hook_config "$settings" "$exe"
    merge_hook_config "$settings" "$exe"  # second call — must not duplicate
    # Count occurrences of the exe path in the file
    local count
    count=$(grep -c "glowpilot-claude-code" "$settings")
    # 4 hook events × 1 entry each = 4 occurrences
    assert_equals "4" "$count" \
        "merge_hook: no duplicate after second call (4 hook events, 1 entry each)"
}

test_merge_hook_preserves_other_keys() {
    local settings="$TMPDIR_TESTS/settings_keys.json"
    cat > "$settings" << 'EOF'
{
  "theme": "dark",
  "model": "claude-opus-4",
  "hooks": {
    "PreToolUse": [
      {"matcher": "Bash", "hooks": [{"type": "command", "command": "/usr/local/bin/some-other-tool"}]}
    ]
  }
}
EOF
    merge_hook_config "$settings" "/home/user/.local/share/glowpilot/bin/claude-code/glowpilot-claude-code"
    assert_file_contains "$settings" '"theme": "dark"' \
        "merge_hook: preserves top-level key 'theme'"
    assert_file_contains "$settings" '"model": "claude-opus-4"' \
        "merge_hook: preserves top-level key 'model'"
    assert_file_contains "$settings" "some-other-tool" \
        "merge_hook: preserves existing PreToolUse entry"
    assert_file_contains "$settings" "glowpilot-claude-code" \
        "merge_hook: still adds glowpilot entry"
}

test_merge_hook_adds_entry_when_missing_from_existing_hooks() {
    local settings="$TMPDIR_TESTS/settings_partial.json"
    local exe="/home/user/.local/share/glowpilot/bin/claude-code/glowpilot-claude-code"
    # File has hooks but NOT with glowpilot
    cat > "$settings" << 'EOF'
{
  "hooks": {
    "PreToolUse": [
      {"matcher": "Bash", "hooks": [{"type": "command", "command": "/usr/bin/other"}]}
    ]
  }
}
EOF
    merge_hook_config "$settings" "$exe"
    assert_file_contains "$settings" "glowpilot-claude-code" \
        "merge_hook: adds entry when other hooks present"
    assert_file_contains "$settings" "/usr/bin/other" \
        "merge_hook: keeps other pre-existing hook entry"
}

test_merge_hook_adds_entry_to_empty_file
test_merge_hook_no_duplicate
test_merge_hook_preserves_other_keys
test_merge_hook_adds_entry_when_missing_from_existing_hooks

# ==============================================================================
# Tests: enable_service (systemctl mock)
# ==============================================================================

echo ""
echo "=== enable_service (systemctl mock) ==="

test_systemctl_enable_now_is_called() {
    # Mock systemctl: record calls
    local calls_log="$TMPDIR_TESTS/systemctl_calls.log"
    : > "$calls_log"

    # Override systemctl in this subshell
    systemctl() {
        echo "$*" >> "$calls_log"
    }
    export -f systemctl

    enable_service

    local calls
    calls=$(cat "$calls_log")

    assert_contains "--user daemon-reload" "$calls" \
        "enable_service: calls 'systemctl --user daemon-reload'"
    assert_contains "--user enable --now glowpilot" "$calls" \
        "enable_service: calls 'systemctl --user enable --now glowpilot'"
}

test_systemctl_fallback_when_unavailable() {
    # Mock systemctl to fail (simulate no D-Bus / container)
    systemctl() {
        return 1
    }
    export -f systemctl

    local profile_file="$TMPDIR_TESTS/fake_profile"
    echo "# existing content" > "$profile_file"

    enable_service_with_fallback "$profile_file"

    assert_file_contains "$profile_file" "glowpilot" \
        "enable_service_fallback: appends glowpilot to profile when systemd unavailable"
}

test_systemctl_fallback_no_duplicate() {
    systemctl() { return 1; }
    export -f systemctl

    local profile_file="$TMPDIR_TESTS/fake_profile_dup"
    echo "# existing" > "$profile_file"

    enable_service_with_fallback "$profile_file"
    enable_service_with_fallback "$profile_file"  # second call

    local count
    count=$(grep -c "glowpilot" "$profile_file")
    assert_equals "1" "$count" \
        "enable_service_fallback: no duplicate when called twice"
}

test_systemctl_enable_now_is_called
test_systemctl_fallback_when_unavailable
test_systemctl_fallback_no_duplicate

# ==============================================================================
# Summary
# ==============================================================================

echo ""
echo "=============================="
echo "Results: $PASS passed, $FAIL failed"
if [[ $FAIL -gt 0 ]]; then
    echo "Failed tests:"
    for err in "${ERRORS[@]}"; do
        echo "  - $err"
    done
    exit 1
fi
echo "All tests passed."
