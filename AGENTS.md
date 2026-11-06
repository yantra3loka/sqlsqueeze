# AGENTS.md — SqlSqueeze operator notes for LLM agents

You are helping a DBA back up / restore SQL Server with SqlSqueeze.
Human runbook: `docs/DBA-GUIDE.md`. This file is the machine précis:
safety rules first, then exact mechanics. When in doubt, `--dry-run`
(or `verify`) before any mutating command.

## Safety rules (hard)

1. The tool MUST run on the SQL Server machine (VDI is local-only).
   Never run a `backup`/`restore` from another host and assume it worked.
2. Read-only before mutating: `verify`, `listplugins`, `help <cmd>`,
   `restore-chain --dry-run` need no approval to run. `backup`,
   `restore`, `test-restore`, `prune`, `copy` change state — confirm
   target database, files, and flags with the DBA first.
3. Never restore over the original database without explicit
   `database=<name>` + `replace` confirmation. Default to restoring as a
   copy (`--as <copy>` / `move=...TO...`) and diffing counts.
4. Never delete/prune without showing the exact file list first.
5. Secrets: only `...Env=` variants (`passwordEnv=`, `secretkeyenv=`,
   `accountkeyenv=`). Never emit literal secrets into commands, scripts,
   logs, or chat. Manifests store variable *names*, never values.
6. Quote MOVE clauses exactly: `move='logical'TO'path'` (uppercase `TO`,
   single quotes, exactly 4 quotes per clause). Through ssh→cmd→PowerShell
   layering the quotes get eaten — write a `.ps1` file, copy it over, run
   `powershell -File`. Never inline complex commands through 2+ shells.

## Connection facts

- Param is `instancename=` (NOT `instance=` — silently ignored, then
  Named Pipes error 40). `[MyDB]` = `db(database=MyDB)`, default instance.
- `backuptype=` takes `full|differential|log` (exact lowercase;
  `diff` is accepted only by `backup-all --type`).
- Every scripting call ends with `--json`: exit `0` ok / `-1` fail.

## Command shapes (verified)

```text
backup  "db(database=D;instancename=I[;backuptype=differential|log])" <pipeline...> "local(path=FILE)" [--copy-to S ...] [--verify] --json
restore "local(path=FILE)" <pipeline...> "db(database=D[;replace][;move='L'TO'P'...])" [--kill-connections] [--from-manifest] --json
restore-chain DIR DB --db "db" [--as COPY] [--stopat T] [--dry-run] [--replace] [--kill-connections] [--norecovery|--standby F] --json
verify  "local(path=FILE)" <pipeline...> [--sql "db..."] [--from-manifest] --json
test-restore "local(path=FILE)" <pipeline...> --db "db..." [--keep] [--physical-only] [--from-manifest] --json
unpack  "local(path=SRC)" <pipeline...> "local(path=DST)" [--from-manifest] --json
copy    SRC DST [--max-hours H] [--backoff-seconds S] --json
prune   "local(dir=D;pattern=GLOB;olderThanDays=N)" --json
backup-all SELECTION <pipeline...> "local(path=...{database}...)" --db "db..." [--type full|diff|log] [--no-fallback] --json
```

- Pipeline order on backup is data→first→...→file (e.g. `zstd` then `aes`
  = compress-then-encrypt); restore lists storage first and mirrors it.
- Striped: repeat `path=` (`key=` S3, `blob=` Azure); same files, same
  order on restore/unpack/copy.
- `restore-chain` takes a plain folder path (not `local(...)`) and REQUIRES
  `--db`. Copy-only backups are excluded from chains.
- `unpack` never overwrites; needs one output per stripe.

## Known behaviors (do not "fix", work around)

- Overwrite restore without `replace` fails on tail-log protection — expected,
  not a bug. DB stays MULTI_USER.
- `--kill-connections` returns the DB to MULTI_USER on success AND failure.
- Post-restore orphaned-users warning = cross-server SIDs, fix with
  `ALTER USER`.
- No manifest ⇒ verify/unpack degrade to decode-only Ok. Say so loudly.
- `test-restore` needs manifest + full backup + free space for a DB copy;
  refuses FILESTREAM-less-unknown types other than ROWS/LOG/FILESTREAM.
- Stripe-count mismatch surfaces as SQL Msg 3241, not a friendly error.

## After any backup/restore you ran

Report: the `--json` result line, file bytes (`.bak.zst` vs manifest),
row counts source vs restored, and `CHECKSUM_AGG(BINARY_CHECKSUM(*))`
per table when byte-correctness matters. Clean up scratch DBs/files you
made unless told to keep them.
