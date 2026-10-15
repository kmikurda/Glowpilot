# Glowpilot Windows Installer

Installs Glowpilot as a user-level background service on Windows.
**No administrator rights required.**

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10/11 | x64 |
| .NET 8 SDK or later | [Download](https://dot.net/download) — used to publish the binaries |
| Claude Code | Must be installed for the hook integration to work |
| PowerShell 5.1+ | Ships with Windows; or use PowerShell 7 |

The installer does **not** need to be run as Administrator. All operations are
user-scoped (Task Scheduler `RunLevel Limited`, paths under `%LOCALAPPDATA%` and
`%APPDATA%`).

## How to run

Open a PowerShell prompt in the **repository root** (or anywhere — the script
resolves its own location) and run:

```powershell
PowerShell -ExecutionPolicy Bypass -File installer\windows\install.ps1
```

### What the installer does

1. **Publishes binaries** — `dotnet publish --self-contained -r win-x64 -c Release` for
   the service and both adapters. Output goes to `%LOCALAPPDATA%\Glowpilot\bin\`.

2. **Registers a scheduled task** — "Glowpilot" runs at every log-on with up to
   3 automatic restarts (1-minute interval). No execution time limit.

3. **Merges Claude Code hooks** — Adds hook entries to
   `%APPDATA%\Claude\settings.json` for `PreToolUse`, `PostToolUse`,
   `Notification`, and `Stop` events. Existing keys are preserved; duplicate
   entries are not added if the installer is run again.

4. **Starts the service** — Launches the scheduled task immediately.

5. **Smoke-tests** — Polls `GET http://localhost:57491/status` for up to 10 s
   and reports success or failure with actionable diagnostics.

### Re-running (idempotent)

The installer is safe to run multiple times:
- Binaries are re-published (always fine).
- The task is re-registered with `-Force` (no duplicate).
- Hook entries are not duplicated in `settings.json`.

## Running the tests

Tests require **Pester 5**:

```powershell
Install-Module Pester -MinimumVersion 5.0 -Scope CurrentUser -Force
```

Then from the `installer\windows\` directory:

```powershell
Invoke-Pester -Path install.Tests.ps1 -Output Detailed
```

## Uninstall

```powershell
# Remove the scheduled task
Unregister-ScheduledTask -TaskName 'Glowpilot' -Confirm:$false

# Remove binaries
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Glowpilot"

# Remove Claude hook entries manually from %APPDATA%\Claude\settings.json
```
