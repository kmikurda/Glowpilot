#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Glowpilot as a user-level background service on Windows.
    No administrator rights required.
.DESCRIPTION
    Steps performed:
      1. Verify .NET 8+ SDK is present
      2. Publish self-contained win-x64 binaries to %LOCALAPPDATA%\Glowpilot\bin\
      3. Register a user-level Task Scheduler task (AtLogOn + wake-from-sleep trigger + 1-min repetition, RunLevel Limited)
      4. Merge Claude Code hook entries into %USERPROFILE%\.claude\settings.json
      5. Install Copilot CLI user-level hooks into %USERPROFILE%\.copilot\hooks\glowpilot.json
      6. Start the scheduled task
      7. Smoke-test GET http://localhost:57491/status (timeout: 10 s)

    The scheduled task combines an AtLogOn trigger, a wake-from-sleep event trigger, and a
    1-minute repetition interval.  This means the service is always available: it starts at
    logon and after every sleep/hibernate resume, and self-heals within one minute if it ever
    stops unexpectedly.  The service exits immediately when the port is already bound, so
    duplicate instances are never a problem.
.EXAMPLE
    PowerShell -ExecutionPolicy Bypass -File install.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ═══════════════════════════════════════════════════════════════════════════════
# Exported helper functions — tested via Pester (install.Tests.ps1)
# ═══════════════════════════════════════════════════════════════════════════════

function Test-DotNetVersion {
    <#
    .SYNOPSIS
        Returns $true when dotnet is installed and its major version is >= MinMajorVersion.
    .PARAMETER MinMajorVersion
        Minimum required major version (default: 8).
    .PARAMETER GetVersionCommand
        Scriptblock that returns the raw output of dotnet --version.
        Injectable so unit tests can supply a fake value without spawning a process.
    #>
    param(
        [int]$MinMajorVersion = 8,
        [scriptblock]$GetVersionCommand = { & dotnet --version 2>&1 }
    )

    $versionOutput = & $GetVersionCommand
    if (-not $versionOutput) { return $false }

    # Output examples: "8.0.401", "10.0.100-preview.1.25163.7"
    # Split on '.' or '-' and take the first segment as the major version number.
    $versionStr = ($versionOutput -as [string]).Trim()
    $majorStr   = ($versionStr -split '[\.\-]')[0]

    $major = 0
    if (-not [int]::TryParse($majorStr, [ref]$major)) { return $false }

    return $major -ge $MinMajorVersion
}

function Merge-ClaudeHooks {
    <#
    .SYNOPSIS
        Merges Glowpilot hook entries into an existing Claude settings JSON string.
    .PARAMETER ConfigJson
        Current contents of Claude\settings.json (may be empty or null).
    .PARAMETER AdapterExePath
        Absolute path to glowpilot-claude-code.exe.
    .OUTPUTS
        [string] — the resulting JSON, preserving every pre-existing key.
    .NOTES
        Idempotency: an entry is only appended when no hook in that event-type
        array already lists the same exe path (regardless of which matcher entry
        it appears under). Safe to run multiple times.
    #>
    param(
        [string]$ConfigJson,
        [string]$AdapterExePath
    )

    # ── 1. Parse or bootstrap ────────────────────────────────────────────────
    if ([string]::IsNullOrWhiteSpace($ConfigJson)) {
        $config = [PSCustomObject]@{}
    } else {
        $config = $ConfigJson | ConvertFrom-Json
    }

    # ── 2. Ensure the top-level "hooks" property exists ──────────────────────
    if ($null -eq $config.PSObject.Properties['hooks']) {
        $config | Add-Member -NotePropertyName 'hooks' -NotePropertyValue ([PSCustomObject]@{})
    }

    # ── 3. Build the hook entry to inject ─────────────────────────────────
    # Claude Code on Windows runs hooks via MSYS2/Git Bash. The path must use
    # the MSYS2 format: drive letter lowercased and prefixed with '/', backslashes
    # replaced with forward slashes. e.g. C:\foo\bar.exe → /c/foo/bar.exe
    $driveLetter = $AdapterExePath[0].ToString().ToLower()
    $rest        = $AdapterExePath.Substring(3).Replace('\', '/')
    $adapterExeMsys = "/$driveLetter/$rest"

    $newEntry = [PSCustomObject]@{
        matcher = ''
        hooks   = @(
            [PSCustomObject]@{
                type    = 'command'
                command = $adapterExeMsys
            }
        )
    }

    # ── 4. Merge into each event type ────────────────────────────────────────
    $eventTypes = @('PreToolUse', 'PostToolUse', 'Notification', 'Stop')

    foreach ($eventType in $eventTypes) {
        $prop = $config.hooks.PSObject.Properties[$eventType]

        if ($null -eq $prop) {
            # Property does not exist yet — add a new array containing our entry
            $config.hooks | Add-Member -NotePropertyName $eventType -NotePropertyValue @($newEntry)
            continue
        }

        # Property exists — check whether the exe is already referenced anywhere
        # in this event's array (walk every entry → every nested hook).
        $existingArray    = @($prop.Value)
        $alreadyRegistered = $false

        foreach ($entry in $existingArray) {
            if ($null -eq $entry.hooks) { continue }
            foreach ($hook in @($entry.hooks)) {
                if ($hook.command -eq $adapterExeMsys) {
                    $alreadyRegistered = $true
                    break
                }
            }
            if ($alreadyRegistered) { break }
        }

        if (-not $alreadyRegistered) {
            # Append our entry; re-assign the property so the object reflects it
            $config.hooks.$eventType = $existingArray + $newEntry
        }
    }

    return $config | ConvertTo-Json -Depth 10
}

function Install-CopilotHooks {
    <#
    .SYNOPSIS
        Writes Glowpilot hook definitions to the user's Copilot CLI hooks directory.
    .PARAMETER AdapterExePath
        Absolute path to glowpilot-copilot.exe (the Copilot adapter binary).
    .NOTES
        Uses the native Copilot CLI file-hooks system (~/.copilot/hooks/) which
        does not require any feature flags.  Each hook pipes a fixed JSON payload
        to the adapter, using "copilot" as the session ID (native hook stdin does
        not carry a session ID).  Idempotent: overwrites on each run.
    #>
    param(
        [string]$AdapterExePath
    )

    $hooksDir = Join-Path $env:USERPROFILE '.copilot\hooks'

    if (-not (Test-Path $hooksDir)) {
        New-Item -ItemType Directory -Path $hooksDir -Force | Out-Null
    }

    # Double backslashes for JSON string escaping; the path appears inside a
    # PowerShell single-quoted string, so backslashes need no further escaping
    # at the PS level — only JSON escaping is required here.
    $jsonPath = $AdapterExePath.Replace('\', '\\')

    $hookFile = Join-Path $hooksDir 'glowpilot.json'

    $content = @"
{
  "version": 1,
  "hooks": {
    "sessionStart": [
      { "type": "command", "powershell": "'{ \"session_id\": \"copilot\", \"event\": \"agent:thinking\" }' | & '$jsonPath'", "timeoutSec": 5 }
    ],
    "userPromptSubmitted": [
      { "type": "command", "powershell": "'{ \"session_id\": \"copilot\", \"event\": \"agent:thinking\" }' | & '$jsonPath'", "timeoutSec": 5 }
    ],
    "preToolUse": [
      { "type": "command", "powershell": "'{ \"session_id\": \"copilot\", \"event\": \"agent:tool_call\" }' | & '$jsonPath'", "timeoutSec": 5 }
    ],
    "postToolUse": [
      { "type": "command", "powershell": "'{ \"session_id\": \"copilot\", \"event\": \"agent:tool_result\" }' | & '$jsonPath'", "timeoutSec": 5 }
    ],
    "postToolUseFailure": [
      { "type": "command", "powershell": "'{ \"session_id\": \"copilot\", \"event\": \"agent:tool_result\" }' | & '$jsonPath'", "timeoutSec": 5 }
    ],
    "permissionRequest": [
      { "type": "command", "powershell": "'{ \"session_id\": \"copilot\", \"event\": \"agent:waiting_for_input\" }' | & '$jsonPath'", "timeoutSec": 5 }
    ],
    "agentStop": [
      { "type": "command", "powershell": "'{ \"session_id\": \"copilot\", \"event\": \"agent:done\" }' | & '$jsonPath'", "timeoutSec": 5 }
    ],
    "sessionEnd": [
      { "type": "command", "powershell": "'{ \"session_id\": \"copilot\", \"event\": \"agent:done\" }' | & '$jsonPath'", "timeoutSec": 5 }
    ]
  }
}
"@

    Set-Content -Path $hookFile -Value $content -Encoding UTF8NoBOM
}

function Register-GlowpilotTask {
    <#
    .SYNOPSIS
        Registers (or replaces) the "Glowpilot" Task Scheduler task.
        Runs at log-on and on every wake-from-sleep for the current user; no UAC required (RunLevel Limited).
        A 1-minute repetition keeps the service alive throughout the session —
        if it ever stops (screen unlock, unexpected exit), it restarts within a minute.
    .PARAMETER ServiceExePath
        Absolute path to Glowpilot.Service.exe.
    #>
    param(
        [string]$ServiceExePath
    )

    # PowerShell's CIM-based trigger objects do not expose Repetition sub-properties
    # as settable. We use XML registration instead — it gives full control over the
    # trigger's <Repetition> block and the MultipleInstancesPolicy setting.
    $userId = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value

    # ISO 8601 durations: PT1M = 1 minute, PT0S = no execution time limit.
    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Glowpilot session tracker — starts at logon, self-heals every minute.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$userId</UserId>
      <Repetition>
        <Interval>PT1M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </LogonTrigger>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="System"&gt;&lt;Select Path="System"&gt;*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=107]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
    </EventTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$userId</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$ServiceExePath</Command>
    </Exec>
  </Actions>
</Task>
"@

    Register-ScheduledTask -TaskName 'Glowpilot' -Xml $xml -Force
}

function Test-ServiceHealth {
    <#
    .SYNOPSIS
        Polls GET /status until HTTP 200 is returned or the timeout expires.
    .PARAMETER Url
        Endpoint to poll (default: http://localhost:57491/status).
    .PARAMETER TimeoutSeconds
        How long to wait in total (default: 10 s).
    .OUTPUTS
        [bool] — $true on success, $false on timeout.
    #>
    param(
        [string]$Url = 'http://localhost:57491/status',
        [int]$TimeoutSeconds = 10
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return $true }
        } catch {
            # Service not yet ready — keep polling
        }
        Start-Sleep -Milliseconds 500
    }

    return $false
}

# ═══════════════════════════════════════════════════════════════════════════════
# Main installation logic
# ═══════════════════════════════════════════════════════════════════════════════

function Invoke-GlowpilotInstall {
    param(
        [string]$RepoRoot
    )

    $binRoot    = Join-Path $env:LOCALAPPDATA 'Glowpilot\bin'
    $claudeConf = Join-Path $env:USERPROFILE   '.claude\settings.json'

    Write-Host ''
    Write-Host '=== Glowpilot Installer for Windows ===' -ForegroundColor Cyan
    Write-Host ''

    # ── Step 0: prerequisite — .NET SDK ─────────────────────────────────────
    Write-Host '[0/6] Checking prerequisites...'
    if (-not (Test-DotNetVersion)) {
        Write-Host '[FAIL] .NET 8 SDK or later is required.' -ForegroundColor Red
        Write-Host '       Download: https://dot.net/download' -ForegroundColor Yellow
        exit 1
    }
    Write-Host '[OK]  .NET SDK >= 8 found.' -ForegroundColor Green

    # ── Step 1: publish self-contained binaries ──────────────────────────────
    Write-Host ''
    Write-Host '[1/6] Publishing binaries (self-contained, win-x64, Release)...'

    $projects = @(
        @{
            Csproj = 'service\Glowpilot.Service.csproj'
            OutDir = "$binRoot\service"
        }
        @{
            Csproj = 'adapters\claude-code\Glowpilot.Adapters.ClaudeCode.csproj'
            OutDir = "$binRoot\claude-code"
        }
        @{
            Csproj = 'adapters\copilot\Glowpilot.Adapters.CopilotCli.csproj'
            OutDir = "$binRoot\copilot"
        }
    )

    foreach ($proj in $projects) {
        $csprojFull = Join-Path $RepoRoot $proj.Csproj
        Write-Host "      dotnet publish $($proj.Csproj)"
        & dotnet publish $csprojFull --self-contained -r win-x64 -c Release -o $proj.OutDir
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[FAIL] Publish failed for $($proj.Csproj)." -ForegroundColor Red
            Write-Host '       Ensure the .NET 8 SDK is installed and the repo is complete.' -ForegroundColor Yellow
            exit 1
        }
    }
    Write-Host '[OK]  All three binaries published.' -ForegroundColor Green

    # ── Step 2: Task Scheduler registration ─────────────────────────────────
    Write-Host ''
    Write-Host '[2/6] Registering Task Scheduler task...'
    $serviceExe = Join-Path $binRoot 'service\Glowpilot.Service.exe'
    try {
        Register-GlowpilotTask -ServiceExePath $serviceExe
        Write-Host '[OK]  Scheduled task "Glowpilot" registered (runs at log-on, no UAC).' -ForegroundColor Green
    } catch {
        Write-Host "[FAIL] Could not register scheduled task: $_" -ForegroundColor Red
        exit 1
    }

    # ── Step 3: Claude Code hook configuration ───────────────────────────────
    Write-Host ''
    Write-Host '[3/6] Merging Claude Code hook configuration...'

    $adapterExe   = Join-Path $binRoot 'claude-code\glowpilot-claude-code.exe'
    $existingJson = ''

    if (Test-Path $claudeConf) {
        $existingJson = Get-Content -Path $claudeConf -Raw -ErrorAction SilentlyContinue
    } else {
        # Create the Claude config directory if it does not exist yet
        $claudeDir = Split-Path $claudeConf
        if (-not (Test-Path $claudeDir)) {
            New-Item -ItemType Directory -Path $claudeDir -Force | Out-Null
        }
    }

    $mergedJson = Merge-ClaudeHooks -ConfigJson $existingJson -AdapterExePath $adapterExe
    Set-Content -Path $claudeConf -Value $mergedJson -Encoding UTF8
    Write-Host "[OK]  Hooks written to $claudeConf" -ForegroundColor Green

    # ── Step 4: Copilot CLI user-level hooks ─────────────────────────────────
    Write-Host ''
    Write-Host '[4/6] Installing Copilot CLI user-level hooks...'
    $copilotAdapter  = Join-Path $binRoot 'copilot\glowpilot-copilot.exe'
    $copilotHookFile = Join-Path $env:USERPROFILE '.copilot\hooks\glowpilot.json'
    Install-CopilotHooks -AdapterExePath $copilotAdapter
    Write-Host "[OK]  Hooks written to $copilotHookFile" -ForegroundColor Green

    # ── Step 5: start the service ────────────────────────────────────────────
    Write-Host ''
    Write-Host '[5/6] Starting Glowpilot service...'
    try {
        Start-ScheduledTask -TaskName 'Glowpilot'
        Write-Host '[OK]  Task started.' -ForegroundColor Green
    } catch {
        Write-Host "[FAIL] Could not start scheduled task: $_" -ForegroundColor Red
        Write-Host '       Try manually: Start-ScheduledTask -TaskName Glowpilot' -ForegroundColor Yellow
        exit 1
    }

    # ── Step 6: smoke test ───────────────────────────────────────────────────
    # A smoke-test timeout is not a critical failure: the service may still be
    # starting (AV scan, slow disk). Installation itself succeeded. Report only.
    Write-Host ''
    Write-Host '[6/6] Smoke-testing service (GET /status, timeout 10 s)...'
    if (Test-ServiceHealth) {
        Write-Host '[OK]  Service responded HTTP 200 at http://localhost:57491/status' -ForegroundColor Green
    } else {
        Write-Host '[FAIL] Service did not respond within 10 seconds.' -ForegroundColor Red
        Write-Host '       Installation succeeded but the service may still be starting.' -ForegroundColor Yellow
        Write-Host '       Diagnostics:' -ForegroundColor Yellow
        Write-Host '         Get-ScheduledTaskInfo -TaskName Glowpilot' -ForegroundColor Yellow
        Write-Host '         Get-EventLog -LogName Application -Source Glowpilot -Newest 10' -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host '=== Installation complete! ===' -ForegroundColor Cyan
    Write-Host "    Service binary   : $serviceExe"
    Write-Host "    Claude adapter   : $adapterExe"
    Write-Host "    Claude config    : $claudeConf"
    Write-Host "    Copilot adapter  : $copilotAdapter"
    Write-Host "    Copilot hook file: $copilotHookFile"
    Write-Host ''
}

# ── Guard: run main logic only when invoked directly, not dot-sourced ────────
# When Pester dot-sources this file to load functions, $MyInvocation.InvocationName
# is '.', so the main block is skipped.
if ($MyInvocation.InvocationName -ne '.') {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
    Invoke-GlowpilotInstall -RepoRoot $repoRoot
}
