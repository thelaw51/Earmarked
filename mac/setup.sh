#!/usr/bin/env bash
# Installs whisper.cpp's server on an Apple Silicon Mac as a launchd agent for PodcastBookRecs.
# Safe to re-run: it reinstalls the agent with the current settings and skips a model that is already verified.
#
# Usage: mac/setup.sh --host <this Mac's LAN IP> [--port 8178] [--model large-v3-turbo]
set -euo pipefail

LABEL="com.podcastbookrecs.whisper-server"
HOST=""
PORT="8178"
MODEL="large-v3-turbo"

# SHA-256 of each supported model file, as published by ggerganov/whisper.cpp on Hugging Face.
# A function rather than an associative array: macOS ships bash 3.2, which has none.
MODELS="large-v3-turbo large-v3-turbo-q8_0 large-v3-turbo-q5_0"
model_sha256() {
    case "$1" in
        large-v3-turbo) echo 1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69 ;;
        large-v3-turbo-q8_0) echo 317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1 ;;
        large-v3-turbo-q5_0) echo 394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2 ;;
    esac
}

usage() { echo "Usage: $0 --host <LAN IP> [--port 8178] [--model ${MODELS// /|}]" >&2; exit 64; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --host) HOST="${2:-}"; shift 2 ;;
        --port) PORT="${2:-}"; shift 2 ;;
        --model) MODEL="${2:-}"; shift 2 ;;
        *) usage ;;
    esac
done

[[ "$HOST" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] || { echo "--host must be this Mac's LAN IPv4 address" >&2; usage; }
[[ "$PORT" =~ ^[0-9]+$ ]] || usage
SHA256="$(model_sha256 "$MODEL")"
[[ -n "$SHA256" ]] || { echo "Unknown model '$MODEL'" >&2; usage; }
[[ "$(uname -s)/$(uname -m)" == "Darwin/arm64" ]] || { echo "This script is for Apple Silicon Macs." >&2; exit 1; }
command -v brew >/dev/null || { echo "Homebrew is required: https://brew.sh" >&2; exit 1; }
ifconfig | grep -q "inet $HOST " || { echo "$HOST is not an address of this Mac." >&2; exit 1; }

BREW_PREFIX="$(brew --prefix)"
DATA_DIR="$HOME/Library/Application Support/whisper-server"
MODEL_PATH="$DATA_DIR/models/ggml-$MODEL.bin"
LOG_PATH="$HOME/Library/Logs/whisper-server.log"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "==> Installing whisper-cpp and ffmpeg (skipped if present)"
for formula in whisper-cpp ffmpeg; do
    brew list --formula "$formula" >/dev/null 2>&1 || brew install "$formula"
done

echo "==> Model $MODEL"
mkdir -p "$DATA_DIR/models"
if [[ -f "$MODEL_PATH" ]] && echo "$SHA256  $MODEL_PATH" | shasum -a 256 -c - >/dev/null 2>&1; then
    echo "    already downloaded and verified"
else
    curl -fL --progress-bar -o "$MODEL_PATH.part" "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-$MODEL.bin"
    echo "$SHA256  $MODEL_PATH.part" | shasum -a 256 -c - >/dev/null || { rm -f "$MODEL_PATH.part"; echo "Checksum mismatch, download removed." >&2; exit 1; }
    mv "$MODEL_PATH.part" "$MODEL_PATH"
    echo "    downloaded and verified"
fi

echo "==> Installing launchd agent $LABEL"
mkdir -p "$(dirname "$PLIST")" "$(dirname "$LOG_PATH")"
sed -e "s|__BREW_PREFIX__|$BREW_PREFIX|g" \
    -e "s|__MODEL_PATH__|$MODEL_PATH|g" \
    -e "s|__HOST__|$HOST|g" \
    -e "s|__PORT__|$PORT|g" \
    -e "s|__DATA_DIR__|$DATA_DIR|g" \
    -e "s|__LOG_PATH__|$LOG_PATH|g" \
    "$SCRIPT_DIR/$LABEL.plist.template" > "$PLIST"
plutil -lint "$PLIST" >/dev/null
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
# bootout returns before launchd has removed the old service; bootstrapping too soon fails with error 5.
for _ in $(seq 1 20); do
    launchctl print "gui/$(id -u)/$LABEL" >/dev/null 2>&1 || break
    sleep 0.5
done
launchctl bootstrap "gui/$(id -u)" "$PLIST"

echo "==> Waiting for http://$HOST:$PORT"
for _ in $(seq 1 60); do
    if curl -s -m 2 -o /dev/null "http://$HOST:$PORT/"; then
        echo "    whisper-server is up: http://$HOST:$PORT/v1/audio/transcriptions"
        echo "    log: $LOG_PATH"
        exit 0
    fi
    sleep 1
done
echo "whisper-server did not start within 60 s. Check $LOG_PATH" >&2
exit 1
