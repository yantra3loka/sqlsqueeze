# SqlSqueeze PowerShell module.
# Requires the sqlsqueeze binary: set $env:SQLSQUEEZE_EXE, put `sqlsqueeze`
# on PATH, or pass -ExePath. Every call runs the CLI with --json and
# returns the result object (throws on failure).

$script:DefaultExe = $null

function Resolve-SqlSqueezeExe {
    param([string]$ExePath)
    if ($ExePath) { return $ExePath }
    if ($script:DefaultExe) { return $script:DefaultExe }
    if ($env:SQLSQUEEZE_EXE -and (Test-Path $env:SQLSQUEEZE_EXE)) {
        $script:DefaultExe = $env:SQLSQUEEZE_EXE
        return $script:DefaultExe
    }
    $found = Get-Command sqlsqueeze, sqlsqueeze.exe -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty Source
    if ($found) {
        $script:DefaultExe = $found
        return $found
    }
    throw "sqlsqueeze not found. Set `$env:SQLSQUEEZE_EXE or install from https://github.com/yantra3loka/sqlsqueeze/releases"
}

function Invoke-SqlSqueeze {
    param([string[]]$Arguments, [string]$ExePath)
    $exe = Resolve-SqlSqueezeExe -ExePath $ExePath
    $lines = & $exe @Arguments --json 2>&1
    $code = $LASTEXITCODE
    $json = ($lines | Select-Object -Last 1).ToString()
    $result = $null
    try { $result = $json | ConvertFrom-Json } catch { }
    if ($code -ne 0 -or $null -eq $result -or $result.status -ne 'ok') {
        $msg = if ($result -and $result.message) { $result.message } else { ($lines -join "`n") }
        throw "sqlsqueeze failed (exit $code): $msg"
    }
    return $result
}

function Join-PluginConfig {
    param([string]$Name, [hashtable]$Options)
    $parts = @($Name)
    if ($Options) {
        # Sort for deterministic command lines (tests, logs).
        $pairs = foreach ($k in ($Options.Keys | Sort-Object)) {
            $v = $Options[$k]
            if ($null -eq $v) { "$k" } else { "$k=$v" -replace ';', ';;' }
        }
        $parts += '(' + ($pairs -join ';') + ')'
    }
    return -join $parts
}

function Backup-SqlDatabase {
    <#
    .SYNOPSIS
        Back up a SQL Server database through SqlSqueeze (compressed VDI backup).
    .EXAMPLE
        Backup-SqlDatabase -Database model -Path /backups/model.bak.zst
    .EXAMPLE
        Backup-SqlDatabase -Database app -Path /backups/app.bak.gz.aes `
            -Transform @('gzip', 'aes(passwordEnv=BACKUP_KEY)') -BackupType Differential
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Database,
        [Parameter(Mandatory, Position = 1)][string]$Path,
        [string[]]$Transform = @('zstd'),
        [string]$ServerInstance,
        [ValidateSet('Full', 'Differential', 'Log')][string]$BackupType = 'Full',
        [switch]$CopyOnly,
        [switch]$Checksum,
        [string]$ExePath
    )
    $dbOpts = @{ database = $Database; backuptype = $BackupType.ToLowerInvariant() }
    if ($ServerInstance) { $dbOpts['instancename'] = $ServerInstance }
    if ($CopyOnly) { $dbOpts['copy_only'] = $null }
    if ($Checksum) { $dbOpts['checksum'] = $null }
    $args = @('backup', (Join-PluginConfig 'db' $dbOpts)) + $Transform +
        @((Join-PluginConfig 'local' @{ path = $Path }))
    $result = Invoke-SqlSqueeze -Arguments $args -ExePath $ExePath
    Write-Host ("Backup complete: {0:N0} bytes in {1:N1}s ({2:N1} MB/s)" -f
        $result.bytesProcessed, $result.elapsedSeconds, $result.mbPerSecond)
    return $result
}

function Restore-SqlDatabase {
    <#
    .SYNOPSIS
        Restore a SqlSqueeze backup file into a database.
    .EXAMPLE
        Restore-SqlDatabase -Path /backups/model.bak.zst -Database model_restored
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Path,
        [Parameter(Mandatory, Position = 1)][string]$Database,
        [string[]]$Transform = @('zstd'),
        [string]$ServerInstance,
        [switch]$Replace,
        [string]$ExePath
    )
    $dbOpts = @{ database = $Database }
    if ($ServerInstance) { $dbOpts['instancename'] = $ServerInstance }
    if ($Replace) { $dbOpts['replace'] = $null }
    # Restore uses the SAME transform order as backup: each layer inverts
    # itself (verified live: mirrored order fails with a gzip magic error).
    $args = @('restore', (Join-PluginConfig 'local' @{ path = $Path })) + $Transform +
        @((Join-PluginConfig 'db' $dbOpts))
    $result = Invoke-SqlSqueeze -Arguments $args -ExePath $ExePath
    Write-Host ("Restore complete: {0:N0} bytes in {1:N1}s ({2:N1} MB/s)" -f
        $result.bytesProcessed, $result.elapsedSeconds, $result.mbPerSecond)
    return $result
}

function Remove-SqlBackup {
    <#
    .SYNOPSIS
        Delete backup files older than retention (sqlsqueeze prune).
    .EXAMPLE
        Remove-SqlBackup -Directory /backups -Pattern '*.bak.zst' -OlderThanDays 30
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Directory,
        [string]$Pattern = '*',
        [Parameter(Mandatory)][int]$OlderThanDays,
        [string]$ExePath
    )
    $cfg = "local(dir=$Directory;pattern=$Pattern;olderThanDays=$OlderThanDays)"
    if ($PSCmdlet.ShouldProcess($Directory, "prune files older than $OlderThanDays days")) {
        $result = Invoke-SqlSqueeze -Arguments @('prune', $cfg) -ExePath $ExePath
        Write-Host ("Pruned {0} file(s), reclaimed {1:N0} bytes" -f
            $result.deletedFiles, $result.bytesReclaimed)
        return $result
    }
}

function Test-SqlBackup {
    <#
    .SYNOPSIS
        Sanity-check a backup file: exists, non-empty, and (when sqlcmd +
        server details are available) RESTORE VERIFYONLY.
    .NOTES
        VERIFYONLY only works on native-format files. Compressed files
        (.zst/.gz/.aes) fail it by design — same as Redgate .sqb files.
        Verify those by restoring to a scratch database and checksumming.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$ServerInstance,
        [string]$SqlUser,
        [string]$SqlPassword
    )
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.Length -eq 0) { throw "Backup file is empty: $Path" }
    $verified = $false
    if ($ServerInstance) {
        $sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
        if (-not $sqlcmd) {
            Write-Warning "sqlcmd not found; skipping RESTORE VERIFYONLY (file checks passed)."
        }
        else {
            $auth = if ($SqlUser) { @('-U', $SqlUser, '-P', $SqlPassword) } else { @('-E') }
            $q = "RESTORE VERIFYONLY FROM DISK = '$($Path -replace "'", "''")'"
            & $sqlcmd.Source -S $ServerInstance @auth -Q $q -b
            if ($LASTEXITCODE -ne 0) { throw "RESTORE VERIFYONLY failed for $Path" }
            $verified = $true
        }
    }
    return [pscustomobject]@{
        Path     = $file.FullName
        Bytes    = $file.Length
        Verified = $verified
    }
}

function Restore-SqlChain {
    <#
    .SYNOPSIS
        Restore a full -> diff -> log chain for log shipping or point-in-time recovery.
    .DESCRIPTION
        Picks the latest full backup, the latest differential after it, and
        every log backup after the differential (or after the full when no
        diff exists). Restores each WITH NORECOVERY except the last file,
        which is RECOVERY by default, STANDBY with -StandbyFile, or left
        NORECOVERY with -NoRecovery (shipping secondary stays loading).
        Supports -WhatIf: prints the planned sequence without restoring.
    .EXAMPLE
        Restore-SqlChain -Directory /backups -Database app -TargetDatabase app_copy
    .EXAMPLE
        Restore-SqlChain -Directory /backups -Database app -TargetDatabase app_copy `
            -Transform @('zstd') -StandbyFile /backups/standby.sby -WhatIf
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$TargetDatabase,
        [string]$FullPattern,
        [string]$DiffPattern,
        [string]$LogPattern,
        [string[]]$Transform = @('zstd'),
        [string]$ServerInstance,
        [string]$StandbyFile,
        [switch]$NoRecovery,
        [string]$ExePath
    )
    if (-not $FullPattern) { $FullPattern = "$Database`_full_*" }
    if (-not $DiffPattern) { $DiffPattern = "$Database`_diff_*" }
    if (-not $LogPattern) { $LogPattern = "$Database`_log_*" }

    $full = Get-ChildItem -LiteralPath $Directory -File -Filter $FullPattern |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $full) { throw "No full backup matching $FullPattern in $Directory" }
    $diff = Get-ChildItem -LiteralPath $Directory -File -Filter $DiffPattern |
        Where-Object { $_.LastWriteTime -ge $full.LastWriteTime } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $anchor = if ($diff) { $diff } else { $full }
    $logs = @(Get-ChildItem -LiteralPath $Directory -File -Filter $LogPattern |
        Where-Object { $_.LastWriteTime -ge $anchor.LastWriteTime } |
        Sort-Object LastWriteTime)
    if (-not $logs -or $logs.Count -eq 0) {
        Write-Warning "No log backups after $($anchor.Name); restoring full/diff only."
    }

    $sequence = @(
        @{ File = $full; Recovery = 'norecovery' }
    )
    if ($diff) { $sequence += @{ File = $diff; Recovery = 'norecovery' } }
    $lastIndex = $sequence.Count + $logs.Count - 1
    $i = $sequence.Count
    foreach ($log in $logs) {
        $mode = 'norecovery'
        if ($i -eq $lastIndex) {
            if ($StandbyFile) { $mode = "standby=$StandbyFile" }
            elseif (-not $NoRecovery) { $mode = 'recovery' }
        }
        $sequence += @{ File = $log; Recovery = $mode }
        $i++
    }
    if ($logs.Count -eq 0 -and -not $NoRecovery -and -not $StandbyFile) {
        $sequence[-1].Recovery = 'recovery'
    }

    $dbOpts = @{ database = $TargetDatabase }
    if ($ServerInstance) { $dbOpts['instancename'] = $ServerInstance }
    foreach ($step in $sequence) {
        $opts = $dbOpts.Clone()
        $opts['replace'] = $null
        foreach ($flag in ($step.Recovery -split ',')) {
            if ($flag -like 'standby=*') { $opts['standby'] = $flag.Substring(8) }
            else { $opts[$flag] = $null }
        }
        $args = @('restore', (Join-PluginConfig 'local' @{ path = $step.File.FullName })) +
            $Transform + @((Join-PluginConfig 'db' $opts))
        if ($PSCmdlet.ShouldProcess($step.File.Name, "restore WITH $($step.Recovery.ToUpper())")) {
            $null = Invoke-SqlSqueeze -Arguments $args -ExePath $ExePath
            Write-Host ("Restored {0} WITH {1}" -f $step.File.Name, $step.Recovery.ToUpper())
        }
    }
}

Export-ModuleMember -Function Backup-SqlDatabase, Restore-SqlDatabase,
    Remove-SqlBackup, Test-SqlBackup, Restore-SqlChain
