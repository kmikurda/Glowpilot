# Glowpilot

A physical LED ring that shows what your AI coding assistant is doing — in real time, without looking at the screen.

Plug a 7-LED WS2812B ring into your USB port. The ring glows green when Claude or Copilot is idle, spins amber when it's using tools, pulses red when it's waiting for your permission, and pulses blue when no session is active. One glance and you know whether to wait, act, or walk away.

---

## Hardware

| Component | Part |
|---|---|
| Microcontroller | Seeed XIAO ESP32-C3 |
| LED ring | 7× WS2812B (addressable RGB) |
| Connection | USB-C (USB CDC serial, no driver needed) |
| Power | Bus-powered from USB |

The MCU connects to the host over USB CDC serial and receives newline-delimited JSON frames from the service. No Bluetooth, no Wi-Fi, no cloud — everything is local.

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  AI Tool (Claude Code / Copilot CLI)                    │
│  fires hooks on tool-use, stop, permission request, …   │
└────────────────┬────────────────────────────────────────┘
                 │ stdin JSON  (one tiny process per hook)
                 ▼
┌────────────────────────────┐
│  Adapter                   │  glowpilot-claude-code.exe
│  (Claude Code or Copilot)  │  glowpilot-copilot.exe
│  maps event → SessionEvent │
└────────────────┬───────────┘
                 │ HTTP POST /event
                 ▼
┌────────────────────────────────────────────────────────────┐
│  Service  (Glowpilot.Service.exe)                          │
│                                                            │
│  SessionMap  — tracks all active sessions by session ID    │
│  RingState   — highest-priority state across all sessions  │
│  EvictStale  — cleans up sessions that stopped reporting   │
│  HTTP server — receives events, exposes /status endpoint   │
└────────────────┬───────────────────────────────────────────┘
                 │ newline-delimited JSON  over USB CDC serial
                 ▼
┌────────────────────────────────────┐
│  Firmware  (ESP32-C3)              │
│  parses state frames               │
│  drives 7× WS2812B LED ring        │
│  cross-fades between states        │
└────────────────────────────────────┘
```

### Components

| Directory | What it is |
|---|---|
| `firmware/` | PlatformIO / C++ — LED animations and USB serial wire protocol |
| `service/` | .NET 10 background service — session state machine, HTTP server, USB writer |
| `adapters/claude-code/` | Tiny .exe spawned by Claude Code hooks — maps events and POSTs to the service |
| `adapters/copilot/` | Tiny .exe spawned by Copilot CLI hooks — same role, different event names |
| `shared/` | Shared types used by service and adapters (`RingState`, `SessionEvent`, `WireFrame`) |
| `installer/windows/` | PowerShell installer — publishes binaries, registers Task Scheduler task, writes hook configs |

The service and adapters are deliberately separate processes. The adapter is fire-and-forget (spawned by the AI tool for each hook, exits in under a second) while the service is a long-lived background process managing state, timers, and the USB connection. The AI tool is never blocked by the service being slow or absent.

---

## LED States

| State | Color | Animation | What it means |
|---|---|---|---|
| **Boot** | Blue | Slow pulse | Firmware is running; service not yet connected |
| **No Sessions** | Blue | Slow pulse | Service connected; no AI sessions active |
| **Idle** | Green | Solid | Session active; AI finished its turn, waiting for you |
| **Working** | Amber | Spinning dot | AI is actively using tools or thinking |
| **Working Stale** | Amber | Slow pulse | Working session, but no event received for >30 s — AI is probably paused waiting for user input |
| **Blocked** | Red | Fast pulse | AI is explicitly waiting for your permission |

When multiple sessions are active simultaneously (e.g. Claude Code and Copilot running at the same time), the highest-priority state wins: Blocked > Working/WorkingStale > Idle > No Sessions.

State transitions cross-fade over 400 ms so sudden changes don't flash.

---

## Session Lifecycle — Claude Code

Claude Code hooks fire in `~/.claude/settings.json`. The adapter `glowpilot-claude-code.exe` is invoked for each event.

```
User sends message
        │
        ▼
   [Working — amber spin]         ← PreToolUse, PostToolUse
        │
        │  (tool calls happen repeatedly while Claude works)
        │
        ▼
   [Idle — green solid]           ← Stop (Claude finished its turn)
        │                         ← Notification (Claude notified user)
        │
        │  (5 minutes pass with no new event)
        │
        ▼
   [No Sessions — blue pulse]     ← idle session evicted by stale timer
```

**Hook → state mapping**

| Hook event | LED state |
|---|---|
| `PreToolUse` | Working |
| `PostToolUse` | Working |
| `Notification` | Idle |
| `Stop` | Idle |

`Notification` maps to Idle rather than Working to prevent the LED from bouncing back to amber immediately after `Stop` sets it green — `Notification` fires when Claude has finished and is notifying the user, not when it is actively working.

---

## Session Lifecycle — GitHub Copilot CLI

Copilot CLI uses native file hooks in `~/.copilot/hooks/glowpilot.json`. The adapter `glowpilot-copilot.exe` is invoked for each event. All Copilot events share the fixed session ID `"copilot"` because the native hook system does not pass a session ID on stdin.

```
User sends prompt
        │
        ▼
   [Working — amber spin]         ← sessionStart, userPromptSubmitted
        │
        │  (tool calls)
        │
        ▼
   [Working — amber spin]         ← preToolUse, postToolUse, postToolUseFailure
        │
        │  (Copilot needs a permission)
        │
        ▼
   [Blocked — red fast pulse]     ← permissionRequest
        │
        │  (user approves)
        │
        ▼
   [Working — amber spin]         ← preToolUse resumes
        │
        │  (Copilot finishes)
        │
        ▼
   [Idle — green solid]           ← agentStop or sessionEnd
        │
        │  (5 minutes pass)
        │
        ▼
   [No Sessions — blue pulse]
```

**Hook → state mapping**

| Hook event | LED state |
|---|---|
| `sessionStart` | Working |
| `userPromptSubmitted` | Working |
| `preToolUse` | Working |
| `postToolUse` | Working |
| `postToolUseFailure` | Working |
| `permissionRequest` | Blocked |
| `agentStop` | Idle |
| `sessionEnd` | Idle |

**Working Stale detection** — If the Copilot session is in Working state and no hook fires for more than 30 seconds, the service transitions the ring to Working Stale (slow amber pulse). This happens when Copilot is paused: waiting for a response it cannot signal through a hook, asking a clarifying question, or paused at an internal decision point. The 30-second threshold is a heuristic; if Copilot's own tools take longer than that the ring will incorrectly show the "stale" animation until the next event arrives.

---

## Stale Timeouts

Sessions are automatically evicted from the session map when they stop reporting. The service checks every 60 seconds.

| Session state | Eviction timeout | Rationale |
|---|---|---|
| Idle | 5 minutes | Ring stays green for a few minutes after a session ends so you can see it finished |
| Working / Working Stale | 10 minutes | Tolerate long pauses without spamming the LED |
| Blocked | 10 minutes | No time-based eviction of blocked state — green should come from the agent resuming, not a timer |

Timeouts are configurable in `%APPDATA%\Glowpilot\config.json`:

```json
{
  "StaleTimeoutMinutes": 10,
  "IdleStaleTimeoutSeconds": 300,
  "WorkingStaleTimeoutSeconds": 30,
  "NoSessionsColor": "#0044FF"
}
```

---

## Logging

The service runs without a console window, so it writes a plain-text log file instead:

| Platform | Log file |
|---|---|
| Windows | `%LOCALAPPDATA%\Glowpilot\logs\service.log` |
| Linux / macOS | `~/.local/share/Glowpilot/logs/service.log` |

Follow it live (the file is shared for reading, so this works while the service runs):

```powershell
Get-Content "$env:LOCALAPPDATA\Glowpilot\logs\service.log" -Tail 50 -Wait -Encoding UTF8
```

Each entry is one line — `timestamp [level] source: message` — with exception details on the following lines. At the default `Information` level the log contains:

- service start (with the effective configuration) and shutdown
- every incoming session event (`claude session <id>: Working`) and session closure
- every Ring State change, e.g. `Ring state: Working -> Idle (tool: claude, sessions: 1)` — the line to look for when the LED shows something unexpected
- stale-session evictions
- USB device connect / disconnect and serial-port errors
- a malformed `config.json` (defaults are used), a second instance being refused, and any unhandled exception that kills the service

`Debug` and `Trace` add finer detail (e.g. every USB poll). ASP.NET's per-request logging is kept at `Warning`.

**Rotation:** `service.log` rolls at 2 MB to `service.1.log`, then `service.2.log`, `service.3.log`; the oldest is dropped, so the logs never exceed about 8 MB. Logging can never affect the service — if the log directory is not writable, entries are silently dropped.

**Level** is set in `%APPDATA%\Glowpilot\config.json` (`Trace`, `Debug`, `Information` (default), `Warning`, `Error`, `Critical`, `None`; anything else means `Information`):

```json
{
  "LogLevel": "Debug"
}
```

The config is read at startup, so restart the service afterwards: `Stop-ScheduledTask -TaskName Glowpilot; Start-ScheduledTask -TaskName Glowpilot`.

---

## Known Limitations and Trade-offs

These are the deliberate design decisions and Copilot CLI limitations encountered during development.

### Copilot: permission denial leaves LED red

When the user **denies** a Copilot permission request, `agentStop` does not fire. The Copilot CLI offers no hook to signal "the agent stopped because permission was denied." The LED stays red until the next `userPromptSubmitted` event (user sends a new message) or until the 10-minute stale timeout. There is no time-based eviction of the Blocked state — the assumption is that green should come from the agent resuming work, not from a countdown.

### Copilot: not all permission prompts are detected

The `permissionRequest` hook fires for tool-level permission requests. Other types of permission dialogs — such as "access to folder outside workspace scope" or other confirmation prompts — use a different mechanism in Copilot CLI and do not trigger `permissionRequest`. Those prompts leave the LED in whatever state it was before (usually amber).

### Copilot: agent questions cannot be detected

When Copilot asks the user a clarifying question mid-task (as opposed to requesting a tool permission), no hook fires. The LED stays amber. There is no `agentQuestion` or equivalent event in the Copilot CLI hook system.

### Working Stale threshold is a heuristic

The 30-second threshold for transitioning from Working to Working Stale assumes that active tool usage produces events more frequently than once per 30 seconds. If a single tool call takes longer than 30 seconds (a slow file search, a long compilation, a network call), the ring will show the "waiting" animation even though Copilot is still actively working. The next tool event immediately returns it to amber spin.

### Single session ID for Copilot

Copilot CLI native hooks do not pass a session ID on stdin. All Copilot events are tracked under the fixed ID `"copilot"`. Running two simultaneous Copilot CLI sessions on the same machine will merge their state. In practice this rarely matters since CLI sessions are single-process.

### Copilot hooks are loaded at session start

Adding or changing entries in `~/.copilot/hooks/glowpilot.json` only takes effect after restarting Copilot CLI. The hook file is read once when the CLI session begins.

### Notification event maps to Idle

The Claude Code `Notification` event fires when Claude wants to alert the user that something is ready — conceptually "done." It is mapped to Idle (green) rather than Working (amber). This prevents the ring from bouncing back to amber after `Stop` already turned it green. The trade-off: if a Notification fires during an active multi-tool session (before `Stop`), it will briefly show green rather than amber. In practice this is rare and the next tool event immediately corrects it.

---

## Service Status Endpoint

The service exposes `GET http://localhost:57491/status` for diagnostics:

```json
{
  "sessions": [
    {
      "sessionId": "abc123",
      "tool": "claude",
      "state": "working",
      "lastSeen": "2026-09-14T07:31:34Z"
    }
  ],
  "ringState": "working",
  "deviceConnected": true
}
```

---

## Installation (Windows)

```powershell
PowerShell -ExecutionPolicy Bypass -File installer\windows\install.ps1
```

The installer:
1. Verifies .NET 8+ SDK is present
2. Publishes self-contained win-x64 binaries to `%LOCALAPPDATA%\Glowpilot\bin\`
3. Registers a Task Scheduler task (runs at logon, on wake from sleep, and self-heals every minute)
4. Merges Claude Code hook entries into `%USERPROFILE%\.claude\settings.json`
5. Writes Copilot CLI hooks to `%USERPROFILE%\.copilot\hooks\glowpilot.json`
6. Starts the task and smoke-tests `GET /status`

The service runs at user privilege level — no administrator rights are required.

The service is built as a Windows GUI-subsystem executable (`<OutputType>WinExe</OutputType>`), so it runs fully in the background with no console window. Because the task self-heals every minute, killing the process (e.g. in Task Manager) only restarts it within a minute. To stop it for real:

```powershell
Disable-ScheduledTask -TaskName Glowpilot; Stop-ScheduledTask -TaskName Glowpilot
```

Without a console, diagnostics go to a log file (see [Logging](#logging)); `GET http://localhost:57491/status` shows the current sessions and Ring State. After changing the service code, re-publish it (stop the task first — a running service locks its own binaries).

## Firmware

See [`firmware/README.md`](firmware/README.md) for flashing instructions.

Flash with PlatformIO from the `firmware/` directory:

```bash
pio run --target upload
```
