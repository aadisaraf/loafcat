#!/bin/bash
# loafcat — the hook Claude Code runs, so the cat can react to what it is doing.
#
# THE CONTRACT: this script must never slow down, block or fail a Claude Code
# session. It is the only part of loafcat that runs inside somebody else's
# process tree, and a hook that misbehaves shows up to the user as "Claude got
# slow", which they will never trace back to a desktop pet.
#
# So, in order of importance:
#
#   * exit 0, always, whatever happened. Exit 2 blocks Claude outright; other
#     non-zero codes surface as errors the user has to read. Neither is ever
#     worth it for a cosmetic animation. The EXIT trap below makes that true even
#     for a failure this script did not anticipate.
#   * no unbounded wait, anywhere. The stdin read has a deadline, curl has both a
#     connect and a total timeout, and both are sub-second on the network side.
#   * silent no-op when loafcat is not running. The app deletes the handshake
#     file when it quits, so there is nothing to read, and we are gone before we
#     ever open a socket.
#   * nothing on stdout. A hook's stdout is Claude's to interpret.
#
# PRIVACY: hook payloads carry the user's prompt text and the full command line
# of every tool call. Five scalar fields are extracted and the rest is dropped on
# the floor here, before anything leaves the process — the app never sees it and
# there is nothing to leak from a log.
#
# Usage:  loafcat-hook.sh <EventName> [state]      hook JSON on stdin
# `state` is written into the settings.json command by the app, so the event to
# mood mapping is visible in the user's own settings file. Without it, the app
# maps the event name itself.

# Deliberately no `set -e`: a failing command must not end this script early,
# because the only acceptable exit status is 0. The trap covers everything else,
# including a syntax error in a future edit.
trap 'exit 0' EXIT

ENDPOINT="${LOAFCAT_ENDPOINT:-$HOME/.loafcat/endpoint.json}"

# Not running, never installed, or mid-restart. Nothing to do, and nothing to
# report — this is the expected state most of the time.
[ -r "$ENDPOINT" ] || exit 0
command -v curl >/dev/null 2>&1 || exit 0

# The handshake: an ephemeral port and a 32-byte token, written 0600 by the app.
# Parsed with sed rather than jq because jq is not on a stock macOS and a hook
# that needs a dependency is a hook that silently stops working.
PORT=$(sed -n 's/.*"port"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$ENDPOINT" 2>/dev/null | head -n 1)
TOKEN=$(sed -n 's/.*"token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$ENDPOINT" 2>/dev/null | head -n 1)
[ -n "$PORT" ] && [ -n "$TOKEN" ] || exit 0

# --- stdin, with a deadline -------------------------------------------------
# `read -t` on the stock macOS bash (3.2) takes whole seconds, so the wait is
# bounded by a deadline around the loop rather than by the per-read timeout
# alone. Every exit from this loop is bounded: a complete read continues, a
# timeout or EOF breaks, and the deadline stops a drip-feeding producer.
PAYLOAD=""
if [ ! -t 0 ]; then
  __deadline=$(( SECONDS + 1 ))
  while [ "$SECONDS" -le "$__deadline" ] && [ "${#PAYLOAD}" -le 65536 ]; do
    __line=""
    IFS= read -r -t 1 __line
    __status=$?
    PAYLOAD="$PAYLOAD$__line"
    [ "$__status" -eq 0 ] || break
  done
fi

# --- the five fields we send ------------------------------------------------
json_field() {
  printf '%s' "$PAYLOAD" \
    | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" \
    | head -n 1
}

# Reduced to a character set that cannot break out of a JSON string, so the body
# below is well-formed by construction with no escaping to get wrong. A path with
# spaces loses them; cwd is only ever displayed, never used as an identity, so
# that costs nothing.
safe() {
  printf '%s' "$1" | LC_ALL=C tr -cd 'A-Za-z0-9._:/@+-' | cut -c1-200
}

EVENT="${1:-}"
[ -n "$EVENT" ] || EVENT=$(json_field hook_event_name)
EVENT=$(safe "$EVENT")
[ -n "$EVENT" ] || exit 0

STATE=$(safe "${2:-}")
SESSION=$(safe "$(json_field session_id)")
CWD=$(safe "$(json_field cwd)")

AGENT="${CLAUDE_AGENT_ID:-}"
[ -n "$AGENT" ] || AGENT=$(json_field agent_id)
AGENT=$(safe "$AGENT")
[ -n "$AGENT" ] || AGENT="main"

BODY="{\"agentId\":\"$AGENT\",\"event\":\"$EVENT\",\"state\":\"$STATE\",\"sessionId\":\"$SESSION\",\"cwd\":\"$CWD\"}"

# --noproxy: a configured http_proxy would otherwise send this — token and all —
# out to a proxy host instead of to the loopback listener two inches away.
# Content-Type is not decoration: the app requires application/json precisely
# because it forces a CORS preflight that a hostile web page cannot satisfy.
curl --silent --output /dev/null \
     --noproxy '*' \
     --connect-timeout 0.2 --max-time 0.5 \
     --request POST \
     --header "Authorization: Bearer $TOKEN" \
     --header 'Content-Type: application/json' \
     --data-binary "$BODY" \
     "http://127.0.0.1:$PORT/agent-state" >/dev/null 2>&1

exit 0
