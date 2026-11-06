# Tests for tools/SqlSqueeze.psm1 using a fake sqlsqueeze CLI (a shell script),
# so no SQL Server is needed.
#
#   pwsh tools/tests/module-test.ps1
#
# Needs PowerShell 7 and bash.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $here '..' 'SqlSqueeze.psd1') -Force

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("sqsmodule-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$failures = 0

# The SQL Server VDI library prints an abort notice on stdout after the CLI's
# JSON line, so the JSON is not the last line of output.
$vdiNoise = 'Host_1234_SQLVDIMemoryName_0: CVD::GetCommand: Abort bit is set: error 0'

function New-FakeCli([string]$Json, [int]$ExitCode) {
    $path = Join-Path $work 'fake-sqlsqueeze'
    $script = "#!/usr/bin/env bash`necho 'local:'`necho '$Json'`nprintf '%s' '$vdiNoise'`nexit $ExitCode`n"
    [System.IO.File]::WriteAllText($path, $script)
    & chmod +x $path
    return $path
}

function Assert-That([bool]$Condition, [string]$Name, [string]$Detail = '') {
    if ($Condition) { Write-Host "PASS: $Name" }
    else { Write-Host "FAIL: $Name $Detail"; $script:failures++ }
}

try {
    # success followed by the notice
    $cli = New-FakeCli '{"status":"ok","operation":"verify","manifestFound":true,"storedBytes":10,"rawBytes":20,"problems":[]}' 0
    $result = Test-SqlSqueezeBackup -Path /x/y.bak -ExePath $cli 6>$null 3>$null
    Assert-That ($result.status -eq 'ok' -and $result.rawBytes -eq 20) 'a good verify is parsed even when the VDI notice comes after it'

    # failure followed by the notice: the thrown message names the problems
    $cli = New-FakeCli '{"status":"error","operation":"verify","manifestFound":true,"problems":["a.bak: stored sha256 mismatch","a.bak: raw sha256 mismatch"]}' 255
    $message = ''
    try { Test-SqlSqueezeBackup -Path /x/y.bak -ExePath $cli 6>$null 3>$null | Out-Null } catch { $message = $_.Exception.Message }
    Assert-That ($message -like '*stored sha256 mismatch*raw sha256 mismatch*') 'a failed verify throws its problems' "(got: $message)"
    Assert-That ($message -notlike '*SQLVDIMemoryName*') 'the thrown message is not the VDI notice' "(got: $message)"

    # an ordinary error message
    $cli = New-FakeCli '{"status":"error","operation":"backup","message":"disk full"}' 255
    $message = ''
    try { Test-SqlSqueezeBackup -Path /x/y.bak -ExePath $cli 6>$null 3>$null | Out-Null } catch { $message = $_.Exception.Message }
    Assert-That ($message -like '*disk full*') 'an ordinary error reports its message' "(got: $message)"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($failures -gt 0) { Write-Host "$failures failing"; exit 1 }
Write-Host 'all passed'
