#!/usr/bin/env bash
#
# The two ports must carry the same update signing key, and it must be the public half
# of the key CI signs with. Nothing else in the build checks this, and the failure is
# silent in the worst possible way: releases keep going out, every installed copy quietly
# declines to install them, and nobody finds out until someone asks why they are still on
# an old version.
#
# Run by CI on every build.

set -euo pipefail
cd "$(dirname "$0")/.."

fail() { echo "check-update-key: $1" >&2; exit 1; }

# The base64 is wrapped across two string literals in both files, so it is read as
# "every base64-looking run in the few lines after the identifier, concatenated".
# python3 rather than awk or sed: the repository already needs it for the art pipeline,
# and the Swift form ends with the same delimiter it begins with, which awk cannot
# bracket without special-casing the first line.
extract() {
  python3 -c "
import re, sys
lines = open(sys.argv[1]).read().splitlines()
for i, line in enumerate(lines):
    if sys.argv[2] in line:
        runs = []
        for follow in lines[i:i + 8]:
            runs += re.findall(r'[A-Za-z0-9+/=]{16,}', follow)
        print(''.join(runs))
        break
" "$1" "$2"
}

SWIFT=$(extract Sources/LoafCat/Updater.swift "static let updateKey")
CSHARP=$(extract windows/LoafCat/Updater.cs "public const string UpdateKey")

[ -n "$SWIFT" ]  || fail "no update key found in Sources/LoafCat/Updater.swift"
[ -n "$CSHARP" ] || fail "no update key found in windows/LoafCat/Updater.cs"

if [ "$SWIFT" != "$CSHARP" ]; then
  echo "check-update-key: the two ports carry DIFFERENT update keys." >&2
  echo "  swift  $SWIFT" >&2
  echo "  csharp $CSHARP" >&2
  fail "one platform would stop accepting updates. See tools/make-update-key.sh."
fi

# It has to be a key, not merely the same string twice.
if ! printf '%s' "$SWIFT" | base64 -d 2>/dev/null |
     openssl ec -pubin -inform DER -noout 2>/dev/null; then
  fail "that value is not a public key openssl can read"
fi

# And, when the private half is to hand, the matching one.
KEY="${LOAFCAT_KEY_DIR:-$HOME/.loafcat}/update-signing-key.pem"
if [ -f "$KEY" ]; then
  MINE=$(openssl ec -in "$KEY" -pubout -outform DER 2>/dev/null | base64 | tr -d '\n')
  if [ "$MINE" != "$SWIFT" ]; then
    fail "the compiled-in key is not the public half of $KEY — releases would be signed
      with a key the apps do not trust. See tools/make-update-key.sh."
  fi
  echo "update key: both ports agree, and it matches the private key on this machine"
else
  echo "update key: both ports agree (no private key here to compare against)"
fi
