#!/usr/bin/env bash
#
# Tests for tools/cron/sqlsqueeze-cron.sh using a fake sqlsqueeze CLI and a
# fake curl, so nothing real runs and no network is used.
#
#   tools/tests/cron-wrapper-test.sh
#
# Needs bash and jq (the wrapper uses jq to read the CLI's JSON).

set -uo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WRAPPER=$HERE/../cron/sqlsqueeze-cron.sh
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/bin"

failures=0

# The VDI library prints an abort notice on stdout after the CLI's JSON line,
# with no trailing newline, so the JSON is not the last line of output.
VDI_NOISE='Host_1234_SQLVDIMemoryName_0: CVD::GetCommand: Abort bit is set: error 0'

cat > "$WORK/bin/curl" <<'CURL'
#!/usr/bin/env bash
while [ $# -gt 0 ]; do
    if [ "$1" = "--data" ]; then printf '%s' "$2" > "$PAYLOAD_OUT"; fi
    shift
done
CURL
chmod +x "$WORK/bin/curl"

# run_case <name> <json line the fake CLI prints> <expected message>
run_case() {
    local name=$1 json=$2 expected=$3 message
    {
        echo '#!/usr/bin/env bash'
        echo "echo 'local:'"
        echo "echo '$json'"
        echo "printf '%s' '$VDI_NOISE'"
        echo 'exit 255'
    } > "$WORK/bin/fake-sqlsqueeze"
    chmod +x "$WORK/bin/fake-sqlsqueeze"
    rm -f "$WORK/payload.json"

    PATH="$WORK/bin:$PATH" SQLSQUEEZE_EXE=fake-sqlsqueeze WEBHOOK_URL=http://example.invalid/hook \
        PAYLOAD_OUT="$WORK/payload.json" bash "$WRAPPER" verify file:///x zstd > /dev/null 2>&1
    local code=$?

    message=$(jq -r '.message' "$WORK/payload.json" 2>/dev/null)
    if [ "$code" -eq 255 ] && [ "$message" = "$expected" ]; then
        echo "PASS: $name"
    else
        echo "FAIL: $name (exit $code)"
        echo "  expected message: $expected"
        echo "  got message:      $message"
        failures=$((failures + 1))
    fi
}

run_case "a failed verify reports its problems, not the VDI notice" \
    '{"status":"error","operation":"verify","problems":["a.bak: stored sha256 mismatch","a.bak: raw sha256 mismatch"]}' \
    'a.bak: stored sha256 mismatch; a.bak: raw sha256 mismatch'

run_case "an ordinary error reports its message, not the VDI notice" \
    '{"status":"error","operation":"backup","message":"disk full"}' \
    'disk full'

run_case "a failed verify with SQL Server's message" \
    '{"status":"error","operation":"verify","problems":["SQL Server RESTORE VERIFYONLY failed: media family incorrectly formed"]}' \
    'SQL Server RESTORE VERIFYONLY failed: media family incorrectly formed'

if [ "$failures" -ne 0 ]; then
    echo "$failures failing"
    exit 1
fi
echo "all passed"
