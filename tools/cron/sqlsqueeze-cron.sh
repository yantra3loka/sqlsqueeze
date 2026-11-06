#!/usr/bin/env bash
#
# SqlSqueeze cron wrapper: run a backup command and POST a webhook when
# it fails.
#
# Usage:
#   tools/cron/sqlsqueeze-cron.sh backup "db(database=app)" zstd \
#       "local(path=/backups/app.bak.zst)"
#
# Any arguments are passed to the sqlsqueeze CLI unchanged, except that
# --json is appended (the CLI only accepts it as the last argument) so
# the result line is machine readable. The exit code of the CLI is the
# exit code of this script, so cron sees the failure too.
#
# Environment:
#   SQLSQUEEZE_EXE     path to the CLI       (default: sqlsqueeze)
#   WEBHOOK_URL        webhook endpoint       (required to notify)
#   WEBHOOK_TOKEN      optional Bearer token, sent in the Authorization header
#   WEBHOOK_TIMEOUT    seconds to wait        (default: 10)
#
# No URL, token or password is stored in this file - everything secret
# comes from the environment. If WEBHOOK_URL is unset the script still
# runs the backup and still exits with the CLI's code; it just cannot
# notify anyone.
#
# Cron example (0 02 * * *):
#   0 2 * * * /opt/sqlsqueeze/tools/cron/sqlsqueeze-cron.sh backup \
#       "db(database=app)" zstd "local(path=/backups/app.bak.zst)" \
#       >> /var/log/sqlsqueeze-cron.log 2>&1

set -uo pipefail

readonly PROGRAM_NAME=${0##*/}
SQLSQUEEZE_EXE=${SQLSQUEEZE_EXE:-sqlsqueeze}
WEBHOOK_URL=${WEBHOOK_URL:-}
WEBHOOK_TOKEN=${WEBHOOK_TOKEN:-}
WEBHOOK_TIMEOUT=${WEBHOOK_TIMEOUT:-10}

usage() {
    cat <<'EOF'
Usage: sqlsqueeze-cron.sh <sqlsqueeze arguments...>

Runs the sqlsqueeze CLI with the given arguments and POSTs a JSON
webhook if the command fails. Example:

  sqlsqueeze-cron.sh backup "db(database=app)" zstd \
      "local(path=/backups/app.bak.zst)"

Environment: SQLSQUEEZE_EXE, WEBHOOK_URL, WEBHOOK_TOKEN, WEBHOOK_TIMEOUT
EOF
}

log() {
    printf '%s: %s\n' "$PROGRAM_NAME" "$*" >&2
}

# Escape a string for use inside a JSON string literal.
json_escape() {
    local value=$1
    value=${value//\\/\\\\}
    value=${value//\"/\\\"}
    value=${value//$'\n'/\\n}
    value=${value//$'\r'/\\r}
    value=${value//$'\t'/\\t}
    printf '%s' "$value"
}

# Pull a human message out of the CLI's one-line JSON result, when a
# JSON tool is available. Falls back to the raw line. Errors carry a
# "message"; a failed verify carries a "problems" list instead.
extract_message() {
    local line=$1 message
    if command -v jq >/dev/null 2>&1; then
        message=$(printf '%s' "$line" \
            | jq -r 'if type == "object" then (((.message // ((.problems // []) | join("; "))) | select(. != "")) // .status // empty) else empty end' \
            2>/dev/null)
        if [ -n "$message" ]; then
            printf '%s' "$message"
            return 0
        fi
    fi
    printf '%s' "$line"
}

# POST the payload. The URL is never logged - it can carry a token in
# the query string.
post_webhook() {
    local payload=$1 response status=0
    if command -v curl >/dev/null 2>&1; then
        if [ -n "$WEBHOOK_TOKEN" ]; then
            response=$(curl --silent --show-error --fail --max-time "$WEBHOOK_TIMEOUT" \
                --header 'Content-Type: application/json' \
                --header "Authorization: Bearer $WEBHOOK_TOKEN" \
                --data "$payload" "$WEBHOOK_URL" 2>&1) || status=$?
        else
            response=$(curl --silent --show-error --fail --max-time "$WEBHOOK_TIMEOUT" \
                --header 'Content-Type: application/json' \
                --data "$payload" "$WEBHOOK_URL" 2>&1) || status=$?
        fi
    elif command -v wget >/dev/null 2>&1; then
        if [ -n "$WEBHOOK_TOKEN" ]; then
            response=$(wget --quiet --timeout="$WEBHOOK_TIMEOUT" \
                --header='Content-Type: application/json' \
                --header="Authorization: Bearer $WEBHOOK_TOKEN" \
                --post-data="$payload" "$WEBHOOK_URL" 2>&1) || status=$?
        else
            response=$(wget --quiet --timeout="$WEBHOOK_TIMEOUT" \
                --header='Content-Type: application/json' \
                --post-data="$payload" "$WEBHOOK_URL" 2>&1) || status=$?
        fi
    else
        log "neither curl nor wget is available; cannot send the webhook"
        return 1
    fi

    if [ "$status" -ne 0 ]; then
        # Never echo the endpoint itself; it may embed a secret.
        log "webhook delivery failed (curl/wget exit $status): $response"
        return 1
    fi
    return 0
}

notify_failure() {
    local operation=$1 exit_code=$2 message=$3
    local payload timestamp host

    if [ -z "$WEBHOOK_URL" ]; then
        log "WEBHOOK_URL is not set; skipping webhook notification"
        return 0
    fi

    timestamp=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    host=$(hostname 2>/dev/null || uname -n 2>/dev/null || printf 'unknown')

    payload=$(printf '{"status":"error","operation":"%s","exitCode":%s,"message":"%s","timestamp":"%s","host":"%s"}' \
        "$(json_escape "$operation")" \
        "$exit_code" \
        "$(json_escape "$message")" \
        "$(json_escape "$timestamp")" \
        "$(json_escape "$host")")

    post_webhook "$payload"
}

main() {
    local -a command=()
    local exit_code output last_line operation

    if [ "$#" -eq 0 ]; then
        usage >&2
        return 2
    fi

    case "${1:-}" in
        -h | --help)
            usage
            return 0
            ;;
    esac

    command=("$@")
    # The CLI only recognizes --json as the final argument.
    if [ "${command[$((${#command[@]} - 1))]}" != "--json" ]; then
        command+=(--json)
    fi

    if ! command -v "$SQLSQUEEZE_EXE" >/dev/null 2>&1; then
        log "$SQLSQUEEZE_EXE not found in PATH"
        notify_failure "${command[0]}" 127 "sqlsqueeze executable not found: $SQLSQUEEZE_EXE" || true
        return 127
    fi

    output=$("$SQLSQUEEZE_EXE" "${command[@]}" 2>&1)
    exit_code=$?

    if [ "$exit_code" -ne 0 ]; then
        # The result is the last line that is a JSON object. The SQL Server VDI
        # library can print an abort notice after it, so the last line of output
        # is not always the result; fall back to it when there is no JSON at all.
        last_line=$(printf '%s' "$output" | grep '^[[:space:]]*{' | tail -n 1)
        if [ -z "$last_line" ]; then
            last_line=$(printf '%s' "$output" | grep -v '^[[:space:]]*$' | tail -n 1)
        fi
        operation=${command[0]}
        log "$operation failed with exit code $exit_code"
        notify_failure "$operation" "$exit_code" "$(extract_message "$last_line")" || true
        return "$exit_code"
    fi

    printf '%s\n' "$output"
    return 0
}

main "$@"
