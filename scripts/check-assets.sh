#!/bin/bash
# Regenerates every generated asset and fails if anything moved.
#
# The provenance claim in the README -- that no pixel here was drawn by hand or
# traced from anything -- is only worth something if it is enforced. This is the
# enforcement. Run it locally before pushing art changes; CI runs it too.
set -euo pipefail
cd "$(dirname "$0")/.."

for theme in mono tuxedo cream; do
  python3 tools/generate_art.py --theme "$theme" > /dev/null
done

# The icon and the disk image background composite the mono theme's own parts, so
# they are held to the same rule -- otherwise the cat could change and the thing
# people download could quietly keep showing the old one.
python3 tools/generate_icon.py > /dev/null
python3 tools/generate_dmg_background.py > /dev/null

# AppIcon.icns and background.tiff are excluded, and the exclusion is the honest
# thing rather than a convenience. Neither is produced by this project: they are
# containers that `iconutil` and `tiffutil` pack around PNGs we have already
# compared byte for byte above. Their bytes therefore track the macOS version of
# whatever machine ran the build, so diffing them would fail on a runner whose
# only crime is being a different macOS to the last person's laptop -- while
# telling us nothing the PNG comparison has not already told us.
EXCLUDE=(':!assets/icon/AppIcon.icns' ':!assets/dmg/background.tiff')

if ! git diff --quiet -- assets/ "${EXCLUDE[@]}"; then
  echo "::error::assets/ differs after regeneration — art must come from tools/"
  git diff --stat -- assets/ "${EXCLUDE[@]}"
  exit 1
fi

echo "assets reproduce exactly"
