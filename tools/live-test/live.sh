#!/usr/bin/env bash
#
# Live scenarios for SqlSqueeze against a REAL SQL Server, on Linux or on Windows
# (run it with Git Bash there).
#
# The unit tests use fakes; this runs the commands that matter against SQL
# Server itself (VDI backup, RESTORE VERIFYONLY, DBCC CHECKDB, restore chains).
# It only touches databases named sqs_live_*, and removes them when it ends.
#
# Needs: a running SQL Server on this machine (VDI needs client and server on the
# same host) with SQL logins enabled, sqlcmd, jq and python3, and a published
# sqlsqueeze next to its VDI shim on Linux (libmsbpvdi.so). On Linux it also needs
# sudo, because VDI needs the mssql user.
#
#   SQS_APP=/path/to/published SA_PASSWORD=... tools/live-test/live.sh
#
# Environment:
#   SQS_APP       folder with the published sqlsqueeze (required)
#   SA_PASSWORD   the sa password (required)
#   SQLCMD        sqlcmd path (default /opt/mssql-tools18/bin/sqlcmd, or sqlcmd on Windows)
#   SQS_WORK      a scratch folder (default: a new one in the temp folder)
#   SQS_SERVER    sqlcmd -S value (default localhost,1433, or localhost on Windows)

set -uo pipefail

: "${SQS_APP:?set SQS_APP to the folder with the published sqlsqueeze}"
: "${SA_PASSWORD:?set SA_PASSWORD}"
case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*) WIN=1 ;;
    *) WIN=0 ;;
esac

if [ "$WIN" = 1 ]; then
    SQLCMD=${SQLCMD:-sqlcmd}
    SQS_SERVER=${SQS_SERVER:-localhost}
    APP=$SQS_APP/sqlsqueeze.exe
    FURL=file:///   # file:///C:/dir/x.bak
    export MSYS_NO_PATHCONV=1   # Git Bash must not rewrite arguments that start with /
    PY=$(command -v python3 || command -v python)
else
    SQLCMD=${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}
    SQS_SERVER=${SQS_SERVER:-localhost,1433}
    APP=$SQS_APP/sqlsqueeze
    FURL=file://    # file:///tmp/dir/x.bak
    PY=python3
fi

own_work=0
if [ -z "${SQS_WORK:-}" ]; then
    SQS_WORK=$(mktemp -d "${TMPDIR:-/tmp}/sqs-live.XXXXXX")
    own_work=1
fi
[ "$WIN" = 1 ] && SQS_WORK=$(cygpath -m "$SQS_WORK")   # C:/..., which every tool here understands
chmod 755 "$SQS_WORK"
mkdir -p "$SQS_WORK/bk" "$SQS_WORK/native" "$SQS_WORK/mirror"
chmod 777 "$SQS_WORK/bk" "$SQS_WORK/mirror"
if [ "$WIN" = 1 ]; then
    # the SQL Server service account has to read the unpacked backup
    icacls "$(cygpath -w "$SQS_WORK")" /grant 'Everyone:(OI)(CI)F' > /dev/null
fi

export SQLCMDPASSWORD=$SA_PASSWORD
export SQS_PW=$SA_PASSWORD
export SQS_KEY=live-test-key-1
PIPE='aes(passwordEnv=SQS_KEY;iterations=1000)'
DBOPT='db(user=sa;passwordEnv=SQS_PW)'

passed=0
failed=0

sql() { "$SQLCMD" -C -S "$SQS_SERVER" -U sa -b -W -h -1 "$@" | tr -d '\r'; }
# On Linux, VDI commands run as the mssql user (it shares memory with the server); the rest need no privileges.
# stderr goes to a log that is printed when a check fails, so a failure says why.
ERRLOG=$SQS_WORK/stderr.log
if [ "$WIN" = 1 ]; then
    vdi() { "$APP" "$@" 2>> "$ERRLOG"; }
else
    vdi() { sudo --preserve-env=SQS_PW,SQS_KEY -u mssql "$APP" "$@" 2>> "$ERRLOG"; }
fi
plain() { "$APP" "$@" 2>> "$ERRLOG"; }
last_json() { grep '^{' | tail -1; }

ok() { passed=$((passed + 1)); echo "PASS: $1"; }
bad() {
    failed=$((failed + 1)); echo "FAIL: $1"; [ -n "${2:-}" ] && echo "      $2"
    if [ -s "$ERRLOG" ]; then echo "      stderr:"; tail -n 12 "$ERRLOG" | sed 's/^/        /'; : > "$ERRLOG"; fi
}

# expect <name> <json> <jq expression that must print true>
expect() {
    local name=$1 json=$2 expr=$3 got
    got=$(printf '%s' "$json" | jq -r "$expr" 2>/dev/null)
    if [ "$got" = "true" ]; then ok "$name"; else bad "$name" "got: $(printf '%s' "$json" | cut -c1-300)"; fi
}
expect_eq() {
    if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "expected [$3], got [$2]"; fi
}

drop_databases() {
    sql -Q "SET NOCOUNT ON; DECLARE @n sysname; DECLARE c CURSOR FOR SELECT name FROM sys.databases WHERE name LIKE 'sqs[_]live[_]%' OR name LIKE 'sqs[_]testrestore[_]sqs[_]live%'; OPEN c; FETCH NEXT FROM c INTO @n; WHILE @@FETCH_STATUS=0 BEGIN BEGIN TRY EXEC('ALTER DATABASE ['+@n+'] SET SINGLE_USER WITH ROLLBACK IMMEDIATE'); END TRY BEGIN CATCH END CATCH; EXEC('DROP DATABASE ['+@n+']'); FETCH NEXT FROM c INTO @n; END; CLOSE c; DEALLOCATE c;" > /dev/null 2>&1
    if [ "$WIN" = 0 ]; then sudo rm -f "$DATA"/sqs_live_*.mdf "$DATA"/sqs_live_*.ldf "$DATA"/sqs_live_*.ndf 2> /dev/null; fi
}
cleanup() {
    drop_databases
    if [ "$own_work" = 1 ]; then if [ "$WIN" = 1 ]; then rm -rf "$SQS_WORK"; else sudo rm -rf "$SQS_WORK"; fi; fi
}
# where this server keeps new databases (no trailing slash)
DATA=$(sql -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400))")
DATA=${DATA%[/\\]}
trap cleanup EXIT
drop_databases   # a clean slate if a previous run was interrupted

stamp() { sql -d "$1" -Q "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS varchar) + '/' + CAST(CHECKSUM_AGG(CHECKSUM(id, v)) AS varchar) FROM t"; }

echo "== setup: $(sql -Q "SET NOCOUNT ON; SELECT LEFT(@@VERSION, CHARINDEX(CHAR(10), @@VERSION) - 1)")"
for d in sqs_live_a sqs_live_b; do
    sql -Q "SET NOCOUNT ON; CREATE DATABASE $d; ALTER DATABASE $d SET RECOVERY FULL;" || bad "create $d"
    sql -d $d -Q "SET NOCOUNT ON; CREATE TABLE t(id INT IDENTITY PRIMARY KEY, v VARCHAR(100)); INSERT INTO t(v) SELECT TOP 20000 CONVERT(VARCHAR(100), NEWID()) FROM sys.all_objects a CROSS JOIN sys.all_objects b;" || bad "load $d"
done
SRC=$(stamp sqs_live_a)

# ---- 1. full backup, manifest, verify (hashes and SQL Server's own check) ----
out=$(vdi backup "db(database=sqs_live_a;user=sa;passwordEnv=SQS_PW)" zstd "$PIPE" "$FURL$SQS_WORK/bk/a.bak" --json | last_json)
expect "backup zstd+aes succeeds" "$out" '.status == "ok"'
[ -f "$SQS_WORK/bk/a.bak.manifest.json" ] && ok "backup wrote a manifest" || bad "backup wrote a manifest"

out=$(plain verify "$FURL$SQS_WORK/bk/a.bak" --from-manifest --json | last_json)
expect "verify --from-manifest passes" "$out" '.status == "ok" and .manifestFound == true'

out=$(vdi verify "$FURL$SQS_WORK/bk/a.bak" --from-manifest --sql "$DBOPT" --json | last_json)
expect "verify --sql (RESTORE VERIFYONLY through VDI) passes" "$out" '.status == "ok" and .sqlChecked == true'

cp "$SQS_WORK/bk/a.bak" "$SQS_WORK/bk/flip.bak" && cp "$SQS_WORK/bk/a.bak.manifest.json" "$SQS_WORK/bk/flip.bak.manifest.json" || bad "could not copy the backup to corrupt it"
"$PY" - "$SQS_WORK/bk/flip.bak" << 'EOF'
import sys
p = sys.argv[1]
b = bytearray(open(p, 'rb').read())
b[len(b) // 2] ^= 1
open(p, 'wb').write(b)
EOF
out=$(plain verify "$FURL$SQS_WORK/bk/flip.bak" --from-manifest --json | last_json)
expect "verify rejects a backup with one flipped byte, naming the hash mismatch" "$out" '.status == "error" and (.problems | map(test("sha256")) | any)'

# ---- 2. unpack to a native .bak and restore it with plain sqlcmd ----
out=$(plain unpack "$FURL$SQS_WORK/bk/a.bak" "$FURL$SQS_WORK/native/a.bak" --from-manifest --json | last_json)
expect "unpack --from-manifest succeeds" "$out" '.status == "ok"'
chmod 644 "$SQS_WORK/native/a.bak" 2> /dev/null
sql -Q "SET NOCOUNT ON; RESTORE DATABASE sqs_live_native FROM DISK='$SQS_WORK/native/a.bak' WITH MOVE 'sqs_live_a' TO '$DATA/sqs_live_native.mdf', MOVE 'sqs_live_a_log' TO '$DATA/sqs_live_native_log.ldf'" > /dev/null 2>&1
expect_eq "a plain native RESTORE of the unpacked file matches the source" "$(stamp sqs_live_native)" "$SRC"

# ---- 3. restore through VDI with the pipeline taken from the manifest ----
out=$(vdi restore "$FURL$SQS_WORK/bk/a.bak" "db(database=sqs_live_vdi;user=sa;passwordEnv=SQS_PW;MOVE='sqs_live_a'TO'$DATA/sqs_live_vdi.mdf';MOVE='sqs_live_a_log'TO'$DATA/sqs_live_vdi_log.ldf')" --from-manifest --json | last_json)
expect "restore --from-manifest succeeds" "$out" '.status == "ok"'
expect_eq "the VDI-restored copy matches the source" "$(stamp sqs_live_vdi)" "$SRC"

# ---- 4. test restore with DBCC CHECKDB ----
out=$(vdi test-restore "$FURL$SQS_WORK/bk/a.bak" --from-manifest --db "$DBOPT" --json | last_json)
expect "test-restore passes and drops its scratch database" "$out" '.status == "ok" and .dropped == true'
expect_eq "no scratch database is left behind" "$(sql -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'sqs_testrestore%'")" "0"

# ---- 5. a chain: full + differential + logs, restored automatically, and to a point in time ----
mkdir -p "$SQS_WORK/bk/chain" && chmod 777 "$SQS_WORK/bk/chain"   # the mssql user writes the backups here
ins() { sql -d sqs_live_b -Q "SET NOCOUNT ON; INSERT INTO t(v) SELECT TOP 3000 '$1' FROM sys.all_objects a CROSS JOIN sys.all_objects b;"; }
bkb() { vdi backup "db(database=sqs_live_b;user=sa;passwordEnv=SQS_PW;backuptype=$1)" zstd "$PIPE" "$FURL$SQS_WORK/bk/chain/b_$2.bak" --json | last_json; sleep 1.3; }
ins b1; out=$(bkb full 1full);          expect "chain: full backup" "$out" '.status == "ok"'
ins b2; out=$(bkb differential 2diff);  expect "chain: differential backup" "$out" '.status == "ok"'
ins b3; out=$(bkb log 3log);            expect "chain: first log backup" "$out" '.status == "ok"'
ins b4; sleep 1.3; MID=$(sql -Q "SET NOCOUNT ON; SELECT CONVERT(VARCHAR(23), SYSDATETIME(), 121)"); MIDROWS=$(sql -d sqs_live_b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM t"); sleep 1.3
ins b5; out=$(bkb log 4log);            expect "chain: second log backup" "$out" '.status == "ok"'
SRCB=$(stamp sqs_live_b)

out=$(plain restore-chain "$SQS_WORK/bk/chain" sqs_live_b --db "$DBOPT" --as sqs_live_chain --dry-run --json | last_json)
expect "restore-chain --dry-run plans four steps in order" "$out" '[.steps[].backupType] == ["full","differential","log","log"]'
out=$(vdi restore-chain "$SQS_WORK/bk/chain" sqs_live_b --db "$DBOPT" --as sqs_live_chain --json | last_json)
expect "restore-chain restores the whole chain" "$out" '.status == "ok" and ([.steps[].status] | all(. == "ok"))'
expect_eq "the chain-restored database matches the source" "$(stamp sqs_live_chain)" "$SRCB"
out=$(vdi restore-chain "$SQS_WORK/bk/chain" sqs_live_b --db "$DBOPT" --as sqs_live_pit --stopat "$MID" --json | last_json)
expect "restore-chain --stopat restores" "$out" '.status == "ok"'
expect_eq "point in time has exactly the rows that existed then" "$(sql -d sqs_live_pit -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM t")" "$MIDROWS"

# ---- 6. backup-all with per-database paths and --copy-to ----
out=$(vdi backup-all "sqs_live_a,sqs_live_b" zstd "$PIPE" "local(path=$SQS_WORK/bk/all/{database}/{database}_{backuptype}.bak)" --db "$DBOPT" --copy-to "local(path=$SQS_WORK/mirror/{database}/{database}.bak)" --json | last_json)
expect "backup-all backs up both databases and copies each" "$out" '.status == "ok" and ([.results[].status] == ["ok","ok"]) and ([.results[].copies[].status] == ["ok","ok"])'
out=$(plain verify "$FURL$SQS_WORK/mirror/sqs_live_a/sqs_live_a.bak" --from-manifest --json | last_json)
expect "the --copy-to copy verifies from its own manifest" "$out" '.status == "ok"'

# ---- 7. restore over a database that is in use ----
( sql -d sqs_live_a -Q "WAITFOR DELAY '00:00:40'" > /dev/null 2>&1 & )
sleep 2
out=$(vdi restore "$FURL$SQS_WORK/bk/a.bak" "db(database=sqs_live_a;user=sa;passwordEnv=SQS_PW;REPLACE)" --from-manifest --json | last_json)
expect "restore over an in-use database fails without --kill-connections" "$out" '.status == "error"'
( sql -d sqs_live_a -Q "WAITFOR DELAY '00:00:40'" > /dev/null 2>&1 & )
sleep 2
out=$(vdi restore "$FURL$SQS_WORK/bk/a.bak" "db(database=sqs_live_a;user=sa;passwordEnv=SQS_PW;REPLACE)" --from-manifest --kill-connections --json | last_json)
expect "restore --kill-connections succeeds" "$out" '.status == "ok"'
expect_eq "the database is multi-user afterwards" "$(sql -Q "SET NOCOUNT ON; SELECT user_access_desc FROM sys.databases WHERE name='sqs_live_a'")" "MULTI_USER"

echo
echo "live tests: $passed passed, $failed failed"
[ "$failed" -eq 0 ]
