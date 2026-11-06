---
layout: default
title: SqlSqueeze docs
---

## Backup compression for the editions Redgate taxes

SqlSqueeze shrinks SQL Server backups with the same Virtual Backup Device
technique Redgate SQL Backup Pro uses — which is why it works where native
`BACKUP ... WITH COMPRESSION` is blocked (Web, Express). Free, open-source,
no lock-in: `unpack` hands you back a plain native `.bak` any time.

**Measured:** 15.7 MB → 1.06 MB (~14.8x) in 0.44 s, restore byte-identical.

## Start here

| You want | Go to |
|---|---|
| Windows / Linux binaries | [Releases](https://github.com/yantra3loka/sqlsqueeze/releases) |
| Install, backup, restore, chains, schedules | [DBA runbook](DBA-GUIDE.html) |
| Drive it through an AI assistant | [LLM operator notes](https://github.com/yantra3loka/sqlsqueeze/blob/master/AGENTS.md) |
| Ready-made SQL Agent jobs | [Job templates](https://github.com/yantra3loka/sqlsqueeze/tree/master/tools/sql-agent) |
| PowerShell cmdlets | [SqlSqueeze module](https://github.com/yantra3loka/sqlsqueeze/blob/master/tools/SqlSqueeze.psm1) |
| What changed per version | [Release notes](https://github.com/yantra3loka/sqlsqueeze/releases) |
| Source code | [GitHub repo](https://github.com/yantra3loka/sqlsqueeze) |

## Thirty-second tour

```text
sqlsqueeze backup "[MyDB]" zstd "local(path=D:\Backups\MyDB.bak.zst)" --json
sqlsqueeze restore "local(path=D:\Backups\MyDB.bak.zst)" zstd "db(database=MyDB_copy;replace;move='MyDB'TO'D:\Data\MyDB_copy.mdf')" --json
sqlsqueeze test-restore "local(path=D:\Backups\MyDB.bak.zst)" zstd --db "db(user=sa;passwordEnv=SQL_PW)" --json
```
