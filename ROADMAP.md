# SqlSqueeze roadmap

**The goal: a SQL Server backup tool that is better than Redgate SQL Backup Pro,
and costs nothing.** Redgate SQL Backup Pro is a paid, proven product with a GUI
and support; "better" has to be earned. This file says how we will earn it, what
is built, what comes next and why, and what needs a decision from the owner.

It has no dates. Items are ordered by how much they move a DBA from "interesting"
to "I trust this with production", and then to "I would not go back".

## Two rules that shape everything

**1. Zero cost, for everyone, always.**
- Nothing in the product needs a paid service, a license server, a subscription
  or a paid dependency. Every dependency is open source and compatible with the
  GPL. No telemetry, no phone-home.
- Building, testing and releasing cost the project nothing: GitHub's free tiers
  for a public repository (Actions, Releases, Pages, the container registry),
  SQL Server's free editions (Developer for testing, Express), and free
  emulators and servers (Azurite for Azure Blob; SeaweedFS, RustFS and moto for S3).
- Where something genuinely cannot be free (a real AWS or Azure account), it is
  **opt-in and run by whoever has an account**, through a published conformance
  script, with the results recorded in the README. The project never depends on
  one.
- Distribution is free too: GitHub Releases, then community package indexes
  (winget, Homebrew, Chocolatey, apt/rpm via GitHub-hosted repos) and a
  container image on the GitHub registry. Code signing uses programs that are
  free for open source (SignPath Foundation for Windows, Sigstore for the rest).
- What users still pay for is what they already pay for: SQL Server licenses and
  whatever storage they choose.

**2. "Better" is a claim we only make after measuring it.**
- A feature that has not run against a real SQL Server is listed as not done,
  whatever the unit tests say (the README's *Verification status* lists exactly
  what was run, and on what).
- Every "better than Redgate" statement needs a reproducible measurement or a
  checkable property, published with how to repeat it. We cannot run Redgate
  here, so speed and size are compared against SQL Server's own native
  compression and against Redgate's published figures, with a harness anyone who
  has Redgate can run. Until then the claim stays off the page.

## Where we are

Compared with SQL Backup Pro (features as Redgate documents them; versions vary,
so check Redgate's current docs before quoting a limit):

| Area | Redgate SQL Backup Pro | SqlSqueeze today |
|---|---|---|
| Compression | levels, a compression analyzer | **Done:** gzip, bzip2, zip64, zstd (levels 1–22). **Not yet:** analyzer or benchmark, multi-threaded compression |
| Encryption | AES 128/256 | **Partly:** AES-256-CBC with PBKDF2, not authenticated (see *Next* 3) |
| Backup types | full, differential, log, striping, mirror/copy-to | **Done:** full, differential, log, striping, copy-only, multi-database `backup-all`, `--copy-to` |
| Verification | CHECKSUM, `RESTORE VERIFYONLY`, scheduled test restore with `DBCC CHECKDB` | **Done:** manifest hashes (`verify`), SQL Server's `RESTORE VERIFYONLY` (`verify --sql`), `test-restore` with `DBCC CHECKDB`. Scheduling is cron/SQL Agent, not built in |
| Restore | point in time, kill connections, orphaned users, restore chains | **Done:** `restore-chain` (point in time, standby), `--kill-connections`, orphan warnings, `unpack` to a native `.bak`. **Not yet:** chains from cloud storage, by LSN |
| Cloud | Azure, S3 and S3-compatible, Backblaze, object lock, immutable containers | **Done against emulators:** S3-compatible and Azure Blob with resumable copies. **Not yet:** real-account results, Backblaze/GCS, object lock, managed identities |
| Network resilience | retries, resumes, log copy queue up to 24h | **Done:** `copy` and `--copy-to` retry for up to 24h and resume S3/Azure uploads. A backup streaming *directly* into the cloud cannot resume (SQL Server writes it once), so back up locally then copy |
| Log shipping | wizard, self-healing queues | **Pieces exist** (`backup`, `copy`, `restore-chain --standby`); **no orchestration or lag monitoring** |
| Alerts and reports | email, activity history, reports | **Partly:** failure alerts via SQL Agent operator or cron webhook, msdb history view. **No** built-in email, metrics or report |
| Management | GUI, central management of many servers | **No GUI, no central management.** CLI, PowerShell, templates |
| Platforms | Windows | **Windows and Linux.** Linux needs the VDI shim built on the SQL box |
| Cost | paid per server | **Free, and the whole stack to build and test it is free** |

Where SqlSqueeze already differs: it is open source, it runs on Linux
(Redgate's documentation does not mention Linux), every backup carries a
manifest, and a backup can always be unpacked to a plain native `.bak`, so there
is no lock-in.

## Done (shipped)

- **v1.5.0:** `verify`, `verify --sql`, `test-restore`, manifests (local, S3,
  Azure), `unpack`, `--from-manifest`, `backup-all`, `restore-chain`,
  `restore --kill-connections` and orphan warnings, `copy` with retry and
  resume; fixes for unrestorable `zstd`+`aes` backups and truncated cloud
  objects from failed backups.
- **v1.6.0:** `--copy-to` on `backup` and `backup-all`.
- **v1.6.1:** SQL Server 2025 on Linux (the shim links libuuid); live CI on Linux
  and Windows.
- **v1.6.2:** S3 and Azure fixes found by the cloud conformance run (S3 request
  signing, Azure blob names); the conformance script and its CI.

## Next: make it trustworthy (and free to keep trustworthy)

These come first because they decide whether anyone can rely on the rest, and
because most of them are what turns "tested once by hand" into "tested on every
change, for free".

1. **Run real SQL Server in CI, for free.** *Linux half done (2026-10-26):*
   `.github/workflows/live-sql.yml` installs SQL Server natively on a free runner
   and runs 25 live checks (`tools/live-test/live.sh`) against 2022 on
   Ubuntu 22.04 and 2025 on 22.04 and 24.04, plus SQL Server 2025 Express on
   Windows (the same script, run under Git Bash), on every push and pull request
   and weekly. It already paid for itself: SQL Server 2025 on Linux could not take a
   backup at all until the shim linked libuuid. SQL Server 2019 (its package needs
   `libldap-2.4`, which Ubuntu 22.04 no longer ships) and 2022 on Ubuntu 24.04
   (installs but will not start) cannot run on a free runner. *Still to do:* Windows
   with older SQL Server versions, and 2019 on Linux through a container image
   whose OS Microsoft supports.
   Original description: the live tests had been run by
   hand on one machine. GitHub's hosted runners are free for public repositories
   (confirm the current terms), and SQL Server Developer edition is free for
   testing. *Done when:* a CI job installs SQL Server natively on a Linux runner
   (VDI needs a native install, not a separate container; this is the part to
   prove first) and on a Windows runner, builds the shim, and runs the live
   scenarios (backup, `verify --sql`, `test-restore`, `unpack`, `restore-chain`,
   `backup-all`) on every change or nightly, across SQL Server 2019, 2022 and
   2025 where the packages exist. This one item closes the "never run on
   Windows, never run across versions" gap at no cost, and keeps it closed.
2. **Cloud conformance without paying.** *Mostly done (2026-10-26):*
   `tools/cloud-conformance/run.sh` (25 scenarios, no SQL Server needed) runs in CI
   against SeaweedFS and RustFS (signature-checking S3 servers), moto and Azurite,
   on every change and weekly, and anyone can run it against their own AWS, Azure
   or S3-compatible account. Its first run found that S3 requests were being
   signed wrongly (rejected by every server that checks signatures, so very likely
   by real AWS) and that Azure mangled some blob names; both fixed in v1.6.2.
   MinIO, named in the first draft, no longer publishes its official container
   image, so it is not used. *Still to do:* a pass recorded against a real AWS S3
   bucket and a real Azure account by someone who has one (the script exists; the
   README says how), and more S3 servers if they are easy to run for free (Garage,
   Ceph).
3. **Authenticated encryption, then public-key encryption.** `aes` is
   AES-256-CBC with no integrity check, so a modified ciphertext is not detected
   by the cipher itself (`verify` catches it via the manifest; a plain `restore`
   does not). *Done when:* a new versioned format uses authenticated encryption
   (AES-GCM or encrypt-then-HMAC), old CBC backups still restore, a key file works
   in addition to an environment variable, and the docs cover key handling and
   rotation. Then public-key encryption (see *Later* 8).
4. **Fix the parameter validation bug.** The "required" flag on plugin parameters
   is never enforced (a typo in `ParameterInfo`), so a missing required option
   fails late with a confusing error. *Done when:* required parameters are
   checked up front with a clear message, with tests for every plugin, and the
   release notes say scripts that relied on the leniency will now fail early.
5. **Make a bad restore harder.** *Done when:* `restore` can check the backup
   against its manifest first (`--verify-first`), and cloud `prune` reports the
   bytes it reclaimed (it reports 0 today).
6. **Robustness from the 2009 todo list that still applies.** Ctrl-C should abort
   a backup cleanly (and abort cloud uploads), a locked database should get a
   longer device and execution timeout, and overwriting an existing backup file
   should be an explicit choice. *Done when:* each has a test and a line in the
   docs. (`todo.txt` is the original upstream list; what is still relevant is
   here.)

## Later: where we get ahead of Redgate

Each of these is something we can build with free tools, and each states how we
would show it is better.

7. **Proof of recovery, automatically.** `test-restore` already proves one backup.
   Make it a routine: a scheduled restore drill per database that records the
   result next to the backup, and a plain HTML/JSON report ("every database
   restored cleanly in N minutes, last drill on …"). *Better because:* a backup
   you have restored is worth more than one you have verified, and the report is
   something you can hand an auditor. *Shown by:* a drill that fails on a
   deliberately corrupted backup and passes on a good one, in CI.
8. **Backups the backup server cannot read.** Public-key encryption: encrypt to a
   public key and keep the private key elsewhere, so a compromised backup host
   cannot decrypt its own backups. Together with write-only and immutable
   destinations (S3 object lock, Azure immutable containers, both testable
   against free servers) this is ransomware resistance by design, which a tool
   that has to hold the password on the backup host cannot offer. *Shown by:* a documented attack scenario and
   a test that the backup host's own credentials cannot read or delete old
   backups.
9. **Monitoring that states the promise.** A backup-age report, success and
   failure notifications (built-in SMTP so the cron route needs no Database Mail),
   and Prometheus metrics with a Grafana dashboard (both free), plus an RPO check:
   "alert if any database could lose more than N minutes", not just "a job
   failed". *Shown by:* an induced missed log backup raising an RPO alert without
   SQL Agent.
10. **Policy as code.** One job file describes a server's whole policy
    (databases, types, destinations, copy-to, retention, verify and test-restore
    schedule), with a dry run, and generates systemd timers, cron or SQL Agent
    jobs, or runs them. It lives in git like the rest of your infrastructure.
    *Shown by:* one file reproducing a real server's backup setup from scratch.
11. **Log shipping as one thing.** A tested `log-ship` flow (log backup, copy,
    restore on the secondary in standby, retry, lag report). *Shown by:* a primary
    and secondary running a day with an induced outage and catching up on their
    own, and an alert when lag passes a threshold.
12. **Faster and smaller, measured.** A `benchmark` command that compresses a
    sample of your own database with every plugin and level and reports ratio and
    speed, multi-threaded zstd, and a guide to choosing. *Shown by:* published,
    reproducible numbers against SQL Server's native compression on the same
    databases, with a harness for anyone who has Redgate to add their column.
13. **Restore chains everywhere.** Cloud listing for `restore-chain`, selection by
    log sequence number (record first and last LSN in the manifest), and
    `test-restore` for differential and log chains. *Done when:* a chain in S3
    restores with no file named and a drill can prove a full + differential +
    logs chain.
14. **Linux and containers as a first-class home.** A published container image
    (free on the GitHub registry), a Kubernetes CronJob example for SQL Server in
    a cluster, and a built Linux shim for common distros. Redgate's
    documentation does not describe a Linux or container deployment; it is where
    SQL Server is increasingly run.
15. **More storage and safer cloud auth.** Google Cloud Storage, native
    Backblaze B2, SFTP, S3 server-side encryption, and managed identities, IAM
    roles and SAS tokens in place of long-lived keys.
16. **Packaging and docs, free.** Signed binaries (SignPath Foundation for
    Windows, Sigstore for Linux archives), winget, Homebrew, Chocolatey, deb and
    rpm, and a documentation site on GitHub Pages instead of one README, with the
    backup and manifest formats specified so others can read them.
17. **Ecosystem fit.** A tested recipe for Ola Hallengren's maintenance scripts
    and dbatools, and (if the owner wants it) a proposal to teach Ola's
    `DatabaseBackup` about SqlSqueeze as a backup software option.
18. **Restore to another server's layout.** `--move-all` style relocation
    without listing every file, and a restore that defaults to "latest" from a
    folder.

### Research (might not work out; will say so)

- **Deduplication across backups.** Storing only the new chunks of each full
  backup (content-defined chunking, as restic and borg do) could cut storage far
  below any compressor. SQL Server's backup stream may not dedupe well; the first
  step is a measurement on real backups, and the result, good or bad, is
  published.
- **A web dashboard.** Redgate's GUI is its biggest advantage. A small read-only
  web view of backup health and drill results, built on item 9's data, is the
  realistic free answer. See *Decisions*.

## Not doing (for now)

- **A desktop GUI,** and anything that needs a server we would have to pay to run.
- **Anything that only works untested.**
- **Replacing native backups.** SqlSqueeze is for what native backup does not
  cover (editions without compression, extra formats, encryption, copying,
  verifying, drilling), and always lets you get back to a native `.bak`.

## Decisions needed from the owner

Nothing here costs money.

1. **Apply to the SignPath Foundation** for free Windows code signing (item 16).
   It needs the project owner as applicant; criteria include an OSI license, a
   public repository, free downloads and an automated build, and each signing
   request is approved by hand. Reputation can take time to build.
2. **Enable GitHub Pages** for the docs site (item 16).
3. **GUI stance:** keep it a non-goal, or plan the small web dashboard after
   monitoring (items 9 and the research note).
4. **Upstreaming to Ola Hallengren's scripts** (item 17): worth doing, or leave
   integration to documentation.
5. **Real-cloud results:** only if you want them sooner than volunteers will
   supply them: run the conformance script (item 2) once against any account you
   already have. Optional.

## Known issues

- Parameter "required" flags are not enforced (item 4).
- Cloud `prune` reports `bytesReclaimed: 0` (item 5).
- `restore-chain` reads local folders only, picks by time taken rather than LSN,
  and `test-restore` handles full backups only (item 13).
- A backup streaming straight into S3 or Azure cannot be resumed if the network
  drops; use a local backup plus `--copy-to` (this is a property of how SQL
  Server writes a backup, not something to fix).
- SQL Server 2025 on Linux could not take any backup with v1.6.0 and earlier (the
  shim did not link libuuid, which 2025's `libsqlvdi.so` needs); fixed on master.
- v1.6.1 and earlier: S3 requests were not signed the way AWS requires (found by
  the conformance run), so S3 backups were very likely rejected by real AWS S3 and
  other strict services; Azure blob names with `%`, `#` or similar were mangled.
  Fixed in v1.6.2. moto and the unit-test fakes never checked signatures, which is
  why nothing noticed.
- v1.4.0 and earlier can write unrestorable `zstd`+`aes` backups and leave
  truncated cloud objects; fixed in v1.5.0.
