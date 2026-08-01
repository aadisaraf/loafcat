#!/bin/bash
# Builds the reaction-tuning harness against the REAL module sources.
#
# It links Atlas / Stage / CatModule / Modules verbatim -- only main.swift and the
# view layer are left out, because main.swift holds the app's top-level code and the
# harness draws nothing. That is the point: nothing under test is a copy.
set -euo pipefail
cd "$(dirname "$0")/../.."

mkdir -p spikes/reactions/build
swiftc -O \
  -o spikes/reactions/build/ReactionSpike \
  Sources/LoafCat/Atlas.swift \
  Sources/LoafCat/Stage.swift \
  Sources/LoafCat/CatModule.swift \
  Sources/LoafCat/Modules/*.swift \
  spikes/reactions/main.swift

echo "built spikes/reactions/build/ReactionSpike (run from the repo root)"
