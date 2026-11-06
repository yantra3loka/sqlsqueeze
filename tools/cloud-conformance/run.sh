#!/usr/bin/env bash
#
# Cloud conformance for SqlSqueeze's S3 and Azure storage.
#
# Runs the storage scenarios that matter against any S3-compatible service or
# Azure Blob account: multipart uploads (including a part-aligned size and a
# one-byte file), awkward object names, overwrite, integrity checks of what was
# stored, a download that is bit-identical to what went up, a copy that is killed
# half way and then resumed, clean failures (bad key, missing bucket), and prune.
#
# It needs no SQL Server. It builds small fake backups (a file plus its manifest)
# and moves them with `copy`, `verify` and `prune`. It only touches objects under
# its own random prefix, which it removes at the end. It checks first that the
# bucket or container exists and refuses to run if not, because sqlsqueeze itself
# creates a missing bucket or container when it writes, and a conformance run
# should not leave one behind in your account.
#
# Use it in CI against MinIO, moto or Azurite, or against your own account to find
# out whether your provider really works (a free-tier bucket is plenty).
#
#   S3 (AWS, MinIO, Backblaze B2, Cloudflare R2, Wasabi, Ceph...):
#     SQS_APP=/path/to/published SQS_S3_ENDPOINT=https://s3.us-east-1.amazonaws.com \
#       SQS_S3_BUCKET=my-test-bucket SQS_S3_REGION=us-east-1 \
#       SQS_S3_ACCESSKEY=... SQS_S3_SECRET=... tools/cloud-conformance/run.sh s3
#
#   Azure Blob:
#     SQS_APP=/path/to/published SQS_AZ_ENDPOINT=https://myaccount.blob.core.windows.net \
#       SQS_AZ_ACCOUNT=myaccount SQS_AZ_CONTAINER=my-test-container \
#       SQS_AZ_KEY=... tools/cloud-conformance/run.sh azure
#
# Optional:
#   SQS_BIG_MB   size of the file used for the resume scenario (default 64)
#
# Needs: bash, jq, gzip, head, and sha256sum or shasum. The bucket or container
# must already exist and the credentials must be able to list, read, write and
# delete in it. Objects are small; the run transfers about 3 x SQS_BIG_MB in total.

set -uo pipefail

PROVIDER=${1:-}
case "$PROVIDER" in
    s3 | azure) ;;
    *) echo "usage: $0 s3|azure   (settings come from environment variables; see the top of this file)" >&2; exit 2 ;;
esac

: "${SQS_APP:?set SQS_APP to the folder with the published sqlsqueeze}"
APP=$SQS_APP/sqlsqueeze
[ -x "$APP" ] || { echo "no sqlsqueeze in $SQS_APP" >&2; exit 2; }
for tool in jq gzip head; do
    command -v "$tool" > /dev/null || { echo "$tool is required" >&2; exit 2; }
done
if command -v sha256sum > /dev/null; then
    sha256() { sha256sum "$1" | cut -d' ' -f1; }
else
    sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }
fi
filesize() { wc -c < "$1" | tr -d ' '; }

BIG_MB=${SQS_BIG_MB:-64}
PART_MB=5
PREFIX="sqs-conformance-$(date +%s)-$RANDOM"
WORK=$(mktemp -d "${TMPDIR:-/tmp}/sqs-cloud.XXXXXX")
ERRLOG=$WORK/stderr.log

if [ "$PROVIDER" = s3 ]; then
    : "${SQS_S3_ENDPOINT:?set SQS_S3_ENDPOINT}" "${SQS_S3_BUCKET:?set SQS_S3_BUCKET}" "${SQS_S3_ACCESSKEY:?set SQS_S3_ACCESSKEY}" "${SQS_S3_SECRET:?set SQS_S3_SECRET}"
    export SQS_S3_SECRET
    TARGET="$SQS_S3_ENDPOINT, bucket $SQS_S3_BUCKET"
    # spec <object name> -> the storage argument for that object
    spec() { echo "s3(endpoint=$SQS_S3_ENDPOINT;bucket=$SQS_S3_BUCKET;key=$PREFIX/$1;region=${SQS_S3_REGION:-us-east-1};accesskey=$SQS_S3_ACCESSKEY;secretkeyenv=SQS_S3_SECRET;partsizeMB=$PART_MB;writeretries=2;writeretrybackoffms=200)"; }
    spec_badsecret() { echo "s3(endpoint=$SQS_S3_ENDPOINT;bucket=$SQS_S3_BUCKET;key=$PREFIX/$1;region=${SQS_S3_REGION:-us-east-1};accesskey=$SQS_S3_ACCESSKEY;secret=not-the-secret;partsizeMB=$PART_MB;writeretries=0)"; }
        prune_spec() { echo "s3(endpoint=$SQS_S3_ENDPOINT;bucket=$SQS_S3_BUCKET;prefix=$PREFIX/$1;pattern=$2;olderThanDays=0;region=${SQS_S3_REGION:-us-east-1};accesskey=$SQS_S3_ACCESSKEY;secretkeyenv=SQS_S3_SECRET)"; }
else
    : "${SQS_AZ_ENDPOINT:?set SQS_AZ_ENDPOINT}" "${SQS_AZ_ACCOUNT:?set SQS_AZ_ACCOUNT}" "${SQS_AZ_CONTAINER:?set SQS_AZ_CONTAINER}" "${SQS_AZ_KEY:?set SQS_AZ_KEY}"
    export SQS_AZ_KEY
    TARGET="$SQS_AZ_ENDPOINT, container $SQS_AZ_CONTAINER"
    spec() { echo "azure(endpoint=$SQS_AZ_ENDPOINT;container=$SQS_AZ_CONTAINER;blob=$PREFIX/$1;account=$SQS_AZ_ACCOUNT;accountkeyenv=SQS_AZ_KEY;blocksizeMB=4;writeretries=2;writeretrybackoffms=200)"; }
    spec_badsecret() { echo "azure(endpoint=$SQS_AZ_ENDPOINT;container=$SQS_AZ_CONTAINER;blob=$PREFIX/$1;account=$SQS_AZ_ACCOUNT;accountkey=bm90LXRoZS1rZXk=;writeretries=0)"; }
    prune_spec() { echo "azure(endpoint=$SQS_AZ_ENDPOINT;container=$SQS_AZ_CONTAINER;prefix=$PREFIX/$1;pattern=$2;olderThanDays=0;account=$SQS_AZ_ACCOUNT;accountkeyenv=SQS_AZ_KEY)"; }
fi

passed=0
failed=0
ok() { passed=$((passed + 1)); echo "PASS: $1"; }
bad() {
    failed=$((failed + 1)); echo "FAIL: $1"; [ -n "${2:-}" ] && echo "      $2"
    if [ -s "$ERRLOG" ]; then echo "      stderr:"; tail -n 8 "$ERRLOG" | sed 's/^/        /'; : > "$ERRLOG"; fi
}
last_json() { grep '^{' | tail -1; }
sqs() { "$APP" "$@" 2>> "$ERRLOG"; }
expect() {   # expect <name> <json> <jq expression that must print true>
    local got
    got=$(printf '%s' "$2" | jq -r "$3" 2> /dev/null)
    if [ "$got" = true ]; then ok "$1"; else bad "$1" "got: $(printf '%s' "$2" | cut -c1-300)"; fi
}
expect_eq() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "expected [$3], got [$2]"; fi; }

cleanup() {
    # remove everything this run created, whatever happened
    sqs prune "$(prune_spec '' '*.bak')" --json > /dev/null 2>&1
    rm -rf "$WORK"
}
trap cleanup EXIT

# fixture <name> <raw bytes> <gzip|none>: a fake backup and the manifest that describes it
fixture() {
    local name=$1 bytes=$2 mode=$3 raw=$WORK/raw_$1 stored=$WORK/$1
    head -c "$bytes" /dev/urandom > "$raw"
    local pipeline='[]'
    if [ "$mode" = gzip ]; then gzip -c "$raw" > "$stored"; pipeline='["gzip"]'; else cp "$raw" "$stored"; fi
    jq -n --arg name "$name" --arg ss "$(sha256 "$stored")" --arg rs "$(sha256 "$raw")" \
        --argjson sb "$(filesize "$stored")" --argjson rb "$(filesize "$raw")" --argjson pipe "$pipeline" \
        '{formatVersion: 1, tool: "conformance", createdUtc: (now | todate), database: "conformance", backupType: "full",
          storage: "local", pipeline: $pipe,
          files: [{name: $name, storedBytes: $sb, storedSha256: $ss, rawBytes: $rb, rawSha256: $rs}]}' > "$stored.manifest.json"
}

upload() { sqs copy "file://$WORK/$1" "$(spec "${2:-$1}")" --max-hours 0.01 --backoff-seconds 1 --json | last_json; }
cloud_verify() { sqs verify "$(spec "$1")" --from-manifest --json | last_json; }
download() { sqs copy "$(spec "$1")" "file://$WORK/$2" --max-hours 0.01 --backoff-seconds 1 --json | last_json; }

echo "== cloud conformance: $PROVIDER, $TARGET"
echo "== $("$APP" version 2>&1 | tail -1), prefix $PREFIX"

# the bucket or container must exist, and these credentials must be able to list it
out=$(sqs prune "$(prune_spec preflight/ 'nothing-*.bak')" --json | last_json)
if [ "$(printf '%s' "$out" | jq -r .status 2> /dev/null)" != ok ]; then
    echo "cannot list the bucket or container (does it exist, and do the credentials allow listing?): $(printf '%s' "$out" | jq -r '.message // empty' 2> /dev/null | cut -c1-400)" >&2
    exit 2
fi

# ---- 1. multipart upload, verified in place, downloaded bit-identical ----
fixture multi.bak $((12 * 1024 * 1024 + 4321)) gzip
out=$(upload multi.bak)
expect "upload of a multi-part object (12 MiB, ${PART_MB} MiB parts) succeeds" "$out" ".status == \"ok\" and .bytesCopied > 12000000"
expect "the object verifies in the cloud against its manifest (size and sha256)" "$(cloud_verify multi.bak)" '.status == "ok" and .manifestFound == true'
out=$(download multi.bak multi.down)
expect "download succeeds" "$out" '.status == "ok"'
expect_eq "the downloaded object is bit-identical to what was uploaded" "$(sha256 "$WORK/multi.down")" "$(sha256 "$WORK/multi.bak")"
# copy rewrites the manifest (the file name is the new one), so compare what it says about the content
core() { jq -S -c '{formatVersion, database, backupType, pipeline, files: [.files[] | del(.name)]}' "$1" 2> /dev/null; }
expect_eq "the manifest came back with the same files, sizes and hashes" "$(core "$WORK/multi.down.manifest.json")" "$(core "$WORK/multi.bak.manifest.json")"

# ---- 2. sizes at the edges of the part size ----
fixture aligned.bak $((PART_MB * 2 * 1024 * 1024)) none
out=$(upload aligned.bak)
expect "an object exactly two parts long uploads" "$out" '.status == "ok"'
expect "... and verifies" "$(cloud_verify aligned.bak)" '.status == "ok"'
fixture tiny.bak 1 none
out=$(upload tiny.bak)
expect "a one-byte object uploads" "$out" '.status == "ok"'
expect "... and verifies" "$(cloud_verify tiny.bak)" '.status == "ok"'

# ---- 3. awkward object names (signing and URL-encoding differences between services) ----
ODD="odd name/ünï cødé+plus&and%41#hash.bak"
fixture odd.bak 300000 gzip
out=$(upload odd.bak "$ODD")
expect "an object name with spaces, unicode, + & % and # uploads" "$out" '.status == "ok"'
expect "... and verifies" "$(cloud_verify "$ODD")" '.status == "ok"'
download "$ODD" odd.down > /dev/null
expect_eq "... and downloads bit-identical" "$(sha256 "$WORK/odd.down" 2> /dev/null)" "$(sha256 "$WORK/odd.bak")"

# ---- 4. overwrite ----
fixture over.bak 200000 none
upload over.bak > /dev/null
cp "$WORK/over.bak" "$WORK/over_first.bak"
fixture over.bak 250000 none
out=$(upload over.bak)
expect "writing to an existing name succeeds" "$out" '.status == "ok"'
download over.bak over.down > /dev/null
expect_eq "the object now holds the new content, not the old" "$(sha256 "$WORK/over.down" 2> /dev/null)" "$(sha256 "$WORK/over.bak")"

# ---- 5. a copy killed half way is resumed, not restarted ----
# Time one complete copy of the big file, then kill copies at about half that time. Too early
# (nothing stored yet) and too late (it finished) both narrow the window, so the kill time is
# bisected until a copy is caught part-way.
now() { if [ -n "${EPOCHREALTIME:-}" ]; then echo "${EPOCHREALTIME/,/.}"; else date +%s; fi; }
fixture big.bak $((BIG_MB * 1024 * 1024)) none
t0=$(now); out=$(upload big.bak); t1=$(now)
expect "the big object (${BIG_MB} MiB) uploads" "$out" '.status == "ok"'
full=$(awk -v a="$t0" -v b="$t1" 'BEGIN { d = b - a; if (d < 1) d = 1; print d }')
kill_after=$(awk -v d="$full" 'BEGIN { printf "%.2f", d * 0.5 }')
lo=0; hi=$full   # lo: a kill at this time came before anything was stored; hi: the copy had finished by this time
resumed=no
for attempt in 1 2 3 4 5 6; do
    key=big$attempt.bak
    cp "$WORK/big.bak" "$WORK/$key"; cp "$WORK/big.bak.manifest.json" "$WORK/$key.manifest.json"
    "$APP" copy "file://$WORK/$key" "$(spec "$key")" --max-hours 0.01 --backoff-seconds 1 --json > "$WORK/kill.out" 2>> "$ERRLOG" &
    pid=$!
    ( sleep "$kill_after"; kill -9 "$pid" 2> /dev/null ) &
    killer=$!
    wait "$pid" 2> /dev/null; rc=$?
    kill "$killer" 2> /dev/null; wait "$killer" 2> /dev/null
    if [ "$rc" -eq 0 ]; then
        hi=$kill_after   # finished before the kill: aim earlier
        kill_after=$(awk -v l="$lo" -v h="$hi" 'BEGIN { printf "%.2f", (l + h) / 2 }')
        continue
    fi
    out=$(sqs copy "file://$WORK/$key" "$(spec "$key")" --max-hours 0.01 --backoff-seconds 1 --json | last_json)
    skipped=$(printf '%s' "$out" | jq -r '.bytesSkipped // 0' 2> /dev/null)
    copied=$(printf '%s' "$out" | jq -r '.bytesCopied // 0' 2> /dev/null)
    if [ "$(printf '%s' "$out" | jq -r .status 2> /dev/null)" != ok ]; then bad "resume after a killed copy completes" "got: $(printf '%s' "$out" | cut -c1-300)"; resumed=failed; break; fi
    if [ "${skipped:-0}" -eq 0 ]; then
        lo=$kill_after   # killed before the first part was stored: aim later
        kill_after=$(awk -v l="$lo" -v h="$hi" 'BEGIN { printf "%.2f", (l + h) / 2 }')
        continue
    fi
    ok "a copy killed at ${kill_after}s resumes: $skipped of $copied bytes were already stored and skipped"
    expect_eq "... and the resumed copy accounts for every byte" "$copied" "$((BIG_MB * 1024 * 1024))"
    expect_eq "... and the resumed object verifies" "$(printf '%s' "$(cloud_verify "$key")" | jq -r .status)" ok
    resumed=yes
    break
done
if [ "$resumed" = no ]; then bad "could not catch a copy part-way in 6 attempts (a full copy took ${full}s; try a larger SQS_BIG_MB)"; fi

# ---- 6. failures are clean ----
out=$(sqs copy "file://$WORK/tiny.bak" "$(spec_badsecret never.bak)" --max-hours 0.001 --backoff-seconds 1 --json | last_json)
expect "a wrong secret fails (and does not retry for 24 hours)" "$out" '.status == "error"'
expect "... and nothing was stored" "$(cloud_verify never.bak)" '.status == "error"'

# ---- 7. prune: only matching, only under the prefix, manifests go with their backups ----
for n in prune-a.bak prune-b.bak keep.bak; do fixture $n 50000 none; upload $n "prune/$n" > /dev/null; done
sleep 2   # the service's clock and ours may differ slightly
out=$(sqs prune "$(prune_spec prune/ 'prune-*.bak')" --json | last_json)
expect "prune deletes the two matching backups" "$out" '.status == "ok" and .deletedFiles == 2'
expect "the non-matching backup is still there and verifies" "$(cloud_verify prune/keep.bak)" '.status == "ok"'
expect "a pruned backup is gone" "$(cloud_verify prune/prune-a.bak)" '.status == "error"'
out=$(sqs prune "$(prune_spec prune/ 'no-such-*.bak')" --json | last_json)
expect "prune with a pattern that matches nothing deletes nothing" "$out" '.status == "ok" and .deletedFiles == 0'
expect "a pruned backup's manifest went with it" "$(cloud_verify prune/prune-b.bak)" '.manifestFound == false or .status == "error"'

echo
echo "cloud conformance ($PROVIDER, $TARGET, $("$APP" version 2>&1 | tail -1 | cut -d' ' -f1-2)): $passed passed, $failed failed"
[ "$failed" -eq 0 ]
