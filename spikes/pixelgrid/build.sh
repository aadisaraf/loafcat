#!/bin/bash
# Builds the pixel-grid check against the real Atlas / Rig / CatView.
set -euo pipefail
cd "$(dirname "$0")/../.."

mkdir -p spikes/pixelgrid/build
swiftc -O \
  -o spikes/pixelgrid/build/PixelGridSpike \
  Sources/LoafCat/Atlas.swift \
  Sources/LoafCat/Stage.swift \
  Sources/LoafCat/CatModule.swift \
  Sources/LoafCat/Rig.swift \
  Sources/LoafCat/CatView.swift \
  spikes/pixelgrid/main.swift

echo "built spikes/pixelgrid/build/PixelGridSpike (run from the repo root)"
