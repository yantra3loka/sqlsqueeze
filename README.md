# SqlSqueeze (formerly MSSQL Compressed Backup)

[![CI](https://github.com/yantra3loka/sqlsqueeze/actions/workflows/ci.yml/badge.svg)](https://github.com/yantra3loka/sqlsqueeze/actions/workflows/ci.yml)

Open-source SQL Server backup compression via the Virtual Backup Device
Interface (VDI) — the same technique Redgate SQL Backup Pro uses, which is
why this works on editions (like Web/Express) where native
`BACKUP ... WITH COMPRESSION` is blocked.

Runs on **Windows and Linux** (`net10.0`, AnyCPU; SQL Server is 64-bit only
on both).

```text
sqlsqueeze backup [model] zstd file:///backups/model.bak.zst
sqlsqueeze backup [mydb] gzip aes(passwordEnv=BACKUP_KEY) file:///backups/mydb.bak.gz.aes
sqlsqueeze restore file:///backups/model.bak.zst zstd [model_restored]
sqlsqueeze prune "local(dir=/backups;pattern=*.zst;olderThanDays=30)" --json
```

Plugins: `db` (database) · `gzip` `bzip2` `zip64` `zstd` `aes` `rate`
(pipeline) · `local` (file storage). Run `sqlsqueeze help`,
`sqlsqueeze listplugins`, `sqlsqueeze helpplugin <name>` for details.

## Striped (parallel multi-file) backup

One database, N files in parallel — the equivalent of T-SQL
`BACKUP DATABASE [MyDatabase] TO DISK='...Part1.bak', DISK='...Part2.bak',
...`. Give `local` one `path=` per stripe; each path becomes one VDI
device with its own thread:

```text
sqlsqueeze backup "db(database=MyDatabase)" zstd "local(path=D:\Backups\MyDatabase_Part1.bak.zst;path=D:\Backups\MyDatabase_Part2.bak.zst;path=D:\Backups\MyDatabase_Part3.bak.zst)"
```

From the PowerShell module, `-Path` takes one string or a string array.
One path emits the same `local(path=...)` argument as before. Several
paths are one backup striped across drives — use this when the database
does not fit on a single volume (here, `C:` and `D:`):

```powershell
Backup-SqlDatabase -Database app -Path @(
    'C:\Backups\app_full_p1.bak.zst',
    'D:\Backups\app_full_p2.bak.zst')
```

That command line is
`local(path=C:\Backups\app_full_p1.bak.zst;path=D:\Backups\app_full_p2.bak.zst)`.

Copy-only backups need a `copy...only` marker in the file name, after
the `<db>_<type>_` prefix, on every stripe
(`app_full_copyonly_p1.bak.zst`, `app_full_copyonly_p2.bak.zst`).
`Restore-SqlChain` skips those names. `Backup-SqlDatabase -CopyOnly`
warns once for each stripe whose file name lacks the marker.

WITH FORMAT is default-on at the engine level (each stripe path is
created fresh; there is no format switch). Native COMPRESSION is
replaced by the zstd/gzip transform.

The stripe count is variable — two paths, three paths, however many the
job needs. Restore must open the same stripes in the same order.
`Restore-SqlChain` (PowerShell module) skips copy-only backups on every
picker (full, diff, and logs) so a copy-only file can never anchor or
extend a chain, and `-StopAt` attaches only to the final log restore.

Option guards fail fast instead of producing invalid SQL: `PARTIAL` is
emitted once, `backuptype`/`restoretype` values are case-insensitive
(`Full`, `FULL`, `full` all work), and contradictory options throw
(`CHECKSUM`+`NO_CHECKSUM`, `STOP_ON_ERROR`+`CONTINUE_AFTER_ERROR`,
`RECOVERY`+`NORECOVERY`, `STANDBY`+`RECOVERY`/`NORECOVERY`). Empty
`STANDBY` (including quoted blanks) and empty `MOVE` endpoints are
rejected; `MOVE ... TO ...` matching is case-insensitive, so a lowercase
`to` is accepted.

## Scripting

- `--json` on backup/restore/prune: one result object on stdout, exit
  `0`/`-1`. See [tools/README.md](tools/README.md) for the contract.
- [PowerShell module](tools/SqlSqueeze.psm1): `Backup-SqlDatabase`,
  `Restore-SqlDatabase`, `Remove-SqlBackup`, `Test-SqlBackup`.
- [SQL Agent templates](tools/sql-agent/) (CmdExec, Windows + Linux) and
  the [msdb dashboard view](tools/msdb/BackupHistory.sql).

## Provenance

Upstream: [MSSQL Compressed Backup](https://mssqlcompressed.sourceforge.net/)
by Clay Lenhart, mirrored here from SourceForge Mercurial (90 changesets,
2008–2011, tags `v1.1-*`/`v1.2-*`) plus the 1.2 release tree.
License: GPL (see [COPYING.txt](COPYING.txt); per-file headers apply).
Upstream is abandoned — this repo keeps it building and running on modern
.NET and SQL Server under the SqlSqueeze name.

## What changed

- Targets `net10.0`, OS-agnostic. Must run **on the SQL Server machine**
  (VDI is local-only) with the .NET 10 runtime.
- `MSBackupPipe.Vdi` (C# VDI interop, `VdiNet.VirtualBackupDevice`
  namespace) with two backends picked at runtime:
  - **Windows**: COM into `sqlvdi.dll` (ships with SQL Server).
  - **Linux**: P/Invoke into `native/linux/libmsbpvdi.so` (build it with
    `make` in `native/linux` — needs g++ and
    `/opt/mssql/lib/libsqlvdi.so` from SQL Server on Linux 2017+),
    because the Linux VDI API is C++ classes. Run as the `mssql` user
    (POSIX shm/sem permissions — see `native/linux/README.md`).
    Named instances don't exist on Linux: `instancename` is ignored there.
- Handles the `VDC_Complete` command SQL Server 2016+ sends at end of
  stream (KB3188454); other unknown commands are acknowledged
  `NotSupported` and skipped, mirroring Microsoft's own VDI sample —
  the 2008-era code aborted the whole backup on either.
- Completion codes are Win32 codes per the VDI reference
  (`Success=0`, `HandleEof=38/ERROR_HANDLE_EOF`,
  `DiskFull=112/ERROR_DISK_FULL`) — confirmed against Microsoft's
  [Linux VDI headers](https://github.com/Microsoft/sql-server-samples/tree/master/samples/features/sqlvdi-linux).
- `System.Data.SqlClient` → `Microsoft.Data.SqlClient` NuGet.
  Connection string drops obsolete `Asynchronous Processing=true` and adds
  `TrustServerCertificate=true` (new driver encrypts by default; matches
  old out-of-box local behavior). `SqlThread` uses `ExecuteNonQueryAsync`.
- SharpZipLib 0.85 DLL → SharpZipLib NuGet. Two real fixes from the
  upgrade: `GZipOutputStream` ctor no longer takes the level (uses
  `SetLevel`), and `Close()` inside `Dispose(bool)` recursed forever.
- `Restore` read loop keeps reading to the full VDI buffer instead of a
  single short `Stream.Read` (partial reads used to fake EOF).
- Dropped: `ReleasePackaging` (use `dotnet publish`), strong-name `.snk`
  signing, `Binaries/` redist DLLs, legacy FxCop suppressions.

## Download

Grab the latest `sqlsqueeze-win-x64.zip` / `sqlsqueeze-linux-x64.tar.gz`
from [Releases](https://github.com/yantra3loka/sqlsqueeze/releases) —
self-contained, no .NET install needed. (Linux: also build
`native/linux/libmsbpvdi.so` on the SQL box, see below.)

## Build / run (Windows or Linux)

```bash
dotnet build SqlSqueeze.slnx -c Release
dotnet test SqlSqueeze.slnx -c Release
dotnet SqlSqueeze.Cli/bin/Release/net10.0/sqlsqueeze.dll version
dotnet publish SqlSqueeze.Cli -c Release -o out
```

Linux additionally: build `native/linux/libmsbpvdi.so` (see above) and
place it next to the published binary or in `/opt/mssql/lib`.

## Roadmap — making it a true Redgate replacement

Done so far: .NET 10 port · Linux backend · `aes` + `zstd` plugins ·
`prune` retention · `--json` scripting API · PowerShell module · SQL Agent
templates · msdb dashboard · CI + binary releases.

Still to do, in order:

1. **Live validation (the gate for everything).** Real `backup` +
   `restore` on Windows SQL 2019/2022 and Linux SQL 2022, including
   striped, differential, and log chains. Nothing here is
   production-claimed until this passes.
2. **Cloud storage plugin.** S3-compatible + Azure Blob destinations.
   Needs real cloud targets to verify against — untested backup code is
   worse than none, so this waits for them.
3. **Scheduled verify jobs.** `RESTORE VERIFYONLY` Agent template +
   `Test-SqlBackup` wired into the sample schedules.
4. **Log-shipping helpers.** Restore-chain scripts (full → diff → logs
   with NORECOVERY/STANDBY) built on the Agent templates.
5. **Alerting example.** Email/webhook on nonzero exit for Agent and
   cron/Task Scheduler setups.
6. **Network resilience.** Redgate retries through dropped shares; a
   failed write today fails the job. Retry-with-backoff on storage
   writes (and resume-friendly chunking for large files).

Non-goals: a GUI (the engine + scripts + docs slot into the Ola/dbatools
setups DBAs already run); anything that only works untested.

## Verification status

- `dotnet build -c Release`: 0 warnings, 0 errors (Linux).
- CLI `version` / `listplugins` / `help`: pass.
  (`sqlsqueeze v1.3.0.0 (.NET 10.0.13, X64)`, all 8 plugins found.)
- xUnit suite: **90/90 pass** — pipeline round-trips (gzip, bzip2,
  zip64 named + first, zstd, rate, local, gzip→aes chain), AES (env key,
  random salt, wrong-password/truncated/missing-key failures, KDF golden
  vector), prune (age/pattern/defaults/validation), date tokens, backend
  selection, restore byte counting, plus `DatabaseSource` option guards
  (single `PARTIAL`, case-insensitive backup/restore types, contradictory
  `WITH` combos throw, `MOVE`/`STANDBY` validation).
- Backend selection on Linux without SQL Server: factory picks the Linux
  backend and fails with the friendly missing-`libmsbpvdi.so` error:
  **PASS**. Struct/enum values asserted: **PASS**.
- Live against SQL Server 2025 Express (Windows VM, 2026-10-25): **PASS**.
  50k-row database: zstd full (13.9 MB → 1.0 MB, 0.4 s) and gzip+aes
  full restored with identical row counts + checksums; differential,
  log, and full→diff / full→log NORECOVERY chains all restore
  byte-correct (51k rows incl. chained diff). msdb history records every
  backup as `device_type 7` (virtual device). Restore order is the same
  as backup order (each layer inverts itself); VERIFYONLY/HEADERONLY
  reject compressed files by design, like Redgate `.sqb` files.
- Live against SQL Server on Linux: **not yet run** — the Windows and
  Linux VDI paths share everything above the backend, but the shim
  (`native/linux`) still needs its first real backup.
