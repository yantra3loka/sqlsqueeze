# SqlSqueeze operator scripts

## PowerShell module (`SqlSqueeze.psm1`)

```powershell
Import-Module ./tools/SqlSqueeze.psm1
$env:SQLSQUEEZE_EXE = 'C:\tools\sqlsqueeze\sqlsqueeze.exe'  # or put it on PATH

Backup-SqlDatabase -Database app -Path /backups/app.bak.zst
# One backup, two drives, when the database does not fit on one volume:
Backup-SqlDatabase -Database app -Path @(
    'C:\Backups\app_full_p1.bak.zst',
    'D:\Backups\app_full_p2.bak.zst')
Backup-SqlDatabase -Database app -Path /backups/app.bak.gz.aes `
    -Transform @('gzip', 'aes(passwordEnv=BACKUP_KEY)') -BackupType Differential -Checksum
Restore-SqlDatabase -Path /backups/app.bak.zst -Database app_copy -Replace
Remove-SqlBackup -Directory /backups -Pattern '*.bak.zst' -OlderThanDays 30
Restore-SqlChain -Directory /backups -Database app -TargetDatabase app_copy
Restore-SqlChain -Directory /backups -Database app -TargetDatabase app_copy -StandbyFile /backups/undo.sby -WhatIf
Restore-SqlChain -Directory /backups -Database app -TargetDatabase app_copy -StopAt '2024-05-01 13:30:00'
Test-SqlBackup -Path /backups/app.bak.zst -ServerInstance .  # +VERIFYONLY when sqlcmd exists
```

Every call runs the CLI with `--json` and returns the result object;
failures throw with the server-side message.

`Backup-SqlDatabase -Path` takes one path or several. One path is a
single file and keeps the previous `local(path=...)` command line.
Several paths are one striped backup, `local(path=A;path=B)`, for a
database that does not fit on one drive (`C:` plus `D:` above). WITH
FORMAT is default-on at the engine level (each path is created fresh;
there is no format switch). Native COMPRESSION is replaced by the
zstd/gzip transform.

`Restore-SqlChain` picks the latest full, the latest diff after it, and
the logs after that, restoring everything `NORECOVERY` except the tail
(`RECOVERY`, or `STANDBY` with `-StandbyFile`, or `NORECOVERY` with
`-NoRecovery`). It still restores one file per step. Files with a
`copy...only` marker after the `<db>_<type>_` prefix are never picked
(mark every stripe that way, e.g. `app_full_copyonly_p1.bak.zst` on `C:`
and `app_full_copyonly_p2.bak.zst` on `D:`). `Backup-SqlDatabase
-CopyOnly` warns once per `-Path` entry whose file name has no such
marker.
`-StopAt` puts `STOPAT` on every log restore and stops at the log
containing the target time; it errors when there is no log to stop in.
`-StandbyFile` and `-NoRecovery` cannot be combined.

## Storage write retries

`local(path=...)` accepts `writeretries=N` (total attempts, default 1 =
no retry) and `writeretrybackoffms=M` (base backoff, default 1000,
doubling each attempt). Transient `IOException`s on write/flush are
retried; anything else fails fast. For dropped shares start with
`writeretries=5`.

Repeat `path=` for striped (parallel multi-file) backups — one `path`
per stripe, e.g.
`local(path=/backups/app_p1.bak.zst;path=/backups/app_p2.bak.zst)`.
`Backup-SqlDatabase -Path` joins a path array the same way, so a
database too big for one drive can split across `C:` and `D:`.
Device count is the path count (variable, not fixed). Restore needs the
same stripes in the same order. WITH FORMAT is default-on at the engine
level; native COMPRESSION is replaced by the zstd/gzip transform.
Copy-only stripe file names still need a `copy...only` marker
(`app_full_copyonly_p1.bak.zst`); `-CopyOnly` warns per unmarked path.

## SQL Agent jobs (`sql-agent/`)

CmdExec-only templates (work on Windows and Linux Agent): edit the four
variables at the top of each file and run it. Full nightly + prune,
diff every 4h, log every 15min. Filenames use sqlsqueeze `{yyyyMMdd_HHmmss}`
tokens — no `%date%` parsing.

`Verify-Backup-Job.sql` runs a nightly `RESTORE VERIFYONLY` over the
newest native-format full backup — VERIFYONLY rejects compressed outputs
by design, so it targets native-format files. `Alert-Operator.sql` creates
the msdb operator and wires failure notifications onto the four backup
jobs (e-mail needs Database Mail configured on the instance).

## Cron wrapper (`cron/sqlsqueeze-cron.sh`)

Runs a backup from cron and POSTs a JSON webhook when it fails:

```bash
export WEBHOOK_URL='https://hooks.example.com/sqlsqueeze'
0 2 * * * /opt/sqlsqueeze/tools/cron/sqlsqueeze-cron.sh backup \
    "db(database=app)" zstd "local(path=/backups/app.bak.zst)"
```

Arguments go straight to the CLI (the wrapper appends `--json`), and the
CLI's exit code is the wrapper's exit code, so cron still sees the
failure. The webhook body is `{status, operation, exitCode, message,
timestamp, host}`. `WEBHOOK_URL` and the optional `WEBHOOK_TOKEN` (a
Bearer token) come from the environment — no URL, token or password is
stored in the script.

## msdb dashboard (`msdb/BackupHistory.sql`)

Creates `dbo.SqlSqueezeBackupHistory` (works for native backups too —
VDI backups log to `backupset` like everything else). Includes starter
queries for "latest per database" and "missing a fresh full".

## CLI scripting contract

- Exit codes: `0` success, `-1` any failure (backup/restore/prune).
- Append `--json` to `backup`, `restore` or `prune` for exactly one result
  line on stdout: `{status, operation, bytesProcessed?, estimatedBytes?,
  elapsedSeconds?, mbPerSecond?}` or `{status:"error", operation, message}`.
  Progress chatter is suppressed in `--json` mode; take the last line.
