#!/bin/bash
# Builds the reaction-tuning harness against the REAL module sources.
#
# It links every source verbatim EXCEPT main.swift, which holds the app's top-level
# code and cannot go into a second executable. That is the point: nothing under test
# is a copy. The view and the rig come along because the wellness and agent modules
# reach for them, even though this harness draws nothing.
set -euo pipefail
cd "$(dirname "$0")/../.."

mkdir -p spikes/reactions/build
swiftc -O \
  -o spikes/reactions/build/ReactionSpike \
  Sources/LoafCat/Atlas.swift \
  Sources/LoafCat/CatStage.swift \
  Sources/LoafCat/CatModule.swift \
  Sources/LoafCat/PixelCanvas.swift \
  Sources/LoafCat/SpeechBubble.swift \
  Sources/LoafCat/Rig.swift \
  Sources/LoafCat/CatView.swift \
  Sources/LoafCat/Modules/*.swift \
  spikes/reactions/main.swift

echo "built spikes/reactions/build/ReactionSpike (run from the repo root)"
