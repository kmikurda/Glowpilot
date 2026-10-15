# Technical Concept: Multi-Agent Session Status Light

## 1. Purpose

A physical, plug-and-play status indicator that shows the real-time state of
one or more concurrent AI coding agent sessions (Claude Code, GitHub Copilot
CLI, and potentially others in the future) running on a developer's machine.

The device sits next to the monitor and communicates over USB. Once the
one-time software installation is done, the intended end-user experience is:
plug the USB cable in, the light starts working; unplug it, everything
degrades gracefully with no crashes or errors surfacing to the user.

## 2. Problem Statement

When running multiple terminal-based AI coding agents in parallel, it is
easy to lose track of which session is idle, which is actively working, and
— most importantly — which one is blocked waiting for user confirmation
(the state that actually requires the developer's attention). Switching
between terminal windows to check status is disruptive to flow. A glanceable
physical indicator solves this without requiring context switches.

## 3. Goals

- Zero required manual configuration after initial setup (no IP addresses,
  no Wi-Fi credentials, no manual port selection).
- Works identically across Windows, macOS, and Linux.
- Supports an arbitrary number of concurrent sessions (initial hardware
  target: 7 addressable LEDs, i.e. up to 7 sessions shown individually;
  degrade gracefully beyond that — see open questions).
- Distinguishes at minimum 3 states: blocked/waiting-for-confirmation,
  actively-working, idle/finished.
- Distinguishes which tool produced a given state (Claude Code vs Copilot
  CLI vs future tools) via a secondary visual cue (hue/pattern), without
  requiring a separate physical indicator per tool.
- Recovers automatically from: USB unplug/replug, background service
  crash/restart, device power loss, stale/orphaned sessions (e.g. terminal
  killed without clean shutdown).
- Background service starts automatically on user login and requires no
  manual start command in normal operation.

## 4. Non-Goals

- Not a general-purpose IoT device; single-purpose firmware only.
- Not required to work without a companion background process running on
  the host machine (fully firmware-autonomous operation is out of scope).
- Not required to support cloud/remote monitoring — local machine only.
- Not required to persist history/analytics of past sessions.

## 5. High-Level Architecture

```
+------------------+     +------------------+     +------------------+
|  Claude Code     |     |  Copilot CLI     |     |  (future agent)  |
|  hook events     |     |  hook events     |     |  hook events     |
+--------+---------+     +--------+---------+     +--------+---------+
         |                        |                        |
         v                        v                        v
              +----------------------------------+
              |     Hook adapter script(s)        |
              |  (per-tool, normalizes event      |
              |   payload to common schema)       |
              +-----------------+------------------+
                                |  HTTP POST (localhost)
                                v
              +----------------------------------+
              |      Local Background Service     |
              |  - session state aggregation       |
              |  - stale-session eviction          |
              |  - USB device discovery/reconnect  |
              |  - serial protocol encoder          |
              +-----------------+------------------+
                                |  USB serial
                                v
              +----------------------------------+
              |         Device Firmware           |
              |  - serial protocol decoder         |
              |  - LED ring rendering              |
              +----------------------------------+
```

## 6. Components

### 6.1 Hardware
- Microcontroller: Seeed XIAO ESP32-C3 (or equivalent small USB-native MCU).
- Display: 7x addressable RGB LED ring (WS2812B "Jewel"-style module).
- Optional secondary display: I2C character LCD (16x2) for textual detail
  (session count, tool name) — stretch goal, not required for v1.
- Connectivity: USB only for v1 (no Wi-Fi dependency). Device must appear to
  the host OS as a standard USB CDC serial device.
- Power: bus-powered via USB, no external power supply required.

### 6.2 Firmware
- Responsibilities: read a compact serial protocol from USB, render the
  corresponding state onto the LED ring, do nothing else.
- Must not require any user-facing configuration (no Wi-Fi setup, no
  pairing step). Flashed once during initial setup.
- Must handle malformed/partial serial input defensively (never hang or
  require a manual reset).
- Should emit a distinct "ready"/idle animation on boot before the host
  service connects, so the user can visually confirm the device works
  standalone before wiring up software.

### 6.3 Hook Adapters (per tool)
- One thin script per supported tool (Claude Code, Copilot CLI) that:
  - Is registered via that tool's native hook/event mechanism.
  - Reads the tool-native event payload (JSON on stdin).
  - Extracts a session identifier and maps the tool-native event to one of
    the common states (`blocked`, `working`, `idle`, `closed`).
  - Forwards a normalized event to the Background Service over localhost
    HTTP.
- Must be idempotent and fail silently (never break the parent tool's
  execution if the Background Service is unreachable).

### 6.4 Background Service
- A long-running local process, started automatically at user login
  (mechanism is OS-specific: Task Scheduler on Windows, launchd on macOS,
  systemd user service on Linux — to be defined precisely in the resulting
  contract).
- Responsibilities:
  - Expose a localhost-only HTTP endpoint that accepts normalized session
    events from hook adapters.
  - Maintain an in-memory map of `session_id -> {tool, state, last_seen}`.
  - Evict sessions that haven't reported in beyond a configurable timeout
    (default suggestion: 10 minutes), to avoid "zombie" sessions holding a
    slot forever.
  - Continuously discover the target USB device by stable hardware
    identifiers (USB VID/PID, not OS-assigned port name/number), and
    reconnect automatically after disconnect/replug without manual
    intervention or restart.
  - Translate the aggregated session map into the wire protocol and push
    updates to the device whenever the map changes, and periodically
    (heartbeat) even without changes, to self-heal after any missed update.
  - Never crash on device absence; must run indefinitely in a degraded
    "no device connected" mode and resume automatically once the device
    reappears.
- Should expose a simple local read-only status endpoint for debugging
  (e.g. current session map as JSON) — not required to be secured beyond
  binding to localhost only.

### 6.5 Installer
- One-time setup step, ideally a single command/script per OS, that:
  - Installs the Background Service's runtime dependencies.
  - Registers the Background Service as an auto-start unit for the current
    user (not system-wide/admin, to avoid elevation prompts if avoidable).
  - Merges (not overwrites) hook configuration into each supported tool's
    existing configuration file(s).
  - Verifies the device is reachable (optional smoke test) and reports
    clear success/failure to the user.
- Must be safely re-runnable (idempotent) if the user reinstalls or updates.

## 7. Communication Protocol (to be finalized in the contract)

### 7.1 Hook Adapter -> Background Service
- Transport: HTTP over localhost, JSON body.
- Minimum required fields per event: `session_id`, `tool`, `state`,
  `closed` (boolean, optional, default false).
- `state` enum: `blocked` | `working` | `idle`.
- `tool` enum: extensible string, initial values `claude`, `copilot`.

### 7.2 Background Service -> Device
- Transport: USB serial (baud rate to be fixed, e.g. 115200), plain-text or
  compact binary framed protocol — decision to be made in the contract,
  with a preference for simplicity and human-debuggability (e.g. newline-
  terminated JSON or CSV frame) over binary efficiency, since payload size
  is tiny (max 7 slots).
- Payload: ordered list of up to N slot states, each slot carrying at
  least: color/state and tool-of-origin (for secondary visual
  differentiation).
- Protocol must be resynchronizable after partial/corrupted frames (e.g.
  framing delimiter + checksum, or newline-delimited JSON with try/catch
  and drop-on-error on the firmware side).

### 7.3 Device discovery
- Background Service must identify the device by USB Vendor ID / Product
  ID, not by OS-assigned port path, since the same physical device can
  enumerate under different port names across replugs/reboots/platforms.

## 8. Session-to-Slot Mapping

- Default policy: first-reported session occupies the lowest available
  slot index; slots are freed on eviction or explicit `closed` events.
- Open question (see Section 10): behavior beyond 7 concurrent sessions.

## 9. Reliability & Failure Modes to Handle Explicitly

- Device unplugged mid-operation: Background Service must detect this
  without crashing and resume pushing updates automatically on replug.
- Background Service crashes/restarts: on restart, session state map
  starts empty; this is acceptable (state will repopulate as hooks fire
  again) and must not require any manual device or tool restart.
- Malformed hook payload: adapter must not propagate an exception to the
  parent tool process; log and drop.
- Multiple Background Service instances accidentally running: must not
  corrupt device state (e.g. bind failure on the shared local port should
  be handled gracefully, second instance exits cleanly).
- Clock/ordering: last-write-wins per `session_id`; no requirement for
  strict global ordering across tools.

## 10. Open Questions for the Contract to Resolve

1. Exact wire protocol format between Background Service and device
   (framing, encoding, checksum strategy).
2. Exact behavior when active sessions exceed available LED slots
   (e.g. round-robin cycling, aggregate/priority mode, or reject silently).
3. Whether tool identity should be encoded as hue-shift, brightness
   pattern, or blink pattern per slot.
4. Auto-start mechanism specifics per OS and whether admin/elevated rights
   can be avoided entirely.
5. Whether the optional I2C LCD is in scope for v1 or deferred.
6. Update frequency / rate limiting to avoid flooding the serial link if a
   tool fires very frequent tool-use events.
7. Versioning strategy for the wire protocol so firmware and service can
   evolve independently without silent incompatibility.

## 11. Deliverables Expected from the Resulting Contract

- Firmware source (C++/Arduino) implementing Section 6.2 and 7.2/7.3.
- Background Service source (language open, Python assumed unless
  contract states otherwise) implementing Section 6.4, 7.1, 7.3, 9.
- Hook adapter scripts for Claude Code and Copilot CLI implementing
  Section 6.3.
- Installer script(s) implementing Section 6.5 for at least one OS, with
  a clear extension path for the others.
- A short verification/test plan covering the failure modes in Section 9.
