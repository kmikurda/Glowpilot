#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Glowpilot as a user-level background service on Windows.
    No administrator rights required.
.DESCRIPTION
    Steps performed:
      1. Verify .NET 8+ SDK is present
      2. Publish self-contained win-x64 binaries to %LOCALAPPDATA%\Glowpilot\bin\
      3. Register a user-level Task Scheduler task (AtLogOn, RunLevel Limited)
      4. Merge Claude Code hook entries into %APPDATA%\Claude\settings.json
      5. Start the scheduled task
      6. Smoke-test GET http://localhost:57491/status (timeout: 10 s)
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
    $newEntry = [PSCustomObject]@{
        matcher = ''
        hooks   = @(
            [PSCustomObject]@{
                type    = 'command'
                command = $AdapterExePath
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
                if ($hook.command -eq $AdapterExePath) {
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

function Register-GlowpilotTask {
    <#
    .SYNOPSIS
        Registers (or replaces) the "Glowpilot" Task Scheduler task.
        Runs at log-on for the current user; no UAC required (RunLevel Limited).
    .PARAMETER ServiceExePath
        Absolute path to Glowpilot.Service.exe.
    #>
    param(
        [string]$ServiceExePath
    )

    $action   = New-ScheduledTaskAction -Execute $ServiceExePath

    $trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

    # Restart up to 3 times with a 1-minute interval; no execution time limit.
    $settings = New-ScheduledTaskSettingsSet `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero)

    Register-ScheduledTask `
        -TaskName 'Glowpilot' `
        -Action   $action `
        -Trigger  $trigger `
        -Settings $settings `
        -RunLevel Limited `
        -Force
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
    $claudeConf = Join-Path $env:APPDATA      'Claude\settings.json'

    Write-Host ''
    Write-Host '=== Glowpilot Installer for Windows ===' -ForegroundColor Cyan
    Write-Host ''

    # ── Step 0: prerequisite — .NET SDK ─────────────────────────────────────
    Write-Host '[0/5] Checking prerequisites...'
    if (-not (Test-DotNetVersion)) {
        Write-Host '[FAIL] .NET 8 SDK or later is required.' -ForegroundColor Red
        Write-Host '       Download: https://dot.net/download' -ForegroundColor Yellow
        exit 1
    }
    Write-Host '[OK]  .NET SDK >= 8 found.' -ForegroundColor Green

    # ── Step 1: publish self-contained binaries ──────────────────────────────
    Write-Host ''
    Write-Host '[1/5] Publishing binaries (self-contained, win-x64, Release)...'

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
    Write-Host '[2/5] Registering Task Scheduler task...'
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
    Write-Host '[3/5] Merging Claude Code hook configuration...'

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

    # ── Step 4: start the service ────────────────────────────────────────────
    Write-Host ''
    Write-Host '[4/5] Starting Glowpilot service...'
    try {
        Start-ScheduledTask -TaskName 'Glowpilot'
        Write-Host '[OK]  Task started.' -ForegroundColor Green
    } catch {
        Write-Host "[FAIL] Could not start scheduled task: $_" -ForegroundColor Red
        Write-Host '       Try manually: Start-ScheduledTask -TaskName Glowpilot' -ForegroundColor Yellow
        exit 1
    }

    # ── Step 5: smoke test ───────────────────────────────────────────────────
    # A smoke-test timeout is not a critical failure: the service may still be
    # starting (AV scan, slow disk). Installation itself succeeded. Report only.
    Write-Host ''
    Write-Host '[5/5] Smoke-testing service (GET /status, timeout 10 s)...'
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
    Write-Host "    Service binary : $serviceExe"
    Write-Host "    Claude adapter : $adapterExe"
    Write-Host "    Config file    : $claudeConf"
    Write-Host ''
}

# ── Guard: run main logic only when invoked directly, not dot-sourced ────────
# When Pester dot-sources this file to load functions, $MyInvocation.InvocationName
# is '.', so the main block is skipped.
if ($MyInvocation.InvocationName -ne '.') {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
    Invoke-GlowpilotInstall -RepoRoot $repoRoot
}
