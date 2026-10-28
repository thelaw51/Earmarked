#!/usr/bin/env bash
# Restarts whisper-server when it answers but rejects all audio. After a GPU out-of-memory error, whisper.cpp's Metal
# backend stays broken and every request gets HTTP 500, but the process keeps running, so launchd's KeepAlive never
# restarts it. launchd runs this every few minutes (com.earmarked.whisper-watchdog); setup.sh installs both.
#
# Each run sends a one-second clip with the same form fields Earmarked sends. Two 500s in a row restart the
# server. Anything else changes nothing: a refused connection is KeepAlive's job, and a timeout means the server is
# busy on a real chunk (it handles one request at a time, so the clip waits behind it).
#
# Usage: whisper-watchdog.sh <base URL> <server launchd label> <clip.wav> <state file>
set -euo pipefail

[[ $# -eq 4 ]] || { echo "Usage: $0 <base URL> <label> <clip.wav> <state file>" >&2; exit 64; }
BASE_URL="$1"
LABEL="$2"
CLIP="$3"
STATE="$4"

[[ "$BASE_URL" =~ ^http://[0-9.]+:[0-9]+$ ]] || { echo "Base URL must be http://<IPv4>:<port>" >&2; exit 64; }
[[ "$LABEL" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "Invalid launchd label" >&2; exit 64; }
[[ -f "$CLIP" ]] || { echo "Missing clip: $CLIP" >&2; exit 66; }

RESTART_AFTER=2

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*"; }

failures=0
if [[ -f "$STATE" ]]; then
    failures="$(head -c 16 "$STATE" | tr -cd '0-9')"
    [[ -n "$failures" ]] || failures=0
fi

save() {
    echo "$1" > "$STATE.tmp"
    mv -f "$STATE.tmp" "$STATE"
}

status="$(curl -s -o /dev/null -w '%{http_code}' -m 600 \
    -F "file=@$CLIP;type=audio/wav" -F model=whisper -F response_format=verbose_json -F temperature=0 \
    -- "$BASE_URL/v1/audio/transcriptions" || true)"

case "$status" in
    2??)
        save 0
        ;;
    500)
        failures=$((failures + 1))
        if (( failures >= RESTART_AFTER )); then
            log "HTTP 500 for the test clip $failures times in a row; restarting $LABEL"
            launchctl kickstart -k "gui/$(id -u)/$LABEL"
            save 0
        else
            log "HTTP 500 for the test clip; restarting if the next check fails too"
            save "$failures"
        fi
        ;;
    000)
        # No answer: refused (KeepAlive restarts a crashed server) or still busy after 10 minutes.
        save 0
        ;;
    *)
        log "HTTP $status for the test clip; not restarting"
        save 0
        ;;
esac
