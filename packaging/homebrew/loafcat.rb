# Homebrew cask for loafcat.
#
# This file does not live anywhere useful yet. To make `brew install --cask
# loafcat` work, it has to be in a *tap*, which Homebrew requires to be its own
# repository named `homebrew-<something>`:
#
#   1. Create a public repo called `homebrew-loafcat`.
#   2. Copy this file to `Casks/loafcat.rb` in it.
#   3. Fill in the sha256 below from the release's published .sha256 file.
#
# Then anyone can:
#
#   brew tap aadisaraf/loafcat
#   brew install --cask loafcat
#
# ---------------------------------------------------------------------------
# The `no_quarantine` flag matters here
# ---------------------------------------------------------------------------
# Homebrew quarantines cask downloads by default, which for an unnotarised app
# means the user still hits the blocked-app dialog and Homebrew has bought them
# nothing. `no_quarantine` says the same thing install.sh says: a person typing
# an install command has already made the deliberate choice Gatekeeper's dialog
# exists to ask about.
#
# Note this cask cannot go into homebrew/cask itself. That repository requires
# software to be notarised or open-source-and-well-known, and declines
# no_quarantine casks. A personal tap is the supported way to do this.
cask "loafcat" do
  version "0.1.0"
  sha256 "REPLACE_WITH_THE_PUBLISHED_SHA256"

  url "https://github.com/aadisaraf/loafcat/releases/download/v#{version}/loafcat-#{version}.dmg",
      verified: "github.com/aadisaraf/loafcat/"
  name "loafcat"
  desc "Pixel cat that reacts to your cursor, your typing, and Claude Code"
  homepage "https://github.com/aadisaraf/loafcat"

  depends_on macos: ">= :ventura"

  app "LoafCat.app"

  # Settings are deliberately not in `zap` above the line -- someone reinstalling
  # should keep their theme and their timers. `brew uninstall --zap` is the
  # explicit "and everything else" request, so it takes them.
  zap trash: [
    "~/Library/Preferences/dev.loafcat.app.plist",
    "~/.loafcat",
  ]

  caveats <<~EOS
    loafcat lives in the menu bar as a cat's face. Settings and Quit are in
    that menu; there is no Dock icon unless you turn one on in Settings.

    If you connected it to Claude Code, disconnect from Settings before
    uninstalling, so its hook entries come out of ~/.claude/settings.json.
  EOS
end
