# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## README must stay in sync

Whenever you change **any of the following**, update `README.md` in the same commit:

- LED state names, colors, or animations (firmware or shared enum)
- State transition logic — when states change, priority rules, eviction timeouts
- Hook event mappings (Claude Code or Copilot CLI adapter)
- Debounce or stale timeout values or their semantics
- Wire protocol format
- New configuration keys in `GlowpilotConfig`
- Installation steps or Task Scheduler behavior
- Any known limitation that is resolved or a new one discovered

The README is the single source of truth for how the system behaves. Outdated docs are worse than no docs.

---

## Build and test

All .NET commands run from the repo root (no `.sln` — build per-project or by directory).

```powershell
# Run all tests
dotnet test

# Run tests for a specific project
dotnet test service.tests/
dotnet test adapters/claude-code.tests/
dotnet test adapters/copilot.tests/
dotnet test shared.tests/

# Run a single test by name
dotnet test service.tests/ --filter "FullyQualifiedName~SessionMapTests.SomeTestName"

# Build the service
dotnet build service/

# Flash firmware (from firmware/ directory, requires PlatformIO)
pio run --target upload
```

The service requires .NET 10. Firmware uses PlatformIO with the `xiao_esp32c3` environment (FastLED + ArduinoJson).

---

## Architecture

The system is three separate processes plus firmware:

1. **Adapters** (`adapters/claude-code/`, `adapters/copilot/`) — fire-and-forget executables spawned once per hook event. They map hook event names to `SessionEvent` and POST to the service, then exit. Never block on the service being slow.

2. **Service** (`service/`) — long-lived ASP.NET background service. Owns the session state machine, USB writer, and HTTP server on `localhost:57491`. Key types:
   - `SessionMap` — thread-safe `ConcurrentDictionary` of sessions; computes `RingState` priority (Blocked > Working/WorkingStale > Idle > NoSessions) and handles `WorkingStale` timeout and `BlockedDebounce` suppression.
   - `SessionService` — hosted service wrapping `SessionMap`; handles `POST /event` and runs the periodic stale-eviction timer.
   - `DeviceConnectionManager` — hosted service that polls for the USB device (VID=0x303A PID=0x1001) and manages the serial port lifecycle.
   - `DebounceWriter` — wraps `SerialDeviceWriter`; suppresses duplicate consecutive frames so the LED doesn't flash needlessly.
   - `GlowpilotConfig` — loaded from `%APPDATA%\Glowpilot\config.json`; all timeouts, `NoSessionsColor` and `LogLevel` live here.
   - `FileLoggerProvider` — rolling plain-text log (`%LOCALAPPDATA%\Glowpilot\logs\service.log`). The service is a `WinExe` with no console, so this is where diagnostics go. Never throws. Test hosts must not attach it (`ServiceFactory` clears providers).

3. **Shared** (`shared/`) — `RingState`, `SessionEvent`, `WireFrame`. Used by both service and adapters.

4. **Firmware** (`firmware/`) — PlatformIO / C++ on Seeed XIAO ESP32-C3. Receives newline-delimited JSON frames over USB CDC serial, drives 7× WS2812B LEDs with cross-fade animations.

### Wire protocol

The service serializes `WireFrame` as `{"state":"working","tool":"claude"}\n` and writes it to the serial port. The firmware parses it and selects the matching animation.

### State priority and debounce

`SessionMap.ComputeRingState()` is the single place where multiple concurrent sessions are resolved into one `RingState`. The `BlockedDebounce` (default 3 s) suppresses a newly-entered Blocked state so silent auto-handled permission checks don't flash red. `WorkingStaleTimeout` (default 30 s) degrades a stale Working session to `WorkingStale` (slow amber pulse).

### Session eviction

`SessionService` runs a timer every 60 s and calls `SessionMap.EvictStale()`. Idle sessions evict after `IdleStaleTimeoutSeconds` (default 300 s); all other sessions after `StaleTimeoutMinutes` × 60 s (default 10 min).
