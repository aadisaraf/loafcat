import CryptoKit
import Foundation

/// Fetches new versions from GitHub releases, and refuses to install one it cannot
/// prove came from the person holding the signing key.
///
/// =============================================================================
/// WHAT AN AUTO-UPDATER IS ACTUALLY RISKING
/// =============================================================================
/// Everything else in this app is careful about permissions it does not ask for. This
/// is the one piece that downloads code and arranges for it to run, so it is the piece
/// worth being paranoid about, and it is worth writing down exactly what is trusted.
///
/// A SHA-256 is not enough on its own. The checksum is published beside the file it
/// describes, on the same host, so it proves the download was not truncated or corrupted
/// in transit and proves nothing whatsoever about who produced it — anyone able to
/// replace the release can replace both halves.
///
/// So every update also carries an ECDSA P-256 signature over the file, made by a key
/// that lives on the maintainer's machine and never in this repository or in CI's
/// filesystem. `updateKey` below is the public half. A release that is unsigned, or
/// signed by anything else, is **never installed** — the app says a new version exists
/// and the human decides. That degradation is the point: it is what makes it safe to
/// ship this before any key exists.
///
/// What is still trusted, and cannot be removed from here: TLS to api.github.com, and
/// the integrity of the machine holding the private key.
///
/// =============================================================================
/// The swap, and why the update lands on the NEXT launch
/// =============================================================================
/// A verified download is unpacked into Application Support and nothing else happens
/// until the next start, where the installed bundle is renamed out of the way, the new
/// one is moved into place, and the app relaunches into it. That happens before the
/// panel exists, so it is invisible.
///
/// macOS will happily let a running bundle be renamed — the process holds its files by
/// inode — which is the same property the Windows build relies on for the same dance.
/// If the move fails after the rename, the old bundle goes straight back: an app that
/// fails to update is a nuisance, one that deletes itself trying is unanswerable.
final class Updater {
    private static let repo = "aadisaraf/loafcat"

    /// stderr, like every other diagnostic in this build. Never the payload of a
    /// response — only which version, and whether it verified.
    private static func note(_ line: String) {
        FileHandle.standardError.write(("loafcat: " + line + "\n").data(using: .utf8)!)
    }
    private func note(_ line: String) { Updater.note(line) }

    /// The public half of the update signing key, SPKI DER, base64.
    ///
    /// Compiled in rather than read from `assets/`, deliberately: everything in assets
    /// is meant to be replaceable by whoever owns the machine, and a trust anchor that
    /// can be swapped by editing a file inside the bundle is not one. The Windows build
    /// carries the same value in Updater.cs; they must agree.
    ///
    /// Empty disables automatic installation entirely and leaves only the notification.
    static let updateKey = """
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJrdvgJC0o3XeGwh1NW4rHnMkr2wBLk6rsmIyRVO4\
        lROV7sMwePs1fIvYWyIjb4kcUIRhc8247fv7CWgu+HyzyQ==
        """

    /// First check well after launch, then a few times a day. Slow on purpose: this is
    /// a desktop pet, not a browser, and an update that arrives six hours late has cost
    /// nobody anything.
    private static let firstCheck: TimeInterval = 45
    private static let interval: TimeInterval = 6 * 60 * 60

    private var timer: Timer?
    private let announce: (String) -> Void
    private var busy = false

    private(set) var availableVersion: String?
    private(set) var stagedAndReady = false

    static var enabled: Bool {
        get {
            let d = UserDefaults.standard
            return d.object(forKey: "updateAutomatically") == nil
                || d.bool(forKey: "updateAutomatically")
        }
        set { UserDefaults.standard.set(newValue, forKey: "updateAutomatically") }
    }

    init(announce: @escaping (String) -> Void) {
        self.announce = announce
    }

    func start() {
        timer = Timer.scheduledTimer(withTimeInterval: Self.firstCheck, repeats: false) { [weak self] _ in
            self?.check(quiet: true)
            self?.timer = Timer.scheduledTimer(
                withTimeInterval: Self.interval, repeats: true
            ) { [weak self] _ in
                self?.check(quiet: true)
            }
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    // MARK: - the swap, which happens before there is a panel

    private static var installedBundle: URL { Bundle.main.bundleURL }
    private static var stagingDir: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("loafcat/update", isDirectory: true)
    }
    private static var staged: URL { stagingDir.appendingPathComponent("LoafCat.app") }
    private static var superseded: URL {
        installedBundle.deletingLastPathComponent()
            .appendingPathComponent(".LoafCat.app.old")
    }

    /// Called before the app finishes launching. Returns true when it relaunched,
    /// meaning this process should exit and let the new one take over.
    static func applyStagedUpdate() -> Bool {
        let fm = FileManager.default

        // Last update's carcass. Removable now that nothing is running it.
        try? fm.removeItem(at: superseded)

        guard fm.fileExists(atPath: staged.appendingPathComponent(
            "Contents/MacOS/LoafCat").path) else { return false }

        do {
            try fm.moveItem(at: installedBundle, to: superseded)
            do {
                try fm.moveItem(at: staged, to: installedBundle)
            } catch {
                try? fm.moveItem(at: superseded, to: installedBundle)
                throw error
            }

            note("update  applied — restarting")
            let task = Process()
            task.executableURL = URL(fileURLWithPath: "/usr/bin/open")
            task.arguments = ["-n", installedBundle.path]
            try task.run()
            return true
        } catch {
            note("update: could not apply the staged version (\(error.localizedDescription))")
            try? fm.removeItem(at: staged)
            return false
        }
    }

    // MARK: - checking

    /// `quiet` is the scheduled check, which says nothing unless there is news. The
    /// Settings button passes false, because a button that can produce no visible result
    /// is a button people press twice.
    func check(quiet: Bool, then done: (() -> Void)? = nil) {
        if busy { done?(); return }
        busy = true

        if quiet && !Self.enabled { busy = false; done?(); return }

        Task { [weak self] in
            guard let self else { return }
            await self.run(quiet: quiet)
            await MainActor.run {
                self.busy = false
                done?()
            }
        }
    }

    private func run(quiet: Bool) async {
        guard let release = await latestRelease() else {
            if !quiet { await say("Could not reach GitHub.") }
            return
        }
        guard Self.isNewer(release.version, than: Branding.version) else {
            if !quiet { await say("loafcat \(Branding.version) is the latest version.") }
            return
        }

        await MainActor.run { self.availableVersion = release.version }

        guard Self.enabled else {
            await say("loafcat \(release.version) is available.")
            return
        }
        guard let signatureURL = release.signatureURL else {
            // Unsigned. Never installed automatically — see the note at the top.
            note("update: \(release.version) carries no signature — not installing")
            await say("loafcat \(release.version) is available (unsigned — install it yourself).")
            return
        }

        if await download(release, signatureURL: signatureURL) {
            await MainActor.run { self.stagedAndReady = true }
            await say("loafcat \(release.version) is ready — it will be running the "
                      + "next time you open the app.")
        } else if !quiet {
            await say("That update did not verify. Nothing was installed.")
        }
    }

    @MainActor private func say(_ message: String) { announce(message) }

    private struct Release {
        let version: String
        let assetURL: URL
        let checksumURL: URL?
        let signatureURL: URL?
    }

    private func latestRelease() async -> Release? {
        // `/latest` excludes prereleases, which is what keeps a release candidate from
        // reaching anyone who did not ask for it by name.
        guard let url = URL(string:
                "https://api.github.com/repos/\(Self.repo)/releases/latest") else { return nil }
        var request = URLRequest(url: url)
        request.timeoutInterval = 30
        request.setValue("loafcat/\(Branding.version)", forHTTPHeaderField: "User-Agent")

        guard let (data, _) = try? await URLSession.shared.data(for: request),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = root["tag_name"] as? String,
              let assets = root["assets"] as? [[String: Any]] else { return nil }

        let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        if version.isEmpty { return nil }

        var asset: URL?, sum: URL?, sig: URL?
        for a in assets {
            guard let name = a["name"] as? String,
                  let link = a["browser_download_url"] as? String,
                  let url = URL(string: link),
                  name.contains("macos") else { continue }
            if name.hasSuffix(".zip.sha256") { sum = url }
            else if name.hasSuffix(".zip.sig") { sig = url }
            else if name.hasSuffix(".zip") { asset = url }
        }
        guard let asset else { return nil }
        return Release(version: version, assetURL: asset, checksumURL: sum, signatureURL: sig)
    }

    /// Downloads, verifies, and stages. Returns false and leaves nothing behind if
    /// anything about it fails to check out.
    private func download(_ release: Release, signatureURL: URL) async -> Bool {
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("loafcat-update-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: work) }

        do {
            let payload = try await fetch(release.assetURL)

            if let sumURL = release.checksumURL,
               let published = String(data: try await fetch(sumURL), encoding: .utf8)?
                   .split(whereSeparator: \.isWhitespace).first?.lowercased() {
                let actual = SHA256.hash(data: payload)
                    .map { String(format: "%02x", $0) }.joined()
                guard published == actual else {
                    note("update: checksum mismatch, discarding \(release.version)")
                    return false
                }
            }

            let signature = try await fetch(signatureURL)
            guard Self.verify(payload: payload, signature: signature) else {
                note("update: signature does not verify, discarding \(release.version)")
                return false
            }

            try fm.createDirectory(at: work, withIntermediateDirectories: true)
            let zip = work.appendingPathComponent("loafcat.zip")
            try payload.write(to: zip)

            let unpack = work.appendingPathComponent("unpack", isDirectory: true)
            try fm.createDirectory(at: unpack, withIntermediateDirectories: true)
            let ditto = Process()
            ditto.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
            ditto.arguments = ["-x", "-k", zip.path, unpack.path]
            try ditto.run()
            ditto.waitUntilExit()
            guard ditto.terminationStatus == 0 else {
                note("update: the package would not unpack")
                return false
            }

            let app = unpack.appendingPathComponent("LoafCat.app")
            guard fm.fileExists(atPath: app.appendingPathComponent(
                "Contents/MacOS/LoafCat").path) else {
                note("update: no LoafCat.app in that package")
                return false
            }

            // Only now, once there is a whole verified bundle to move: staging is a
            // rename, so nothing half-written is ever left where the next launch looks.
            try fm.createDirectory(at: Self.stagingDir, withIntermediateDirectories: true)
            try? fm.removeItem(at: Self.staged)
            try fm.moveItem(at: app, to: Self.staged)
            note("update  staged \(release.version), applies on next start")
            return true
        } catch {
            note("update: download failed (\(error.localizedDescription))")
            return false
        }
    }

    private func fetch(_ url: URL) async throws -> Data {
        var request = URLRequest(url: url)
        request.timeoutInterval = 60
        request.setValue("loafcat/\(Branding.version)", forHTTPHeaderField: "User-Agent")
        let (data, _) = try await URLSession.shared.data(for: request)
        return data
    }

    /// ECDSA P-256 over the file, DER-encoded, against the compiled-in public key.
    static func verify(payload: Data, signature: Data) -> Bool {
        guard !updateKey.isEmpty,
              let der = Data(base64Encoded: updateKey),
              let key = try? P256.Signing.PublicKey(derRepresentation: der),
              let sig = try? P256.Signing.ECDSASignature(derRepresentation: signature)
        else { return false }
        return key.isValidSignature(sig, for: payload)
    }

    /// Strictly greater, component by component. A build that somehow finds itself newer
    /// than the release must never "update" backwards into a loop.
    static func isNewer(_ candidate: String, than current: String) -> Bool {
        func parts(_ v: String) -> [Int] {
            v.split(separator: "-")[0].split(separator: ".").map { Int($0) ?? 0 }
        }
        let a = parts(candidate), b = parts(current)
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0
            let y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }
}
