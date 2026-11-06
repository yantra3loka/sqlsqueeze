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
sqlsqueeze verify file:///backups/model.bak.zst zstd
sqlsqueeze test-restore file:///backups/model.bak.zst zstd --db "db(user=sa;passwordEnv=SQL_PW)"
sqlsqueeze unpack file:///backups/model.bak.zst file:///restore/model.bak --from-manifest
sqlsqueeze backup-all USER_DATABASES,-scratch* zstd "local(path=/backups/{database}/{database}_{backuptype}_{yyyyMMdd_HHmmss}.bak.zst)" --db "db(user=sa;passwordEnv=SQL_PW)"
```

Plugins: `db` (database) · `gzip` `bzip2` `zip64` `zstd` `aes` `rate`
(pipeline) · `local` `s3` `azure` (storage). Run `sqlsqueeze help`,
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
    'C:\Backups\app_full_20261106_013000_s01of02.bak.zst',
    'D:\Backups\app_full_20261106_013000_s02of02.bak.zst')
```

That command line is
`local(path=C:\Backups\app_full_20261106_013000_s01of02.bak.zst;path=D:\Backups\app_full_20261106_013000_s02of02.bak.zst)`.

Copy-only backups need a `copy...only` marker in the file name, after
the `<db>_<type>_` prefix, on every stripe
(`app_full_copyonly_20261106_013000_s01of02.bak.zst`,
`app_full_copyonly_20261106_013000_s02of02.bak.zst`).
`Restore-SqlChain` skips those names. `Backup-SqlDatabase -CopyOnly`
warns once for each stripe whose file name lacks the marker.

WITH FORMAT is default-on at the engine level (each stripe path is
created fresh; there is no format switch). Native COMPRESSION is
replaced by the zstd/gzip transform.

The stripe count is variable — two paths, three paths, however many the
job needs. Restore must open the same stripes in the same order.
`Restore-SqlChain` (PowerShell module) groups these stripes as one restore
step, skips copy-only backups on every picker (full, diff, and logs), and
applies `-StopAt` to every selected log restore.

Option guards fail fast instead of producing invalid SQL: `PARTIAL` is
emitted once, `backuptype`/`restoretype` values are case-insensitive
(`Full`, `FULL`, `full` all work), and contradictory options throw
(`CHECKSUM`+`NO_CHECKSUM`, `STOP_ON_ERROR`+`CONTINUE_AFTER_ERROR`,
`RECOVERY`+`NORECOVERY`, `STANDBY`+`RECOVERY`/`NORECOVERY`). Empty
`STANDBY` (including quoted blanks) and empty `MOVE` endpoints are
rejected; `MOVE ... TO ...` matching is case-insensitive, so a lowercase
`to` is accepted.

Ctrl-C is graceful: the first press aborts the running backup/restore
through the normal failure path (partial files removed, database put back
to multi-user); a second press kills immediately.


## DBA start here (one page of links)

- **Download Windows / Linux builds:** [Releases](https://github.com/yantra3loka/sqlsqueeze/releases)
  (`sqlsqueeze-win-x64.zip`, `sqlsqueeze-linux-x64.tar.gz`).
- **DBA runbook** (install, backup/restore/chain/verify, scheduling,
  troubleshooting): [docs/DBA-GUIDE.md](docs/DBA-GUIDE.md).
- **LLM operator notes** (for driving it via an AI assistant):
  [AGENTS.md](AGENTS.md).
- **SQL Agent job templates:** [tools/sql-agent/](tools/sql-agent/).
- **PowerShell module:** [tools/SqlSqueeze.psm1](tools/SqlSqueeze.psm1).

## Backing up many databases

`sqlsqueeze backup-all <databases> [pipeline...] <storage> [--db "db(...)"]
[--type full|diff|log] [--no-fallback] [--json]` runs one backup per
database the first argument selects, instead of one command per database.

- **Selecting:** `USER_DATABASES`, `SYSTEM_DATABASES`, `ALL_DATABASES`,
  names, and `*`/`?` wildcards, comma separated and case-insensitive; a
  leading `-` excludes (`USER_DATABASES,-scratch*`). tempdb is never
  backed up; naming a database that does not exist is an error.
- **Paths:** `{database}` and `{backuptype}` in any storage parameter are
  replaced per database (`{database}` is required when more than one is
  selected, so backups cannot overwrite each other). Missing folders are
  created. Date tokens like `{yyyyMMdd_HHmmss}` work as before.
- **Log chains:** a differential or log backup that cannot be taken yet,
  because no full backup exists to build on, becomes a full backup (the
  result says so), or is skipped with `--no-fallback`. Log backups of
  SIMPLE-recovery databases, and any backup of a database that is not
  online, are skipped and reported. `master` is only ever backed up full.
- **Failures:** one failing database is reported and does not stop the
  rest; the exit code is `-1` if any failed. Skips are not failures.
- **Login:** `--db "db(user=sa;passwordEnv=SQL_PW;instancename=...)"` gives
  the same connection options as `backup`, without `database=`.

## Verifying a backup

`backup` writes `<file>.manifest.json` next to the backup (an object
beside it in `s3` and `azure` storage): database, backup type, the
database's files, the pipeline (secrets redacted; an
`passwordEnv=` variable name is kept, never its value), and the size and
SHA-256 of each file as stored and as SQL Server produced it. `prune`
removes a manifest together with its backup, in every storage.

`sqlsqueeze verify <storage> [pipeline...]` reads the file back through the
pipeline (decrypt, decompress) and compares sizes and both hashes with the
manifest. It needs no SQL Server and can run on any machine that has the
file. Exit `0` when everything matches, `-1` otherwise; `--json` prints one
result line. It catches corruption, truncation, a wrong password, and a
pipeline that does not hand back the stream SQL Server wrote.

Add `--sql "db(user=sa;passwordEnv=SQL_PW)"` to also have SQL Server read
the whole backup: `RESTORE VERIFYONLY` through a virtual device, so the
pipeline is undone first and compressed or encrypted backups work. That
needs the SQL Server machine (and on Linux the `mssql` user), and is skipped
when the file already failed the checks above. It catches what the hashes
cannot: a file that decodes cleanly but is not a valid backup. Add
`;CHECKSUM` to the db options to also check page checksums; that only works
on a backup taken with `checksum` (SQL Server refuses it otherwise).

Without a manifest (backups from before manifests existed, or one that was
deleted) the stream check can only confirm the file decodes, and says so,
because corruption can decode cleanly (a flipped byte in a zstd file did).

## Test restores

`verify` shows the file is the file that was written, and `verify --sql`
shows SQL Server accepts it as a backup. Neither shows the database inside
is healthy: a backup of a corrupt database is a perfect, valid backup of
corruption. `sqlsqueeze test-restore <storage> [pipeline...] --db "db(...)"`
does: it restores a full backup into a scratch database
(`sqs_testrestore_<db>_<time>`, in the server's default data and log
folders), runs `DBCC CHECKDB` on it, and drops it again. Run it from cron on
the SQL Server machine (`tools/cron/sqlsqueeze-cron.sh test-restore ...`
alerts like any other command).

- It needs the manifest next to the backup, for the database name and the
  file list recorded when the backup was taken, so only backups from this
  version on can be test-restored, and only full backups.
- The original database is never touched. It refuses to start if a database
  with the scratch name exists, and only ever drops the one it created; if
  the drop fails that is reported as a failure so the leftover gets
  cleaned up. `--keep` leaves the scratch database for you to inspect.
- `--physical-only` runs the faster `DBCC CHECKDB ... PHYSICAL_ONLY`.
- It needs free disk space for a copy of the database, and runs as the
  `mssql` user on Linux like `backup` does.
- FILESTREAM databases are supported: each FILESTREAM file is moved to its
  own folder under the server's default data folder (`<scratch>_fs`,
  `<scratch>_fs_1`, ...). The instance must have FILESTREAM enabled.
  Anything else that is neither `ROWS` nor `LOG` (e.g. `FULLTEXT`) still
  refuses with "restore this database by hand".

## Getting a native backup back

Nothing here locks you in. `sqlsqueeze unpack <storage> [pipeline...]
<native-output>` runs a backup back through its pipeline (decrypt,
decompress) and writes the plain `.bak` SQL Server produced, which
`RESTORE DATABASE ... FROM DISK` accepts with no SqlSqueeze involved. It
needs no SQL Server and checks the output against the manifest as it is
written (and removes it if anything differs). It never overwrites an
existing file or the source, and a striped backup needs one output file per
stripe.

`--from-manifest` (on `unpack`, `restore`, `verify` and `test-restore`)
takes the pipeline from the manifest next to the backup, so it does not have
to be remembered or retyped. A manifest never holds a secret, so a backup
made with a literal `aes(password=...)` cannot be read this way (the error
says so); use `passwordEnv=` and the variable name is kept.

## Restoring over a database that is in use

A restore over an existing database needs exclusive access, and SQL Server
refuses if anyone is connected. `sqlsqueeze restore ... --kill-connections`
(or `KILL_CONNECTIONS` in the db options) rolls back the other sessions
first. It is opt-in because it ends people's work, and if the restore then
fails it puts the database back to multi-user instead of leaving it
single-user.

After a restore that leaves the database usable, users whose login is not on
this server are reported as a **warning** (the restore still succeeded): the
usual result of restoring onto another server. Logins are matched by SID, not
by name, so a login recreated with the same name still leaves the user
orphaned; fix each with `ALTER USER <user> WITH LOGIN = <login>`. The warning
appears in the `--json` result as `warnings[]`. It is skipped for a verify
and while restoring in steps (`NORECOVERY`/`STANDBY`); the last log of a
chain, which brings the database online, does report it.

## Restoring the latest chain

`sqlsqueeze restore-chain <folder> <database> --db "db(...)"` restores a
database to its latest state from the backups in a folder, without you
choosing the files or retyping the pipeline. It reads the manifests under the
folder (subfolders included, so `backup-all`'s layout works), takes the
latest full, the latest differential after it and every log after that, and
restores them in order, each with the pipeline its manifest records
(`NORECOVERY` until the last step, which brings the database online).
Copy-only backups are not part of a chain.

- `--stopat "<time>"` restores what was current at that time (server local
  time): the latest full and differential before it, and the logs up to and
  including the first one taken after it, with `STOPAT` applied.
- `--as <name>` restores under another name, placing the files in the
  server's default folders (it needs the file list in the manifest;
  FILESTREAM files go to their own folders there too).
  `--replace` and `--kill-connections` apply to the first step.
- `--norecovery` leaves the database restoring for more logs later;
  `--standby <file>` leaves it readable between steps (log shipping).
  `--dry-run` prints the plan without touching the server.
- It stops at the first step that fails and says which; the database is left
  restoring so the chain can be continued once the cause is fixed. Selection
  goes by when each backup was taken, not by log sequence number, but SQL
  Server checks the LSNs as it restores, so a missing log fails at the next
  step (with SQL Server's own message) instead of restoring wrongly.
- Local folders only for now; manifests that are unreadable, or whose backup
  file is missing, are skipped with a warning.

## Copying a backup somewhere else

`sqlsqueeze copy <source-storage> <destination-storage>` copies a finished
backup, with its manifest, from one storage to another as it is stored (no
pipeline, no SQL Server): a second copy on another disk or share, or off to S3
or Azure. A backup that streams straight into cloud storage cannot be resumed
if the network drops, because SQL Server writes it only once; backing up
locally and then copying is the way to get Redgate-style network resilience.

- It **retries through outages** with a growing wait (5 seconds doubling to
  5 minutes) for up to 24 hours (`--max-hours`, `--backoff-seconds`), and
  only for faults that waiting can fix: the network, timeouts, a server that
  is down or busy. A missing file, bad credentials or a damaged source fail
  at once.
- It **checks what it read against the manifest** before anything is
  committed, so a source that has changed or been damaged since the backup is
  refused rather than copied as if it were good.
- Copying to S3 or Azure **resumes**. An attempt cut off by the network keeps
  its unfinished upload on the server (it is suspended, not discarded), and
  the next attempt, or running the command again hours later, continues it:
  the source is read again from the start (it is checked against the manifest
  as it is read) but parts the server already holds are not sent again. S3
  compares each part's MD5 with the server's ETag; Azure puts a hash of each
  block in its block id, so a part is only ever reused when its bytes are
  identical, never mixed in. The result says how much was not re-sent
  (`bytesSkipped`).
- The manifest is written **last**, so a destination holding a backup but no
  manifest is an unfinished copy. A copy that is given up, or fails for a
  reason retrying cannot fix, is aborted and its unfinished upload removed,
  never left as a truncated object. Running it again is always safe.
- Both storages need the same number of files (stripes). The copied
  manifest names the destination's files.
- Both storages need the same number of files (stripes), and a copy to the
  same place as the backup is refused.

### Copying as part of the backup: `--copy-to`

`sqlsqueeze backup ... --copy-to <storage>` (repeatable) and `backup-all ...
--copy-to <storage>` copy each backup, with its manifest, to other places
straight after taking it: a second disk or share, S3, Azure.

```text
sqlsqueeze backup [app] zstd "aes(passwordEnv=KEY)" "local(path=/backups/app_{yyyyMMdd_HHmmss}.bak)" \
    --copy-to "local(path=/mnt/nas/app.bak)" --copy-to "s3(endpoint=...;bucket=b;key=app.bak;accesskey=...;secretkeyenv=SK)"
```

- The copy finds the file by the name the backup **actually** used (date
  tokens already expanded), not by expanding them again a moment later.
- It retries through outages like `copy` (`--copy-max-hours`,
  `--copy-backoff-seconds`) and resumes S3 and Azure uploads.
- **The backup stands on its own.** If a copy fails, the backup (already
  written and verified-able) is kept, every other destination is still tried,
  the failure is reported, and the exit code is `-1` so cron and the cron
  webhook notice. Retry just the copy with `sqlsqueeze copy`.
- With `backup-all`, put `{database}` (and `{backuptype}`) in each
  `--copy-to` path, as in the primary one; selecting several databases into a
  copy-to path without `{database}` is refused before anything is backed up.

## Checking your own cloud account

`tools/cloud-conformance/run.sh` runs the storage scenarios against any
S3-compatible service (AWS, Backblaze B2, Cloudflare R2, Wasabi, MinIO, Ceph...)
or Azure Blob account and prints a pass/fail line for each. It needs no SQL
Server, only an existing bucket or container and credentials that can list,
read, write and delete in it. It writes under its own random prefix, moves
about three times `SQS_BIG_MB` (default 64) of data, and removes everything it
created:

```bash
SQS_APP=/path/to/sqlsqueeze SQS_S3_ENDPOINT=https://s3.us-east-1.amazonaws.com \
  SQS_S3_BUCKET=my-test-bucket SQS_S3_REGION=us-east-1 \
  SQS_S3_ACCESSKEY=... SQS_S3_SECRET=... tools/cloud-conformance/run.sh s3

SQS_APP=/path/to/sqlsqueeze SQS_AZ_ENDPOINT=https://myaccount.blob.core.windows.net \
  SQS_AZ_ACCOUNT=myaccount SQS_AZ_CONTAINER=my-test-container \
  SQS_AZ_KEY=... tools/cloud-conformance/run.sh azure
```

If you run it against a real account, a pass (or a failure, with the output) is
exactly what the project is missing: please open an issue with the provider and
the last line of output.

## Scripting

- `--json` on backup/restore/prune: one result object on stdout, exit
  `0`/`-1`. See [tools/README.md](tools/README.md) for the contract.
- [PowerShell module](tools/SqlSqueeze.psm1): `Backup-SqlDatabase`,
  `Restore-SqlDatabase`, `Remove-SqlBackup`, `Test-SqlBackup`,
  `Test-SqlSqueezeBackup`, `Restore-SqlChain`.
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

## Roadmap

The aim is a backup tool that is **better than Redgate SQL Backup Pro and costs
nothing**: free to use, and free to build, test and release (free GitHub tiers,
free SQL Server editions, free emulators), with every "better" claim published
only after it has been measured. The full roadmap, with what is done, what comes
next and why, and what needs the owner's decision, is in
**[ROADMAP.md](ROADMAP.md)**. In short, what is built and tested: compressed and encrypted backups through VDI on Windows and
Linux; full, differential, log and multi-database backups; `verify` (hashes and
SQL Server's own `RESTORE VERIFYONLY`) and `test-restore` with `DBCC CHECKDB`;
manifests, `unpack` and `--from-manifest`; `restore-chain`, `--kill-connections`
and orphan warnings; S3 and Azure storage; `copy` and `--copy-to` with retry
and resume. What comes next is mostly about trust: running real SQL Server in free CI on
Windows and Linux across versions, S3 and Azure conformance without paid
accounts, and authenticated encryption. After that, the features meant to put
it ahead: automatic proof-of-recovery drills, public-key encryption with
immutable destinations so a compromised backup host cannot read or delete its
backups, RPO monitoring, and policy as code.

Non-goals: a GUI for now (the engine, scripts and docs slot into the Ola/dbatools
setups DBAs already run); anything that only works untested.

## Verification status

- `dotnet build -c Release`: 0 warnings, 0 errors (Linux).
- CLI `version` / `listplugins` / `help`: pass.
  (`sqlsqueeze v1.3.0.0 (.NET 10.0.13, X64)`, all 8 plugins found.)
- xUnit suite: **370/370 pass** after merging the roadmap and DBA tests — database selection, full/diff/log
  planning and the `backup-all` runner (fakes, no SQL Server), data
  source and `@@VERSION` parsing; manifest, verify (intact, flipped byte,
  truncated, wrong password, striped, no manifest) and every ordered pair
  of pipeline stages written through the real pipeline path; manifest-aware
  prune; pipeline round-trips (gzip, bzip2,
  zip64 named + first, zstd, rate, local, gzip→aes chain), AES (env key,
  random salt, wrong-password/truncated/missing-key failures, KDF golden
  vector), prune (age/pattern/defaults/validation, including S3 and Azure
  listings that end with an empty `NextContinuationToken`/`NextMarker`),
  date tokens, backend selection, restore byte counting, SQL login
  options, write retries, S3 and Azure storage against mocked services.
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
- Live in CI on free GitHub runners (2026-10-26): **PASS**. SQL Server 2022 on
  Ubuntu 22.04 and SQL Server 2025 on Ubuntu 22.04 and 24.04, each installed
  natively, run 25 end-to-end checks (`tools/live-test/live.sh`: backup
  types, restore, `verify --sql`, `test-restore`, `unpack`, `restore-chain`,
  `backup-all`). The first run found that SQL Server 2025 on Linux could not take
  a backup (`undefined symbol: uuid_generate`); the shim now links libuuid. The
  same 25 checks run on Windows too, against SQL Server 2025 Express (script run
  under Git Bash; passed 25/25 on 2026-10-26), so the Windows build of these
  commands is now tested against a real server on every change. Not covered: SQL
  Server 2019, and Windows with older SQL Server versions.
- Live against SQL Server 2022 on Linux (RTM-CU27, 16.0.4295.3,
  2026-10-26): **PASS**. A 20,000-row database backed up through the
  `native/linux` shim and restored (also with `MOVE` to a new database)
  with identical checksums. The first run exposed a heap corruption at
  the end of a working backup: the shim deleted the device object, which
  the device set owns. Fixed in `05e7aee`.
- S3 and Azure, cloud conformance in CI (2026-10-26): **PASS** against
  SeaweedFS 4.50 and RustFS 1.0.4 (two independent S3 servers that check
  signatures), moto 5.2.4, and Azurite 3.37.0, 25 scenarios each
  (`tools/cloud-conformance/run.sh`): multipart uploads, a part-aligned and a
  one-byte object, names with spaces, non-ASCII, `+ & % #`, overwrite,
  integrity checked in place, bit-identical download, a copy killed half way and
  resumed, a wrong secret failing cleanly, and prune. **This corrects the
  entry below.** moto and the unit-test fakes do not check S3 signatures, so
  until this run the `s3` plugin had never been shown to work against a server
  that does: its requests were missing the `x-amz-content-sha256` header AWS
  requires, and a query string containing a slash was encoded twice, so
  v1.6.1 and earlier were very likely rejected by real AWS S3 and by other
  strict S3 services. Azure blob names containing `%`, `#` or similar were
  stored under a different name or failed. All fixed in v1.6.2; the signer now
  reproduces the signature AWS prints in its own documentation example. Still
  not run against a real AWS or Azure account: anyone with one can run the
  script against their own bucket (see below).
- S3 and Azure storage, live against emulators (2026-10-26): **PASS**
  (superseded by the entry above for S3).
  `s3` against moto: 6 MB multipart round trip byte-correct, prune
  verified. `azure` against Azurite: 3 MB in 3 blocks, SHA-256 identical
  after download. Not yet run against real S3 or Azure. Live runs found
  two real bugs that mocks missed: an Azure SharedKey canonical-resource
  signing error, and a prune loop that never ended because both services
  send an empty continuation element on the last page (fixed in
  `31099ca`).
- `verify` and manifests, live against SQL Server 2022 on Linux
  (2026-10-26): **PASS**. A 20,000-row database backed up with `zstd` and
  with `zstd` + `aes`; `verify` passed both against their manifests, failed
  a wrong AES key, failed a copy with one flipped byte (zstd decoded it
  without error, only the hashes caught it), and said so when the manifest
  was missing. The `zstd` + `aes` backup restored into a new database with
  identical row count and checksum.
- `backup-all`, live against SQL Server 2022 on Linux (2026-10-26):
  **PASS**. Five scratch databases (full recovery, simple recovery, new
  with no backup, offline, excluded) selected with `sqs_all_*`, so the
  instance's other databases were not touched. Full: three backed up into
  per-database folders with manifests, the offline one skipped, the excluded
  one untouched. Log: full-recovery databases got log backups, the simple one
  and the offline one were skipped, the new one fell back to a full with the
  reason reported; `--no-fallback` skipped it instead. Differential and
  fallback-to-full worked. With one database's folder made unwritable the
  others still completed and the exit code was `-1`. All 11 backups then
  passed `verify`.
- `verify --sql`, live against SQL Server 2022 on Linux (2026-10-26):
  **PASS**. `RESTORE VERIFYONLY` through VDI accepted `zstd` + `aes`
  backups (with and without page checksums, and `;CHECKSUM` on the one
  that had them); it refused `;CHECKSUM` on a backup without checksums
  with SQL Server's own message; it rejected a gzip file that decodes
  cleanly but is not a backup ("media family incorrectly formed"); and it
  was skipped for a file that had already failed the hash checks.
- `test-restore`, live against SQL Server 2022 on Linux (2026-10-26):
  **PASS**. A healthy database with an extra data file was backed up with
  `zstd` + `aes`, restored into a scratch database, checked and dropped
  (the manifest had recorded all three files). A second database with a
  page deliberately corrupted (`DBCC WRITEPAGE`, scratch database only)
  was backed up the same way: `verify` and `verify --sql` both passed it,
  as they should (it is a faithful backup of the corruption), and
  `test-restore` failed it with `DBCC CHECKDB`'s page-level errors, then
  dropped the scratch database. No scratch databases or files were left.
  It refuses a backup with no manifest and a log backup, with a clear
  message.
- `unpack` and `--from-manifest`, live against SQL Server 2022 on Linux
  (2026-10-26): **PASS**. A `zstd` + `aes(passwordEnv=)` backup was unpacked
  with `--from-manifest` into a native `.bak`, and a plain
  `RESTORE DATABASE ... FROM DISK` run with `sqlcmd` alone restored it with
  an identical row count and checksum. `restore --from-manifest` through VDI
  gave the same result. `unpack` refused to overwrite an existing file, and
  a corrupted copy failed and left no output behind. A backup made with a
  literal password has no secret in its manifest (0 occurrences) and
  `--from-manifest` refused it with a clear message.
- **A failed backup to S3 or Azure left a truncated object that looked like a
  backup.** The cloud writers completed their upload when the pipeline
  disposed them, even when the backup had failed part-way (SQL Server
  aborting, or the pipeline breaking). Live against moto and Azurite, a
  backup of a database with a corrupted page, taken with `CHECKSUM`, failed
  as it should, but the previous release left `db/old.bak` behind in both
  stores (657,746 bytes in S3, against about 2.5 MB for a good backup) while
  the new release left nothing. Writers now implement `IAbortableStream`:
  the pipeline aborts them before disposing on any failure, a failed write
  marks them aborted, writes after an abort are discarded, and S3 deletes the
  multipart upload. Any object that failed backups left behind in cloud
  storage should be deleted; a good backup always has a manifest beside it.
- `--copy-to`, live against SQL Server 2022 on Linux, moto and Azurite
  (2026-10-26): **PASS**. One `backup` command with a date-stamped primary
  name and three `--copy-to` destinations (a folder, S3, Azure) took the
  backup and left three copies that each verified from their own manifest.
  `backup-all` with `{database}` in the primary and copy-to paths copied each
  database to its own place in S3, and a restore test ran straight from one
  of those S3 copies. A destination that could not be reached gave up after
  four attempts: the backup was intact and verified, the other copy was still
  made, and the exit code was non-zero. Several databases into one copy-to
  path were refused before anything was backed up.
- Resuming a copy, live against moto and Azurite (2026-10-26): **PASS**. A
  ~56 MB backup copied through a proxy that cut the connection once after
  about 28 MB. To S3 (5 MB parts): attempt 1 died mid-upload, attempt 2
  continued the same multipart upload and did not re-send 25 MB (five
  parts); the object verified from its manifest and no upload was left
  open. To Azure (4 MB blocks): the same, 24 MB not re-sent; the blob
  verified, unpacked, and restored with plain `sqlcmd` to an identical
  row count and checksum.
- `copy`, live against moto and Azurite (2026-10-26): **PASS**. A `zstd` +
  `aes` backup copied to S3 verified from its own manifest with no pipeline
  typed. With Azurite stopped, a copy to Azure failed three times with
  "Connection refused" (waiting 2, 4 and 8 seconds), succeeded on the fourth
  attempt after Azurite came back, and the copy verified. A source with one
  flipped byte was refused after one attempt and left nothing in the bucket.
- `restore-chain`, live against SQL Server 2022 on Linux (2026-10-26):
  **PASS**. A chain of one full, one differential and three log backups
  (data inserted between each, all `zstd` + `aes`) was restored from the
  folder with no file or pipeline named: five steps in order, 18,000 rows,
  identical checksum to the source. `--stopat` a moment before the last
  batch restored 15,000 rows and left that batch out. `--norecovery` left
  the database `RESTORING`; `--standby` left it `ONLINE` with
  `is_in_standby=1` and readable. With the middle log deleted, it stopped
  at the next log with SQL Server's LSN error, did not run the last step,
  exited non-zero and left the database `RESTORING`. `--dry-run` printed the
  plan with no SQL Server involved.
- `--kill-connections` and orphaned-user warnings, live against SQL Server
  2022 on Linux (2026-10-26): **PASS**. With another session open in the
  database, a restore over it failed with SQL Server's "exclusive access
  could not be obtained"; with `--kill-connections` it ended that session
  and succeeded. A restore that failed after the sessions were ended (the
  file was not a backup) left the database `ONLINE`/`MULTI_USER` with its
  data. Restoring a backup onto a copy after the login was dropped reported
  the orphaned user in both the JSON `warnings` and the human output, and a
  user whose login still matched by SID was not reported.
- Manifests for `s3` and `azure`, live against moto and Azurite
  (2026-10-26): **PASS**. A `zstd` + `aes` backup of a SQL Server 2022
  database written to each store left the backup and `<key>.manifest.json`
  beside it. Against each store, `verify --from-manifest`, `verify
  --from-manifest --sql`, `unpack --from-manifest` (the output restored with
  plain `sqlcmd` to an identical row count and checksum), and
  `test-restore --from-manifest` all passed with no pipeline typed, and
  `prune` removed each backup together with its manifest. Real S3 and Azure
  accounts have still not been tried.
- **The VDI library prints an abort notice after the CLI's JSON line.**
  When SQL Server aborts a VDI operation, `libsqlvdi` writes a line like
  `Host_…_SQLVDIMemoryName_0: CVD::GetCommand: Abort bit is set` to stdout
  after the result, so "the last line" is not always the result. The cron
  wrapper and the PowerShell module took the last line, so a failed backup
  alerted with that notice as its message and a successful run could not
  be parsed. They now take the last line that is a JSON object, and CI runs
  tests for both (`tools/tests/`).
- **Differential backups failed on patched SQL Server builds.** The
  `@@VERSION` parser took the first `-` in the string, which on builds like
  `2022 (RTM-CU27)` or `2022 (RTM-GDR)` sits inside the parentheses, so a
  differential backup stopped with "unexpected version string". Fixed;
  covered by tests with real version strings.
- **Known fix for old backups:** before this change `zstd` did not close the
  stage after it, so any pipeline with a stage *after* `zstd` (for example
  `zstd aes(...)`) never flushed that stage and wrote a truncated,
  unrestorable file. `zstd` alone and `gzip aes(...)` were not affected.
  Found by `verify`; take new backups of anything made with
  `zstd` followed by another stage and do not trust the old ones.
- Other fixes from the same work: `local` storage opened backups for
  reading with write access, so another user's or read-only backup could
  not be restored or verified; and gzip, bzip2, zip64 and zstd rejected
  `level=` on restore, so one pipeline string could not be used for both
  backup and restore.
