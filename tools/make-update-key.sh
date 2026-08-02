#!/usr/bin/env bash
#
# Generates the key that signs releases for the auto-updater, and tells you where to
# put each half.
#
#   ./tools/make-update-key.sh
#
# ---------------------------------------------------------------------------
# Why an auto-updater needs a key at all
# ---------------------------------------------------------------------------
# The published SHA-256 sits next to the file it describes, on the same host. It proves
# a download was not corrupted in transit; it proves nothing about who produced it,
# because anyone able to replace the release can replace both halves. An updater that
# checks only a checksum will install whatever the release contains.
#
# The signature moves the trust from "whoever can write to the GitHub release" to
# "whoever holds this private key" -- which is you, on your machine, and nowhere else.
# A release built by a compromised token is then something installed copies refuse.
#
# loafcat is not code-signed for Gatekeeper or SmartScreen -- that needs Apple's $99/year
# programme and an Authenticode certificate respectively. This is a different thing and
# it is free: it protects the update channel rather than the first install.
#
# ---------------------------------------------------------------------------
# Rotating
# ---------------------------------------------------------------------------
# Run this again. The new public key ships in the NEXT release, so anyone still on an
# older version will keep verifying against the old one -- meaning you must sign with
# BOTH keys for one release, or accept that older copies stop auto-updating and say so.
# That is the cost of a key that has no revocation story, and it is the honest one for a
# project this size.
#
# If the private key is lost, nothing breaks: releases go out unsigned, installed copies
# report the new version and decline to install it, and people update by hand.

set -euo pipefail

KEY_DIR="${LOAFCAT_KEY_DIR:-$HOME/.loafcat}"
KEY="$KEY_DIR/update-signing-key.pem"

mkdir -p "$KEY_DIR"
chmod 700 "$KEY_DIR"

if [ -f "$KEY" ]; then
  echo "A key already exists at $KEY"
  echo "Delete it first if you really mean to rotate — read the note above about what"
  echo "that does to copies already installed."
  echo
else
  # P-256 rather than Ed25519: .NET 8 has no Ed25519, CryptoKit does, and a signature
  # scheme both builds can verify with nothing but their standard library is worth more
  # than the newer curve. Both sides are verified against openssl output by tools in CI.
  openssl ecparam -name prime256v1 -genkey -noout -out "$KEY"
  chmod 600 "$KEY"
  echo "Generated $KEY"
  echo
fi

PUB=$(openssl ec -in "$KEY" -pubout -outform DER 2>/dev/null | base64 | tr -d '\n')

cat <<EOF
1. Put the PRIVATE half in the repository's secrets, so the release workflow can sign:

     gh secret set LOAFCAT_UPDATE_KEY --repo aadisaraf/loafcat < "$KEY"

   Back the file up somewhere you will still have it in a year. It is the only thing
   that can sign an update anyone's copy will accept.

2. The PUBLIC half is compiled into both apps. It is already there if it matches; if you
   have just rotated, replace it in BOTH files — they must agree:

     Sources/LoafCat/Updater.swift   updateKey
     windows/LoafCat/Updater.cs      UpdateKey

$PUB

3. Check what a build is actually carrying:

     ./scripts/check-update-key.sh

EOF
