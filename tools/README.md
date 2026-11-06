# SqlSqueeze operator scripts

## PowerShell module (`SqlSqueeze.psm1`)

```powershell
Import-Module ./tools/SqlSqueeze.psm1
$env:SQLSQUEEZE_EXE = 'C:\tools\sqlsqueeze\sqlsqueeze.exe'  # or put it on PATH

Backup-SqlDatabase -Database app -Path /backups/app.bak.zst
# One backup, two drives, when the database does not fit on one volume:
Backup-SqlDatabase -Database app -Path @(
    'C:\Backups\app_full_20261106_013000_s01of02.bak.zst',
    'D:\Backups\app_full_20261106_013000_s02of02.bak.zst')
Backup-SqlDatabase -Database app -Path /backups/app.bak.gz.aes `
    -Transform @('gzip', 'aes(passwordEnv=BACKUP_KEY)') -BackupType Differential -Checksum
Restore-SqlDatabase -Path /backups/app.bak.zst -Database app_copy -Replace
Remove-SqlBackup -Directory /backups -Pattern '*.bak.zst' -OlderThanDays 30
Restore-SqlChain -Directory /backups -Database app -TargetDatabase app_copy
Restore-SqlChain -Directory @('C:\Backups', 'D:\Backups') -Database app -TargetDatabase app_copy
Restore-SqlChain -Directory /backups -Database app -TargetDatabase app_copy -StandbyFile /backups/undo.sby -WhatIf
Restore-SqlChain -Directory /backups -Database app -TargetDatabase app_copy -StopAt '2024-05-01 13:30:00'
Test-SqlBackup -Path /backups/app.bak.zst -ServerInstance .  # +VERIFYONLY when sqlcmd exists
Test-SqlSqueezeBackup -Path /backups/app.bak.zst             # hashes vs manifest, no SQL Server
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

`Restore-SqlChain` picks the latest full set, the latest diff set after it, and
the log sets after that, restoring everything `NORECOVERY` except the tail
(`RECOVERY`, or `STANDBY` with `-StandbyFile`, or `NORECOVERY` with
`-NoRecovery`). `-Directory` accepts one directory or a string array for
stripes on different drives. Name each stripe
`<db>_<type>_[copyonly_]<yyyyMMdd_HHmmss>_s<ii>of<nn>.bak.zst`, with
two-digit, one-based `ii` and `nn` (for example,
`app_full_20261106_013000_s01of02.bak.zst` and
`app_full_20261106_013000_s02of02.bak.zst`). Files sharing database,
type, copy-only marker, timestamp, and `nn` form one backup set. The
planner checks that every index from 1 through `nn` exists, then restores
the set in index order as one `local(path=...;path=...)` step. Files with
older names remain individual restore steps. Files with a
`copy...only` marker after the `<db>_<type>_` prefix are never picked
(mark every stripe that way, e.g. `app_full_copyonly_20261106_013000_s01of02.bak.zst`
on `C:` and `app_full_copyonly_20261106_013000_s02of02.bak.zst`
on `D:`). `Backup-SqlDatabase
-CopyOnly` warns once per `-Path` entry whose file name has no such
marker.
`-StopAt` puts `STOPAT` on every log restore and stops at the log
containing the target time; it errors when there is no log to stop in.
`-StandbyFile` and `-NoRecovery` cannot be combined.
## Object storage (`s3`)

`s3(endpoint=...;bucket=...;key=...;region=...;accesskey=...;secretkeyenv=...)`
stores backups in any S3-compatible service (AWS S3, MinIO, moto-tested).
Uploads always use multipart (buffered `partsizeMB`, default 8), so
arbitrarily large backups stream; the bucket is created when missing.
`key=` repeats for striped backups and takes `{date}` tokens. Prune with
`s3(endpoint=...;bucket=...;prefix=...;pattern=...;olderThanDays=...)`.
Signatures are SigV4 with no new dependencies.

`azure(endpoint=...;container=...;blob=...;account=...;accountkeyenv=...)`
stores backups in Azure Blob (or the Azurite emulator, tested live).
Uploads stage blocks (`blocksizeMB`, default 8) and commit a block list;
the container is created when missing. `blob=` repeats for striped
backups and takes `{date}` tokens. Prune mirrors `s3`. Auth is Storage
Shared Key with no new dependencies.

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
(`app_full_copyonly_20261106_013000_s01of02.bak.zst`); `-CopyOnly` warns per unmarked path.

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
  Progress chatter is suppressed in `--json` mode, but other lines can still
  appear (`local:`, `zstd`, and the SQL Server VDI library's abort notice,
  which it prints *after* the result). Take the last line that starts with
  `{`, not simply the last line; the cron wrapper and PowerShell module do.
- `sqlsqueeze verify <storage> [pipeline...] [--sql "db(...)"] --json`
  prints `{status, operation:"verify", manifestFound, storedBytes,
  rawBytes, sqlChecked, sqlMessage, problems[]}`.
  `status` is `ok` only when `problems` is empty (exit `0`); otherwise
  `status:"error"`, `problems` lists what differs, exit `-1`. With
  `manifestFound:false` an `ok` only means the file decoded. With `--sql`,
  `sqlChecked` is true once SQL Server's `RESTORE VERIFYONLY` ran and
  `sqlMessage` is what it said (or why it failed). The cron
  wrapper reports `problems` as the webhook message, so it can run
  `verify` from cron: `sqlsqueeze-cron.sh verify file:///backups/app.bak.zst zstd`.
- `sqlsqueeze backup-all <databases> ... --json` prints `{status,
  operation:"backup-all", results:[{database, backupType, status,
  message, bytesProcessed, elapsedSeconds}]}`. Each result's `status` is
  `ok`, `skipped` (with the reason in `message`) or `error`; `backupType`
  is the type actually run, which differs from the requested one when a
  diff/log backup fell back to a full (`message` says so). The top-level
  `status` is `error` and the exit code `-1` if any database failed;
  skips do not count. Selecting nothing, or several databases into one
  path, is a top-level `{status:"error", message}` before any backup runs.
- `sqlsqueeze unpack <storage> [pipeline...] <native-output> --json` prints
  `{status, operation:"unpack", manifestFound, bytesWritten, problems[]}`;
  `status:"error"` (exit `-1`) means the output was removed. Refusing to
  overwrite, the same source and destination, or a different number of
  files is a top-level `{status:"error", message}` before anything is written.
- `restore --json` also prints `warnings[]` (for example orphaned users after
  a restore onto another server); the restore itself succeeded. `restore
  --kill-connections` rolls back other sessions first.
- `sqlsqueeze restore-chain <folder> <database> --db "db(...)" --json`
  prints `{status, operation:"restore-chain", dryRun, database,
  bytesProcessed, steps:[{file, backupType, status, message,
  bytesProcessed}], warnings[], problems[]}`. A step's `status` is `ok`,
  `error`, `not run` (after a failure) or `planned` (`--dry-run`); `status`
  is `error` and the exit code `-1` if any step failed. `warnings` lists
  manifests that were skipped while scanning. Nothing to restore (no full
  backup of that database in the folder) is a top-level
  `{status:"error", message}`.
- `backup --json` and `backup-all --json` also print `copies` (one entry per
  `--copy-to`: `destination` (no secrets), `status`, `attempts`, `bytesCopied`,
  `bytesSkipped`, `problems`) and `copyProblems` (backup-all: per result). A
  failed copy does not make the backup's own `status` an error, but the exit
  code is `-1`, so check `copyProblems` to tell a failed copy from a failed
  backup.
- `sqlsqueeze copy <source> <destination> --json` prints `{status,
  operation:"copy", attempts, bytesCopied, bytesSkipped, elapsedSeconds,
  warnings[], problems[]}` (`bytesSkipped`: bytes an S3 or Azure destination
  already held from an earlier attempt, read but not sent again); `status:"error"` (exit `-1`) means it gave up or hit a fault
  retrying cannot fix, and `problems` says which. A source that does not match
  its manifest is an error, not a warning. Different file counts, or the same
  source and destination, are a top-level `{status:"error", message}`.
- `--from-manifest` (on `unpack`, `restore`, `verify`, `test-restore`) takes
  the pipeline from the manifest instead of the command line; it is an error
  to give both, or to use it where there is no manifest or the pipeline had a
  literal secret.
- `sqlsqueeze test-restore <storage> [pipeline...] --db "db(...)" --json`
  prints `{status, operation:"test-restore", scratchDatabase,
  restoredBytes, dropped, elapsedSeconds, problems[]}`. `status` is `ok`
  only when the backup restored, `DBCC CHECKDB` was clean and the scratch
  database was dropped (or `--keep` was given); otherwise `error` with
  `problems` and exit `-1`. A backup it cannot test (no manifest, not a
  full backup, no file list) is a top-level `{status:"error", message}`.
  Cron: `sqlsqueeze-cron.sh test-restore file:///backups/app.bak.zst zstd --db "db(user=sa;passwordEnv=SQL_PW)"`.
- `local` storage writes `<file>.manifest.json` beside each backup, and `s3`
  and `azure` write `<key>.manifest.json` as an object beside it (database,
  type, pipeline with secrets redacted, sizes and SHA-256, and for
  backups of a database its files, so `test-restore` can place them);
  `prune` removes it with the backup in every storage.
