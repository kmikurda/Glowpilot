#Requires -Version 5.1
<#
.SYNOPSIS
    Pester v5 tests for install.ps1 helper functions.
.DESCRIPTION
    Tests four public seams:
      1. Test-DotNetVersion        — parses dotnet --version output
      2. Merge-ClaudeHooks         — idempotent JSON merge for Claude settings
      3. Register-GlowpilotTask    — Task Scheduler registration parameters
      4. Install-CopilotHooks      — writes glowpilot.json to ~/.copilot/hooks/

    Run with:  Invoke-Pester -Path install.Tests.ps1 -Output Detailed
    Requires:  Pester >= 5.0  (Install-Module Pester -MinimumVersion 5.0)
#>

BeforeAll {
    # Dot-source the installer to load all helper functions without triggering
    # the main installation logic.
    . (Join-Path $PSScriptRoot 'install.ps1')
}

# ────────────────────────────────────────────────────────────────────────────
# Seam 1: Test-DotNetVersion
# ────────────────────────────────────────────────────────────────────────────
Describe 'Test-DotNetVersion' {

    Context 'when dotnet is not installed or returns nothing' {
        It 'returns $false when command returns null' {
            $result = Test-DotNetVersion -GetVersionCommand { $null }
            $result | Should -BeFalse
        }

        It 'returns $false when command returns an empty string' {
            $result = Test-DotNetVersion -GetVersionCommand { '' }
            $result | Should -BeFalse
        }

        It 'returns $false when command returns non-parseable output' {
            $result = Test-DotNetVersion -GetVersionCommand { 'dotnet: command not found' }
            $result | Should -BeFalse
        }
    }

    Context 'when dotnet version meets the minimum requirement' {
        It 'returns $true for exact minimum version 8.0.100' {
            $result = Test-DotNetVersion -MinMajorVersion 8 -GetVersionCommand { '8.0.100' }
            $result | Should -BeTrue
        }

        It 'returns $true for version 10.x (above minimum)' {
            $result = Test-DotNetVersion -MinMajorVersion 8 -GetVersionCommand { '10.0.100' }
            $result | Should -BeTrue
        }

        It 'returns $true for a preview build (e.g. 10.0.100-preview.1)' {
            $result = Test-DotNetVersion -MinMajorVersion 8 -GetVersionCommand { '10.0.100-preview.1.25163.7' }
            $result | Should -BeTrue
        }
    }

    Context 'when dotnet version is below the minimum requirement' {
        It 'returns $false for version 7.x' {
            $result = Test-DotNetVersion -MinMajorVersion 8 -GetVersionCommand { '7.0.410' }
            $result | Should -BeFalse
        }

        It 'returns $false for version 6.x' {
            $result = Test-DotNetVersion -MinMajorVersion 8 -GetVersionCommand { '6.0.427' }
            $result | Should -BeFalse
        }
    }
}

# ────────────────────────────────────────────────────────────────────────────
# Seam 2: Merge-ClaudeHooks
# ────────────────────────────────────────────────────────────────────────────
Describe 'Merge-ClaudeHooks' {

    BeforeAll {
        $ExePath     = 'C:\Users\test\AppData\Local\Glowpilot\bin\claude-code\glowpilot-claude-code.exe'
        # Merge-ClaudeHooks converts the Windows path to MSYS2 format for Claude Code (Git Bash).
        $ExePathMsys = '/c/Users/test/AppData/Local/Glowpilot/bin/claude-code/glowpilot-claude-code.exe'
    }

    Context 'when the config is empty or does not exist' {
        It 'creates all four required hook event types' {
            $result = Merge-ClaudeHooks -ConfigJson '' -AdapterExePath $ExePath
            $obj    = $result | ConvertFrom-Json

            $obj.hooks.PreToolUse   | Should -Not -BeNullOrEmpty
            $obj.hooks.PostToolUse  | Should -Not -BeNullOrEmpty
            $obj.hooks.Notification | Should -Not -BeNullOrEmpty
            $obj.hooks.Stop         | Should -Not -BeNullOrEmpty
        }

        It 'stores the command as a MSYS2 path (required by Claude Code Git-Bash hooks)' {
            $result = Merge-ClaudeHooks -ConfigJson '' -AdapterExePath $ExePath
            $obj    = $result | ConvertFrom-Json

            foreach ($eventType in @('PreToolUse', 'PostToolUse', 'Notification', 'Stop')) {
                $obj.hooks.$eventType[0].hooks[0].command | Should -Be $ExePathMsys
            }
        }

        It 'sets an empty matcher string in every hook entry' {
            $result = Merge-ClaudeHooks -ConfigJson '' -AdapterExePath $ExePath
            $obj    = $result | ConvertFrom-Json

            $obj.hooks.PreToolUse[0].matcher | Should -Be ''
        }

        It 'sets hook type to "command"' {
            $result = Merge-ClaudeHooks -ConfigJson '' -AdapterExePath $ExePath
            $obj    = $result | ConvertFrom-Json

            $obj.hooks.PreToolUse[0].hooks[0].type | Should -Be 'command'
        }
    }

    Context 'when the config already contains unrelated keys' {
        It 'preserves other top-level keys' {
            $existing = @{ apiKey = 'abc123'; theme = 'dark' } | ConvertTo-Json
            $result   = Merge-ClaudeHooks -ConfigJson $existing -AdapterExePath $ExePath
            $obj      = $result | ConvertFrom-Json

            $obj.apiKey | Should -Be 'abc123'
            $obj.theme  | Should -Be 'dark'
        }

        It 'preserves other properties inside the hooks object' {
            $existing = @{
                hooks = @{
                    UnknownEvent = @(@{ matcher = 'x'; hooks = @(@{ type = 'command'; command = 'other.exe' }) })
                }
            } | ConvertTo-Json -Depth 10
            $result = Merge-ClaudeHooks -ConfigJson $existing -AdapterExePath $ExePath
            $obj    = $result | ConvertFrom-Json

            # Our four entries should be added
            $obj.hooks.PreToolUse  | Should -Not -BeNullOrEmpty
            # Pre-existing entry must not be removed
            $obj.hooks.UnknownEvent[0].matcher | Should -Be 'x'
        }
    }

    Context 'when the hook for this exe already exists' {
        It 'does not duplicate the entry when called twice' {
            $first  = Merge-ClaudeHooks -ConfigJson ''     -AdapterExePath $ExePath
            $second = Merge-ClaudeHooks -ConfigJson $first -AdapterExePath $ExePath
            $obj    = $second | ConvertFrom-Json

            # Each event type should still have exactly one entry for our exe
            $obj.hooks.PreToolUse.Count   | Should -Be 1
            $obj.hooks.PostToolUse.Count  | Should -Be 1
            $obj.hooks.Notification.Count | Should -Be 1
            $obj.hooks.Stop.Count         | Should -Be 1
        }
    }

    Context 'when an event type already has entries from other tools' {
        It 'appends our entry without removing the existing one' {
            $existing = @{
                hooks = @{
                    PreToolUse = @(
                        @{ matcher = 'mytool'; hooks = @(@{ type = 'command'; command = 'other-tool.exe' }) }
                    )
                }
            } | ConvertTo-Json -Depth 10

            $result = Merge-ClaudeHooks -ConfigJson $existing -AdapterExePath $ExePath
            $obj    = $result | ConvertFrom-Json

            $obj.hooks.PreToolUse.Count | Should -Be 2
            # The original entry is first
            $obj.hooks.PreToolUse[0].matcher | Should -Be 'mytool'
            # Our entry is appended (stored as MSYS2 path)
            $obj.hooks.PreToolUse[1].hooks[0].command | Should -Be $ExePathMsys
        }

        It 'does not duplicate when our exe already appears nested in an existing entry' {
            # Edge case: our exe is already a hook inside some other entry (MSYS2 path, as stored by the function).
            $existing = @{
                hooks = @{
                    PreToolUse = @(
                        @{
                            matcher = 'combined'
                            hooks   = @(
                                @{ type = 'command'; command = $ExePathMsys }
                            )
                        }
                    )
                }
            } | ConvertTo-Json -Depth 10

            $result = Merge-ClaudeHooks -ConfigJson $existing -AdapterExePath $ExePath
            $obj    = $result | ConvertFrom-Json

            # Should not add a second entry because the exe is already present
            $obj.hooks.PreToolUse.Count | Should -Be 1
        }
    }
}

# ────────────────────────────────────────────────────────────────────────────
# Seam 3: Register-GlowpilotTask
# ────────────────────────────────────────────────────────────────────────────
Describe 'Register-GlowpilotTask' {
    <#
    Strategy: Register-GlowpilotTask uses XML-based registration so that the
    trigger's <Repetition> block can be set (CIM trigger objects do not expose
    those properties as settable). Only Register-ScheduledTask (which writes to
    Task Scheduler) is mocked; everything else runs for real and returns the
    XML string that we can inspect in ParameterFilter.
    #>

    BeforeAll {
        $ServiceExe = 'C:\Users\test\AppData\Local\Glowpilot\bin\service\Glowpilot.Service.exe'
    }

    BeforeEach {
        # Only mock the cmdlet that has real system side effects
        Mock Register-ScheduledTask { [PSCustomObject]@{ TaskName = $TaskName } }
    }

    It 'registers a task named "Glowpilot"' {
        Register-GlowpilotTask -ServiceExePath $ServiceExe

        Should -Invoke Register-ScheduledTask -Times 1 -ParameterFilter {
            $TaskName -eq 'Glowpilot'
        }
    }

    It 'embeds the service exe path in the XML action' {
        Register-GlowpilotTask -ServiceExePath $ServiceExe

        Should -Invoke Register-ScheduledTask -Times 1 -ParameterFilter {
            $Xml -like "*<Command>$ServiceExe</Command>*"
        }
    }

    It 'sets RunLevel to LeastPrivilege (no UAC required)' {
        Register-GlowpilotTask -ServiceExePath $ServiceExe

        Should -Invoke Register-ScheduledTask -Times 1 -ParameterFilter {
            $Xml -like '*<RunLevel>LeastPrivilege</RunLevel>*'
        }
    }

    It 'uses -Force so re-installation is idempotent' {
        Register-GlowpilotTask -ServiceExePath $ServiceExe

        Should -Invoke Register-ScheduledTask -Times 1 -ParameterFilter {
            $Force -eq $true
        }
    }

    It 'calls Register-ScheduledTask exactly once' {
        Register-GlowpilotTask -ServiceExePath $ServiceExe

        Should -Invoke Register-ScheduledTask -Times 1
    }

    It 'sets a 1-minute repetition interval so the service self-heals while logged in' {
        Register-GlowpilotTask -ServiceExePath $ServiceExe

        Should -Invoke Register-ScheduledTask -Times 1 -ParameterFilter {
            $Xml -like '*<Interval>PT1M</Interval>*'
        }
    }

    It 'disables StopAtDurationEnd so the repetition runs indefinitely' {
        Register-GlowpilotTask -ServiceExePath $ServiceExe

        Should -Invoke Register-ScheduledTask -Times 1 -ParameterFilter {
            $Xml -like '*<StopAtDurationEnd>false</StopAtDurationEnd>*'
        }
    }

    It 'includes a wake-from-sleep EventTrigger (Kernel-Power EventID 107)' {
        Register-GlowpilotTask -ServiceExePath $ServiceExe

        Should -Invoke Register-ScheduledTask -Times 1 -ParameterFilter {
            $Xml -like '*EventTrigger*' -and $Xml -like '*EventID=107*'
        }
    }

    It 'uses IgnoreNew to suppress duplicate instances during repetition checks' {
        Register-GlowpilotTask -ServiceExePath $ServiceExe

        Should -Invoke Register-ScheduledTask -Times 1 -ParameterFilter {
            $Xml -like '*<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>*'
        }
    }
}

# ────────────────────────────────────────────────────────────────────────────
# Seam 4: Install-CopilotHooks
# ────────────────────────────────────────────────────────────────────────────
Describe 'Install-CopilotHooks' {

    BeforeAll {
        $AdapterExe = 'C:\Users\test\AppData\Local\Glowpilot\bin\copilot\glowpilot-copilot.exe'
        # Use a temp directory to avoid touching the real ~/.copilot/hooks/ during tests.
        $TempHome = Join-Path $TestDrive 'fakehome'
    }

    BeforeEach {
        # Reset the temp home before each test.
        if (Test-Path $TempHome) { Remove-Item -Recurse -Force $TempHome }
        New-Item -ItemType Directory -Path $TempHome -Force | Out-Null
        # Override $env:USERPROFILE so Install-CopilotHooks writes to the temp dir.
        $env:USERPROFILE = $TempHome
    }

    AfterAll {
        # Restore USERPROFILE after the Describe block.
        $env:USERPROFILE = $env:SystemRoot -replace '\\Windows$', "\Users\$env:USERNAME"
    }

    It 'creates the hooks directory if it does not exist' {
        Install-CopilotHooks -AdapterExePath $AdapterExe
        $expected = Join-Path $TempHome '.copilot\hooks'
        Test-Path $expected | Should -BeTrue
    }

    It 'writes glowpilot.json to the correct location' {
        Install-CopilotHooks -AdapterExePath $AdapterExe
        $file = Join-Path $TempHome '.copilot\hooks\glowpilot.json'
        Test-Path $file | Should -BeTrue
    }

    It 'produces valid JSON with version 1' {
        Install-CopilotHooks -AdapterExePath $AdapterExe
        $file    = Join-Path $TempHome '.copilot\hooks\glowpilot.json'
        $content = Get-Content $file -Raw
        $obj     = $content | ConvertFrom-Json
        $obj.version | Should -Be 1
    }

    It 'embeds the adapter path (backslashes JSON-escaped) in every hook command' {
        Install-CopilotHooks -AdapterExePath $AdapterExe
        $file    = Join-Path $TempHome '.copilot\hooks\glowpilot.json'
        $content = Get-Content $file -Raw
        # The path must appear with doubled backslashes (JSON escaping).
        $content | Should -Match 'glowpilot-copilot\.exe'
        $content | Should -Match 'C:\\\\Users\\\\test'
    }

    It 'includes all required hook event types' {
        Install-CopilotHooks -AdapterExePath $AdapterExe
        $file    = Join-Path $TempHome '.copilot\hooks\glowpilot.json'
        $content = Get-Content $file -Raw
        $obj     = $content | ConvertFrom-Json

        $obj.hooks.sessionStart        | Should -Not -BeNullOrEmpty
        $obj.hooks.userPromptSubmitted | Should -Not -BeNullOrEmpty
        $obj.hooks.preToolUse          | Should -Not -BeNullOrEmpty
        $obj.hooks.postToolUse         | Should -Not -BeNullOrEmpty
        $obj.hooks.permissionRequest   | Should -Not -BeNullOrEmpty
        $obj.hooks.agentStop           | Should -Not -BeNullOrEmpty
        $obj.hooks.sessionEnd          | Should -Not -BeNullOrEmpty
    }

    It 'is idempotent: running twice does not throw and still produces one file' {
        Install-CopilotHooks -AdapterExePath $AdapterExe
        Install-CopilotHooks -AdapterExePath $AdapterExe

        $dir   = Join-Path $TempHome '.copilot\hooks'
        $files = @(Get-ChildItem $dir -Filter '*.json')
        $files.Count | Should -Be 1
    }
}
