---
layout: page
title: SqlSqueeze DBA Guide
---

# SqlSqueeze DBA Guide

Backup and restore SQL Server with compression where native
`BACKUP ... WITH COMPRESSION` is unavailable (Web/Express editions),
without Redgate. Same technique Redgate uses: the Virtual Backup
Device Interface (VDI). The tool **must run on the SQL Server machine**
(VDI is local-only).

Measured pilot (Oct 2026, 51k-row DB): 15.7 MB → 1.06 MB `.zst`
(~14.8x) in 0.44 s; restore byte-identical, row counts and checksums match.

## Install

1. Unzip the release (`sqlsqueeze-win-x64.zip`) to `C:\tools\sqlsqueeze\`.
   Self-contained build needs no .NET install; framework-dependent build
   needs the matching .NET runtime.
2. Confirm: `C:\tools\sqlsqueeze\sqlsqueeze.exe version`
3. Confirm plugins: `sqlsqueeze.exe listplugins`
   (`db` · `gzip bzip2 zip64 zstd aes rate` · `local s3 azure`).

## Connecting to SQL Server

The database spec is `db(...)`. Two parameters matter most:

```text
db(database=MyDB)                          default instance, Windows auth
db(database=MyDB;instancename=SQLEXPRESS)   named instance, Windows auth
db(database=MyDB;user=sa;passwordEnv=SQL_PW) SQL login (recommended: passwordEnv=,
                                           never password= — command lines are visible)
```

Notes:

- The parameter is **`instancename=`**, not `instance=`. A wrong name is
  silently ignored and you get a connection error (Named Pipes, error 40).
- `[MyDB]` in brackets is shorthand for `db(database=MyDB)` (default
  instance only).
- Every command ends with `--json` in jobs: one result object on stdout,
  exit `0` on success, `-1` on any failure.

## Backups

```text
:: Full (default type)
sqlsqueeze.exe backup "db(database=MyDB;instancename=SQLEXPRESS)" zstd "local(path=D:\Backups\MyDB_full.bak.zst)" --json

:: Differential and log (note backuptype= values: full|differential|log)
sqlsqueeze.exe backup "db(database=MyDB;instancename=SQLEXPRESS;backuptype=differential)" zstd "local(path=D:\Backups\MyDB_diff.bak.zst)" --json
sqlsqueeze.exe backup "db(database=MyDB;instancename=SQLEXPRESS;backuptype=log)" zstd "local(path=D:\Backups\MyDB_log.bak.zst)" --json

:: With encryption (password from environment, never on the command line)
sqlsqueeze.exe backup "[MyDB]" zstd "aes(passwordEnv=BACKUP_KEY)" "local(path=D:\Backups\MyDB.bak.zst.aes)" --json
```

Each backup writes `<file>.manifest.json` next to it: database, backup
type, file list, pipeline (secrets redacted — only the `passwordEnv=`
variable *name* is kept), sizes and SHA-256 hashes. **Keep manifests with
their backups.** Without one, verify/unpack can only confirm the file
decodes.

Add `--verify` to any `backup` or `backup-all` to check the finished
backup against its manifest immediately (hashes, no SQL Server needed) —
one command, one result line, exit `-1` if the verify fails. This is the
recommended daily-driver form.

Suggested chain: FULL nightly → DIFF every 4h → LOG every 15min
(templates in `tools/sql-agent/` create these Agent jobs plus retention).

### Many databases at once

```text
sqlsqueeze.exe backup-all USER_DATABASES,-scratch* zstd "local(path=D:\Backups\{database}\{database}_{backuptype}_{yyyyMMdd_HHmmss}.bak.zst)" --db "db(user=sa;passwordEnv=SQL_PW)" --json
```

`{database}` is required when several are selected. A diff/log that has no
full to build on becomes a full (reported), unless `--no-fallback`. One
failing database does not stop the rest; exit code is `-1` if any failed.

### Split (striped) backups

Repeat `path=` for parallel streams (like `BACKUP ... TO DISK=,DISK=`):

```text
sqlsqueeze.exe backup "[MyDB]" zstd "local(path=D:\Backups\MyDB_1.bak.zst;path=D:\Backups\MyDB_2.bak.zst)" --json
```

Restore/unpack/copy must list the **same files in the same order**.
(`key=` repeats for S3, `blob=` for Azure.)

## Restores

```text
:: Restore as a copy (safe: original untouched). MOVE every file:
sqlsqueeze.exe restore "local(path=D:\Backups\MyDB_full.bak.zst)" zstd "db(database=MyDB_copy;replace;move='MyDB'TO'D:\Data\MyDB_copy.mdf';move='MyDB_log'TO'D:\Data\MyDB_copy_log.ldf')" --json

:: Overwrite in place (needs REPLACE; without it SQL Server refuses when
:: the tail of the log was never backed up)
sqlsqueeze.exe restore "local(path=D:\Backups\MyDB_full.bak.zst)" zstd "db(database=MyDB;replace)" --json

:: Over a database that is in use (rolls back other sessions first)
sqlsqueeze.exe restore "local(path=D:\Backups\MyDB_full.bak.zst)" zstd "db(database=MyDB;replace)" --kill-connections --json
```

Notes:

- The MOVE form is exactly `move='logical'TO'path'` (uppercase `TO`,
  single quotes). From PowerShell, quoting through ssh/cmd eats the
  quotes — **put the command in a `.ps1` file and run
  `powershell -File`** instead of inlining it.
- After a usable restore, logins missing on this server are reported as a
  **warning** (orphaned users, matched by SID). Fix with
  `ALTER USER <user> WITH LOGIN = <login>`.
- `--from-manifest` takes the pipeline from the manifest so it is not
  retyped (refused for literal `password=` backups — use `passwordEnv=`).
- FILESTREAM databases restore like any other: MOVE the FILESTREAM
  container to a folder path.

### Restore a whole chain at once

```text
sqlsqueeze.exe restore-chain D:\Backups MyDB --db "db" --as MyDB_copy --json
```

Picks the latest full, the latest diff after it, and every log after that
(copy-only backups excluded), restores in order. Useful flags: `--stopat
"<time>"` (point in time, server local), `--dry-run` (print plan only),
`--norecovery` / `--standby <file>` (leave restoring/readable),
`--replace`, `--kill-connections`. Local folders only.

## Cancelling (Ctrl-C)

The first Ctrl-C aborts the running backup or restore gracefully: the
operation fails through the normal path (exit `-1`), partial output files
are removed, and a `--kill-connections` restore is put back to MULTI_USER.
Press Ctrl-C a second time to kill immediately. A press with nothing
in flight also kills immediately.

One deliberate limit: cancelling a restore leaves the database in the
RESTORING state (there is no log tail to recover with). Re-run the restore
(or the chain) to bring it online.

## Verify without restoring

```text
:: Hashes vs manifest, any machine with the file (no SQL Server needed)
sqlsqueeze.exe verify "local(path=D:\Backups\MyDB_full.bak.zst)" zstd --json

:: Plus SQL Server reading the whole backup (RESTORE VERIFYONLY via VDI)
sqlsqueeze.exe verify "local(path=D:\Backups\MyDB_full.bak.zst)" zstd --sql "db(user=sa;passwordEnv=SQL_PW)" --json

:: Full proof: restore to scratch DB, DBCC CHECKDB, drop it (full backups only)
sqlsqueeze.exe test-restore "local(path=D:\Backups\MyDB_full.bak.zst)" zstd --db "db(user=sa;passwordEnv=SQL_PW)" --json
```

`test-restore` never touches the original; `--keep` leaves the scratch DB
for inspection; `--physical-only` runs the faster CHECKDB. FILESTREAM
databases are supported (each goes to its own folder); anything else that
is not ROWS/LOG still refuses with "restore this database by hand".

## Getting a native .bak back (no lock-in)

```text
sqlsqueeze.exe unpack "local(path=D:\Backups\MyDB.bak.zst.aes)" zstd "aes(passwordEnv=BACKUP_KEY)" "local(path=D:\Restore\MyDB.bak)" --json
```

Writes the plain `.bak` SQL Server produced; plain
`RESTORE DATABASE ... FROM DISK` then works with no SqlSqueeze involved.
Never overwrites an existing file; striped backups need one output per
stripe; output failing the manifest check is removed.

## Retention and copies

```text
:: Delete backups older than 14 days (manifests go with their backups)
sqlsqueeze.exe prune "local(dir=D:\Backups;pattern=MyDB_*.bak.zst;olderThanDays=14)" --json

:: Copy a finished backup elsewhere (second disk, S3, Azure) with retries
sqlsqueeze.exe copy "local(path=D:\Backups\MyDB.bak.zst)" "s3(endpoint=...;bucket=b;key=MyDB.bak.zst;accesskey=...;secretkeyenv=SK)" --json

:: Or copy straight after every backup (repeatable)
sqlsqueeze.exe backup "[MyDB]" zstd "local(path=D:\Backups\MyDB.bak.zst)" --copy-to "local(path=\\nas\Backups\MyDB.bak.zst)" --json
```

A failed copy never harms the backup; retry with `sqlsqueeze copy`.
Source and destination need the same stripe count.

### Streaming vs copy (read this before backing up to the cloud)

You *can* stream a backup straight into S3/Azure by naming it as the
backup target — each stripe uploads multipart (S3) or in blocks (Azure)
as SQL Server writes it. But SQL Server writes the data **once**: if the
network drops mid-backup there is nothing to resume from, and the failed
attempt is aborted server-side, never left as a truncated object.

The resilient pattern is backup-then-copy: take the backup to local disk,
then `sqlsqueeze copy` (or `--copy-to`) it to the cloud. Copies retry
through outages with growing backoff (up to 24 h), resume unfinished
multipart/block uploads without re-sending verified parts, check every
byte against the manifest, and write the manifest last — so a destination
with a backup but no manifest is an unfinished copy, safe to retry or
ignore. Rule of thumb: stream direct only on reliable networks for small
DBs; copy for everything else.


## Scheduling (SQL Agent)

Ready-made CmdExec job templates live in `tools/sql-agent/`
(`Full-Backup-Job.sql`, `Diff-Backup-Job.sql`, `Log-Backup-Job.sql`,
`Restore-Chain-Job.sql`, `Verify-Backup-Job.sql`, `Alert-Operator.sql`):
edit the four variables at the top, run on the instance. A PowerShell
module (`tools/SqlSqueeze.psm1`: `Backup-SqlDatabase`,
`Restore-SqlDatabase`, `Restore-SqlChain`, `Test-SqlBackup`, ...) wraps
the CLI with `--json` and throws on failure. Cron wrapper for Linux in
`tools/cron/`.

## Monitoring

- msdb history records every backup as `device_type 7` (virtual device);
  a dashboard view is in `tools/msdb/BackupHistory.sql`.
- Every `--json` result carries `status`, `bytesProcessed`,
  `elapsedSeconds`, `warnings[]`; non-zero exit means failed — alert on it.
- Run `test-restore` on a schedule (e.g. weekly per database): it is the
  only check that proves the data inside is healthy.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Named Pipes error 40, server not found | Wrong instance parameter — use `instancename=SQLEXPRESS`, not `instance=`. |
| `Invalid MOVE clause ... write it as MOVE='from'TO'to'` | Quotes stripped by shell layering; run from a `.ps1` file with `powershell -File`. `TO` must be uppercase. |
| Tail of the log not backed up | Restoring over a DB without `replace`; add `replace` (loses tail) or back up the log first. |
| DB left SINGLE_USER | Pre-fix behavior; current build rolls back to MULTI_USER on success and failure when `--kill-connections` was used. |
| Orphaned-users warning after restore | Expected cross-server; `ALTER USER <u> WITH LOGIN = <l>` per user. |
| Msg 3241 media family incorrectly formed | Stripe count/files/order differ from backup, or file is not a backup at all. |
| verify says decodes but no manifest | Manifest missing/deleted — only decode check possible; re-check pipeline and re-run with manifest present. |
| AES restore fails, wrong password | `passwordEnv=` variable missing/different on restore box; manifest keeps the *name*, never the value. |

## Security notes

- Secrets never go on command lines: `password=`/`secretkey=` take
  `...Env=` variants; manifests store variable names only.
- AES is CBC with per-backup salt/IV; a mid-stream bit-flip decrypts to
  garbled blocks without error (no AEAD) — pair encryption with `verify`
  or `test-restore`, and prefer `gzip`+`aes` (gzip CRC catches garbling)
  where integrity matters.
- The tool runs on the SQL box with rights to back up/restore; treat
  `C:\tools\sqlsqueeze\` like any backup-admin surface.
