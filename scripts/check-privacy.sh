#!/bin/bash
# Fails the build if code appears that would trigger a macOS permission prompt.
#
# loafcat's promise is that it asks for nothing, and that promise is only worth
# something if it is enforced mechanically. A reviewer will not notice a single
# added CGEventTap in a large diff; this will.
#
# Verified in spikes/RESULTS.md: cursor position, keystroke COUNTS, click counts,
# scroll counts, per-type idle time, frontmost app, and window owner/PID/bounds are
# all available with Accessibility and Input Monitoring denied and Screen Recording
# off. Only window TITLES are gated, and a desktop pet does not need titles.
#
# Run standalone, or automatically as part of ./build.sh.
set -uo pipefail
cd "$(dirname "$0")/.."

fail=0

# Comments are stripped before matching, because this file's own warnings — and
# the ones in main.swift telling people not to use these APIs — would otherwise
# trip every rule. Line numbers are preserved so the report still points at the
# real location.
SCAN=$(mktemp)
trap 'rm -f "$SCAN"' EXIT
while IFS= read -r f; do
  case "$f" in
    */check-privacy.sh) continue ;;
  esac
  # Blank out // line comments and # comments, keeping the line count intact.
  sed -e 's://.*::' -e 's:^[[:space:]]*#.*::' "$f" \
    | awk -v F="$f" '{ if ($0 ~ /[^[:space:]]/) printf "%s:%d:%s\n", F, NR, $0 }'
done < <(find Sources tools hooks scripts -type f \
           \( -name '*.swift' -o -name '*.py' -o -name '*.sh' \) 2>/dev/null) > "$SCAN"

# Each entry: regex | why it is banned | what to use instead
check() {
  local pattern="$1" why="$2" instead="$3"
  local hits
  hits=$(grep -E "$pattern" "$SCAN" || true)
  if [ -n "$hits" ]; then
    echo "BLOCKED: $why"
    echo "  use instead: $instead"
    echo "$hits" | sed 's/^/    /'
    echo
    fail=1
  fi
}

check 'CGEventTapCreate|kCGEventTapOptionDefault' \
  'an event tap is an active filter that can read and suppress every keystroke system-wide, and forces the full Accessibility prompt' \
  'CGEventSource.counterForEventType for rates, secondsSinceLastEventType for idle'

check 'uiohook|iohook|node-global-key-listener|robotjs' \
  'these bundle an active event tap and additionally break code-signing' \
  'CGEventSource counters'

check 'addGlobalMonitorForEvents\(matching: *\[?\.key|NSEvent\.addGlobalMonitorForEvents.*keyDown|NSEvent\.addGlobalMonitorForEvents.*keyUp' \
  'a global KEYBOARD monitor requires Accessibility (global MOUSE monitors do not)' \
  'CGEventSource.counterForEventType(.combinedSessionState, eventType: .keyDown)'

check 'AXIsProcessTrustedWithOptions|kAXTrustedCheckOptionPrompt' \
  'this variant shows the Accessibility dialog' \
  'AXIsProcessTrusted() reports the same thing without prompting'

check 'CGRequestListenEventAccess|CGRequestScreenCaptureAccess|CGRequestPostEventAccess' \
  'these explicitly request a TCC permission' \
  'nothing — the feature needing it is the wrong design; see CLAUDE.md'

check 'CGWindowListCreateImage|SCStream|SCShareableContent|CGDisplayStream|CGDisplayCreateImage' \
  'screen capture requires Screen Recording, which also carries a 30-day re-prompt' \
  'CGWindowListCopyWindowInfo gives owner/PID/layer/bounds permission-free'

check 'kCGWindowName' \
  'window TITLES are the one field gated behind Screen Recording' \
  'match on kCGWindowOwnerName or the bounds instead'

check '\.hidSystemState|\.privateState' \
  'these event-source states BLOCK INDEFINITELY for an unprivileged process — no error, no prompt, just a hang' \
  '.combinedSessionState'

# The privacy claim is that key IDENTITY never enters the process. Anything reading
# a keycode or a character would break that, so it is banned outright.
check 'kCGKeyboardEventKeycode|NSEvent.*\.characters|event\.keyCode' \
  'reading key identity would break the structural content-blindness claim' \
  'counts only — the cat needs a rate, never which key'

if [ "$fail" -ne 0 ]; then
  echo "privacy check FAILED — see CLAUDE.md 'Privacy' section"
  exit 1
fi

echo "privacy check passed: no permission-requiring APIs"
