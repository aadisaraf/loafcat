import AppKit
import Darwin
import Security

// =============================================================================
// The cat reacts to Claude Code.
//
// Claude Code fires hooks; a tiny shell script POSTs the event to a loopback
// listener this module owns; the module turns a stream of events into one
// coherent mood and asks the stage for the matching reaction.
//
// Three things in here are load-bearing and easy to get wrong:
//
//  1. SECURITY. A loopback HTTP port is reachable from any web page the user has
//     open. See `AgentEndpoint.handle` for the four gates and why each one is
//     needed. Payloads carry prompt text and shell commands, so nothing from a
//     request body is ever written to a log.
//
//  2. NEVER STALLING THE USER. Everything on the hook side is fire-and-forget
//     with sub-second timeouts and an unconditional `exit 0`. If this app is not
//     running, the hook must be a silent no-op. See hooks/loafcat-hook.sh.
//
//  3. `Stop` DOES NOT FIRE ON ESC. A session that only ever ends via `Stop`
//     gets stuck looking busy the first time the user interrupts, which is
//     constantly. Hence the idle backstop in `expire`.
// =============================================================================

// MARK: - Hook installation

/// Merges our hooks into `~/.claude/settings.json`, and takes them out again.
///
/// Read-modify-write only. That file is the user's, it is shared with every other
/// tool that installs hooks, and clobbering it would break their setup in a way
/// they would blame on us and never find.
enum HookInstaller {
    /// The single source of truth for what we register and what each event means.
    ///
    /// `state` is passed to the script as `$2` and travels in the payload, so the
    /// mapping is visible in the user's settings file rather than buried in a
    /// binary. `AgentModule` falls back to this same table when a payload arrives
    /// without a state, which is what keeps there from being a second copy.
    ///
    /// Every entry is async with a short explicit timeout. A sync hook BLOCKS
    /// Claude's execution and the default timeout is 600 seconds — a hook that
    /// misbehaves would stall the user's real coding session, which is the one
    /// failure mode this feature is not allowed to have.
    ///
    /// SessionEnd gets 2s rather than 5s because its timeout is charged against
    /// the whole session's shutdown budget: raising it makes every quit slower.
    ///
    /// `MessageDisplay` is deliberately absent and must stay that way — it holds
    /// every streamed batch until the hook returns.
    ///
    /// SubagentStop maps to `working`, not `complete`: subagents finish in
    /// parallel and at arbitrary times, and letting them fire the hop would have
    /// the cat leaping around mid-task. The parent is still busy, so refreshing
    /// the busy timer is the honest reading.
    static let events: [(event: String, state: String, timeout: Int, matcher: String?)] = [
        ("SessionStart",       "idle",         5, nil),
        ("SessionEnd",         "idle",         2, nil),
        ("UserPromptSubmit",   "thinking",     5, nil),
        ("PreToolUse",         "working",      5, "*"),
        ("PostToolUse",        "working",      5, "*"),
        ("PostToolUseFailure", "error",        5, "*"),
        ("Stop",               "complete",     5, nil),
        ("StopFailure",        "error",        5, nil),
        ("Notification",       "notification", 5, nil),
        ("PermissionRequest",  "notification", 5, "*"),
        ("SubagentStart",      "working",      5, nil),
        ("SubagentStop",       "working",      5, nil),
    ]

    /// Every command we write contains this, and nothing else does. It is how
    /// "remove only our entries" stays precise.
    static let marker = "loafcat-hook.sh"

    enum Failure: Error, CustomStringConvertible {
        case unreadable(String)
        case notJSON(String)
        case unsafePath(String)

        var description: String {
            switch self {
            case .unreadable(let p): return "could not read \(p)"
            case .notJSON(let p):
                return "\(p) is not valid JSON — refusing to touch it, fix it by hand first"
            case .unsafePath(let p): return "refusing to write a hook command for \(p)"
            }
        }
    }

    static var settingsURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".claude/settings.json")
    }

    // --- the pure part, so the merge can be exercised without a real settings file

    /// Our entries, merged in additively. Idempotent: it strips ours first, so
    /// installing twice leaves exactly one copy rather than stacking duplicates.
    ///
    /// Hook entries merge additively across settings levels and identical handlers
    /// are deduplicated, so appending is safe — but every existing entry has to
    /// survive, which is why this reaches in and rebuilds only the arrays it
    /// touches instead of assembling a fresh `hooks` object.
    static func merged(into root: [String: Any], script: String) throws -> [String: Any] {
        guard !script.contains("\""), !script.contains("\\") else {
            throw Failure.unsafePath(script)
        }
        var out = stripped(from: root)
        var hooks = out["hooks"] as? [String: Any] ?? [:]

        for spec in events {
            var groups = hooks[spec.event] as? [[String: Any]] ?? []
            let entry: [String: Any] = [
                "type": "command",
                "command": "\"\(script)\" \(spec.event) \(spec.state)",
                "timeout": spec.timeout,
                "async": true,
            ]
            var group: [String: Any] = ["hooks": [entry]]
            if let m = spec.matcher { group["matcher"] = m }
            groups.append(group)
            hooks[spec.event] = groups
        }

        out["hooks"] = hooks
        return out
    }

    /// Removes only our entries, leaving every other hook — and every group that
    /// merely sits next to one of ours — exactly as it was.
    static func stripped(from root: [String: Any]) -> [String: Any] {
        var out = root
        guard var hooks = out["hooks"] as? [String: Any] else { return out }

        for (event, value) in hooks {
            guard let groups = value as? [[String: Any]] else { continue }
            var kept: [[String: Any]] = []
            for group in groups {
                guard let inner = group["hooks"] as? [[String: Any]] else {
                    kept.append(group)      // shape we do not recognise: hands off
                    continue
                }
                let survivors = inner.filter { !isOurs($0) }
                if survivors.count == inner.count {
                    kept.append(group)      // nothing of ours in here
                } else if !survivors.isEmpty {
                    var g = group
                    g["hooks"] = survivors  // ours removed, someone else's kept
                    kept.append(g)
                }
                // else: the group held only our entry, so the group goes too
            }
            if kept.isEmpty {
                hooks.removeValue(forKey: event)
            } else {
                hooks[event] = kept
            }
        }

        if hooks.isEmpty {
            out.removeValue(forKey: "hooks")
        } else {
            out["hooks"] = hooks
        }
        return out
    }

    static func isOurs(_ hook: [String: Any]) -> Bool {
        (hook["command"] as? String)?.contains(marker) ?? false
    }

    static func isInstalled(in root: [String: Any]) -> Bool {
        guard let hooks = root["hooks"] as? [String: Any] else { return false }
        for (_, value) in hooks {
            for group in (value as? [[String: Any]] ?? []) {
                for hook in (group["hooks"] as? [[String: Any]] ?? []) where isOurs(hook) {
                    return true
                }
            }
        }
        return false
    }

    // --- the file part

    static func read(_ url: URL) throws -> [String: Any] {
        guard FileManager.default.fileExists(atPath: url.path) else { return [:] }
        guard let data = try? Data(contentsOf: url) else {
            throw Failure.unreadable(url.path)
        }
        if data.isEmpty { return [:] }
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw Failure.notJSON(url.path)
        }
        return obj
    }

    /// Writes the settings back, after copying the original aside.
    ///
    /// The backup is taken before the first byte is written and named so it is
    /// obvious what made it and what to do with it.
    static func write(_ root: [String: Any], to url: URL) throws {
        let fm = FileManager.default
        try fm.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true)

        // Remember the mode before touching anything: an atomic write is a
        // rename over the top, so without this a 0600 settings file would come
        // back 0644 and we would have quietly widened the user's permissions.
        let existingMode = (try? fm.attributesOfItem(atPath: url.path))?[.posixPermissions]

        if fm.fileExists(atPath: url.path) {
            let backup = url.appendingPathExtension("loafcat-backup")
            if let original = try? Data(contentsOf: url) {
                try original.write(to: backup)
            }
        }

        let data = try JSONSerialization.data(
            withJSONObject: root, options: [.prettyPrinted, .sortedKeys])
        try (unescapeSlashes(data) + Data("\n".utf8)).write(to: url, options: .atomic)

        if let mode = existingMode {
            try? fm.setAttributes([.posixPermissions: mode], ofItemAtPath: url.path)
        }
    }

    /// Turns `\/` back into `/`.
    ///
    /// JSONSerialization escapes every forward slash. It is valid JSON and parses
    /// identically, but it rewrites every path in a file the user reads and edits
    /// by hand — connecting a desktop pet should not leave their settings looking
    /// like that. Done with a scanner rather than a search-and-replace because a
    /// blind one corrupts `\\/`, which is a literal backslash followed by a slash.
    static func unescapeSlashes(_ data: Data) -> Data {
        var out = Data(capacity: data.count)
        var inString = false
        var i = data.startIndex
        while i < data.endIndex {
            let byte = data[i]
            if inString, byte == UInt8(ascii: "\\"), data.index(after: i) < data.endIndex {
                let next = data[data.index(after: i)]
                if next == UInt8(ascii: "/") {
                    out.append(next)            // drop the backslash
                } else {
                    out.append(byte)
                    out.append(next)            // keep the escape intact
                }
                i = data.index(i, offsetBy: 2)
                continue
            }
            if byte == UInt8(ascii: "\"") { inString.toggle() }
            out.append(byte)
            i = data.index(after: i)
        }
        return out
    }

    static func isInstalled() -> Bool {
        ((try? read(settingsURL)).map(isInstalled(in:))) ?? false
    }
}

// MARK: - Loopback endpoint

/// A single-purpose HTTP listener on 127.0.0.1, on a port the kernel picks.
///
/// Raw sockets rather than a framework because the security rules here are all
/// about exactly which requests are refused, and that is easiest to read — and to
/// audit — when the request is parsed in one place with no middleware in between.
final class AgentEndpoint {
    /// Only the five fields we actually use. Hook payloads also carry the user's
    /// prompt text and the full shell command of every tool call; those are never
    /// read out of the body, never stored and never logged.
    struct Event {
        let agentId: String
        let event: String
        let state: String
        let sessionId: String
        let cwd: String
    }

    /// 8KB. A legitimate payload from the hook script is a few hundred bytes.
    private let maxBody = 8 * 1024
    private let maxRequest = 16 * 1024

    private(set) var port: UInt16 = 0
    let token: String
    private let tokenBytes: [UInt8]

    private var listenFD: Int32 = -1
    private let onEvent: (Event) -> Void
    private let connections = DispatchQueue(
        label: "dev.loafcat.agent.connection", attributes: .concurrent)

    init(onEvent: @escaping (Event) -> Void) {
        self.onEvent = onEvent
        // 32 bytes from the system CSPRNG. Long enough that guessing it is not a
        // strategy, so the other three gates only have to defend against a page
        // that never learns it in the first place.
        var raw = [UInt8](repeating: 0, count: 32)
        if SecRandomCopyBytes(kSecRandomDefault, raw.count, &raw) != errSecSuccess {
            for i in raw.indices { raw[i] = UInt8.random(in: 0...255) }
        }
        self.token = Data(raw).base64EncodedString()
        self.tokenBytes = Array(self.token.utf8)
    }

    enum Failure: Error, CustomStringConvertible {
        case syscall(String, Int32)
        var description: String {
            switch self {
            case .syscall(let what, let e):
                return "agent endpoint: \(what) failed (\(String(cString: strerror(e))))"
            }
        }
    }

    func start() throws {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { throw Failure.syscall("socket", errno) }

        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        // Port 0: the kernel assigns a free one, read back with getsockname. A
        // fixed port would collide with whatever else the user runs and would
        // make the endpoint findable without reading the token file.
        addr.sin_port = 0
        // Loopback ONLY. Binding INADDR_ANY would put this on every interface the
        // machine has, including whatever coffee-shop wifi it is on.
        addr.sin_addr = in_addr(s_addr: INADDR_LOOPBACK.bigEndian)

        let bound = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0 else {
            let e = errno; close(fd); throw Failure.syscall("bind", e)
        }

        var actual = sockaddr_in()
        var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        _ = withUnsafeMutablePointer(to: &actual) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                getsockname(fd, $0, &len)
            }
        }
        port = UInt16(bigEndian: actual.sin_port)

        guard listen(fd, 16) == 0 else {
            let e = errno; close(fd); throw Failure.syscall("listen", e)
        }
        listenFD = fd

        // A dedicated thread rather than a dispatch queue: `accept` blocks
        // forever by design, and parking a libdispatch worker on it for the
        // lifetime of the process is exactly what dispatch asks you not to do.
        let t = Thread { [weak self] in self?.acceptLoop() }
        t.name = "dev.loafcat.agent.accept"
        t.start()
    }

    func stop() {
        let fd = listenFD
        listenFD = -1
        if fd >= 0 { close(fd) }
    }

    private func acceptLoop() {
        while true {
            let fd = accept(listenFD, nil, nil)
            if fd < 0 {
                if errno == EINTR { continue }
                return                      // listener closed: we are shutting down
            }
            var on: Int32 = 1
            // Without SO_NOSIGPIPE, writing a response to a client that already
            // hung up (curl --max-time 0.5 does exactly that) kills the process.
            setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
            // A client that connects and then says nothing must not hold a worker
            // open indefinitely.
            var tv = timeval(tv_sec: 2, tv_usec: 0)
            setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
            setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
            connections.async { [weak self] in self?.handle(fd) }
        }
    }

    // MARK: request handling

    /// The four gates.
    ///
    /// The threat is not a program on the machine — anything running as the user
    /// can read the token file anyway. It is a **web page**: any site the user has
    /// open can POST to any localhost port, no permission needed, and the browser
    /// will happily send it.
    ///
    ///  1. Bearer token, compared in constant time. A page cannot read
    ///     `~/.loafcat/endpoint.json`, so it cannot produce one.
    ///  2. `Content-Type: application/json`. This is the important one. A form
    ///     POST or a `sendBeacon` is a *simple request* and needs no preflight, so
    ///     a page can fire one blind. `application/json` is not on the simple list,
    ///     which forces an `OPTIONS` preflight — and we answer no preflight, so the
    ///     browser refuses to send the real request at all.
    ///  3. Any `Origin` header at all is a refusal. A browser attaches one to every
    ///     cross-origin request; our hook script never sends one.
    ///  4. Method and path pinned, body capped at 8KB.
    private func handle(_ fd: Int32) {
        defer { close(fd) }
        guard let request = readRequest(fd) else {
            respond(fd, 400, "Bad Request"); return
        }
        guard !request.oversize else { reject(fd, 413, "Payload Too Large"); return }
        let (head, body) = (request.head, request.body)

        var lines = head.split(separator: "\r\n", omittingEmptySubsequences: false)
        guard !lines.isEmpty else { respond(fd, 400, "Bad Request"); return }
        let requestLine = lines.removeFirst().split(separator: " ")
        guard requestLine.count >= 2 else { respond(fd, 400, "Bad Request"); return }

        var headers: [String: String] = [:]
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let name = line[..<colon].lowercased()
            let value = line[line.index(after: colon)...]
                .trimmingCharacters(in: .whitespaces)
            headers[name] = value
        }

        guard requestLine[0] == "POST" else { reject(fd, 405, "Method Not Allowed"); return }
        guard requestLine[1] == "/agent-state" else { reject(fd, 404, "Not Found"); return }
        guard headers["origin"] == nil else { reject(fd, 403, "Forbidden"); return }

        let mediaType = (headers["content-type"] ?? "")
            .split(separator: ";").first?
            .trimmingCharacters(in: .whitespaces).lowercased() ?? ""
        guard mediaType == "application/json" else {
            reject(fd, 415, "Unsupported Media Type"); return
        }
        guard body.count <= maxBody else { reject(fd, 413, "Payload Too Large"); return }

        let presented = (headers["authorization"] ?? "")
        guard presented.hasPrefix("Bearer "),
              Self.constantTimeEqual(Array(presented.dropFirst(7).utf8), tokenBytes)
        else { reject(fd, 401, "Unauthorized"); return }

        guard let obj = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any]
        else { respond(fd, 400, "Bad Request"); return }

        // Only these five keys are ever read. Truncated because they end up as
        // dictionary keys and there is no reason for any of them to be long.
        func field(_ key: String) -> String {
            String((obj[key] as? String ?? "").prefix(200))
        }
        onEvent(Event(
            agentId: field("agentId"),
            event: field("event"),
            state: field("state"),
            sessionId: field("sessionId"),
            cwd: field("cwd")))

        respond(fd, 204, "No Content")
    }

    /// Refusals are logged by reason only — never the request, never a header
    /// value, and never one byte of the body.
    private func reject(_ fd: Int32, _ status: Int, _ reason: String) {
        FileHandle.standardError.write(
            Data("loafcat agent: refused a request (\(status) \(reason))\n".utf8))
        respond(fd, status, reason)
    }

    private func readRequest(_ fd: Int32) -> (head: String, body: Data, oversize: Bool)? {
        var data = Data()
        var buf = [UInt8](repeating: 0, count: 2048)
        var headEnd: Int?
        var contentLength = 0
        var oversize = false

        while data.count < maxRequest {
            if let end = headEnd, data.count >= end + contentLength { break }
            let n = recv(fd, &buf, buf.count, 0)
            if n <= 0 { break }
            data.append(contentsOf: buf[0..<n])

            if headEnd == nil, let r = data.range(of: Data("\r\n\r\n".utf8)) {
                headEnd = r.upperBound
                let head = String(decoding: data[..<r.lowerBound], as: UTF8.self)
                for line in head.split(separator: "\r\n")
                where line.lowercased().hasPrefix("content-length:") {
                    contentLength = Int(
                        line.dropFirst("content-length:".count)
                            .trimmingCharacters(in: .whitespaces)) ?? 0
                }
                // Refuse an oversized body from its declared length, before
                // reading it — the point of a cap is not to buffer the thing
                // first and complain afterwards.
                if contentLength > maxBody { oversize = true; break }
            }
        }

        guard let end = headEnd, let sep = data.range(of: Data("\r\n\r\n".utf8)) else {
            return nil
        }
        // An undeclared body that ran past the cap counts too.
        if data.count - end > maxBody { oversize = true }
        let head = String(decoding: data[..<sep.lowerBound], as: UTF8.self)
        let body = data.count > end ? data[end...] : Data()
        return (head, Data(body), oversize)
    }

    /// Compares over the longer of the two so the loop count leaks nothing about
    /// how much of the token was right.
    static func constantTimeEqual(_ a: [UInt8], _ b: [UInt8]) -> Bool {
        var diff = UInt32(a.count ^ b.count)
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0
            let y = i < b.count ? b[i] : 0
            diff |= UInt32(x ^ y)
        }
        return diff == 0
    }

    private func respond(_ fd: Int32, _ status: Int, _ reason: String) {
        // No CORS headers of any kind, deliberately: a browser that somehow got a
        // request through still cannot read the answer.
        let text = """
        HTTP/1.1 \(status) \(reason)\r
        Content-Length: 0\r
        Cache-Control: no-store\r
        Connection: close\r
        \r

        """
        Array(text.utf8).withUnsafeBufferPointer { p in
            var sent = 0
            while sent < p.count {
                let n = send(fd, p.baseAddress! + sent, p.count - sent, 0)
                if n <= 0 { return }
                sent += n
            }
        }
    }
}

// MARK: - The module

/// Turns a stream of hook events into one mood.
final class AgentModule: NSObject, CatModule {
    /// A singleton because the menu bar is built before modules are registered
    /// and both need the same instance. Registration in `main.swift` hands this
    /// very object to the registry.
    static let shared = AgentModule()

    let id = "agent"

    private enum Phase: String { case thinking, working, notification }

    private struct Session {
        var phase: Phase
        var lastEvent: CFAbsoluteTime
    }

    /// Concurrent sessions, keyed `agentId:sessionId`. Two terminals running
    /// Claude at once are two sessions, and the cat should look busy until BOTH
    /// are done rather than until the first one finishes.
    private var sessions: [String: Session] = [:]
    private var celebratedAt: CFAbsoluteTime?
    private var erroredAt: CFAbsoluteTime?
    private let lock = NSLock()

    /// Ten minutes with no events at all and a session is assumed gone — the app
    /// was quit, the terminal was closed, the machine slept.
    private let sessionTTL: CFAbsoluteTime = 600

    /// Ninety seconds of "busy" with nothing arriving and we stop believing it.
    ///
    /// This is not belt-and-braces, it is the primary exit for a very common case:
    /// `Stop` does NOT fire when the user presses Esc. Without this the cat would
    /// sit there thinking, forever, the first time anyone interrupts Claude.
    private let idleBackstop: CFAbsoluteTime = 90

    private var endpoint: AgentEndpoint?
    private var lastRequested: String?
    private var signalSources: [DispatchSourceSignal] = []

    private weak var connectItem: NSMenuItem?
    private weak var disconnectItem: NSMenuItem?
    private weak var statusItem: NSMenuItem?

    private override init() {
        super.init()
        startEndpoint()
        NotificationCenter.default.addObserver(
            self, selector: #selector(cleanUp),
            name: NSApplication.willTerminateNotification, object: nil)
        installSignalHandlers()
    }

    /// Ctrl+C is one of the two documented ways to quit this app, and a raw
    /// signal does not go through AppKit's terminate path — `willTerminate`
    /// never fires and the handshake file gets left behind pointing at a dead
    /// port. The hook survives that fine (an instant connection refusal, still
    /// exit 0), but a stale state file is how "it worked yesterday" bugs start.
    private func installSignalHandlers() {
        for sig in [SIGINT, SIGTERM] {
            // The default action would kill the process before the source runs.
            signal(sig, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: sig, queue: .main)
            source.setEventHandler { [weak self] in
                self?.cleanUp()
                exit(0)
            }
            source.resume()
            signalSources.append(source)
        }
    }

    // MARK: endpoint lifecycle

    private func startEndpoint() {
        let ep = AgentEndpoint { [weak self] event in self?.ingest(event) }
        do {
            try ep.start()
            try writeEndpointFile(port: ep.port, token: ep.token)
            endpoint = ep
            print("""
              agent   listening on 127.0.0.1:\(ep.port), \
            handshake at ~/.loafcat/endpoint.json (0600)
            """)
            fflush(stdout)
        } catch {
            FileHandle.standardError.write(Data("loafcat agent: \(error)\n".utf8))
        }
    }

    /// The handshake file. 0600 in a 0700 directory: any process running as the
    /// user could read it anyway, but there is no reason to hand it to anything
    /// else on a shared machine.
    private func writeEndpointFile(port: UInt16, token: String) throws {
        let fm = FileManager.default
        let dir = Self.stateDirectory
        try fm.createDirectory(
            at: dir, withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: dir.path)

        let url = dir.appendingPathComponent("endpoint.json")
        let body = Data(#"{"port": \#(port), "token": "\#(token)"}"# .utf8) + Data("\n".utf8)
        try? fm.removeItem(at: url)
        fm.createFile(atPath: url.path, contents: body,
                      attributes: [.posixPermissions: 0o600])
        try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    static var stateDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".loafcat")
    }

    /// Removing the handshake file on quit is what makes the hook a no-op when the
    /// app is not running: no file, no POST, no connection attempt at all.
    @objc private func cleanUp() {
        endpoint?.stop()
        try? FileManager.default.removeItem(
            at: Self.stateDirectory.appendingPathComponent("endpoint.json"))
    }

    // MARK: event ingestion

    /// Called off the main thread, from a connection worker.
    private func ingest(_ e: AgentEndpoint.Event) {
        let resolved = e.state.isEmpty
            ? (HookInstaller.events.first { $0.event == e.event }?.state ?? "")
            : e.state
        let key = "\(e.agentId.isEmpty ? "main" : e.agentId):\(e.sessionId)"
        let now = CFAbsoluteTimeGetCurrent()

        lock.lock()
        defer { lock.unlock() }

        switch resolved {
        case "thinking":
            sessions[key] = Session(phase: .thinking, lastEvent: now)
        case "working":
            sessions[key] = Session(phase: .working, lastEvent: now)
        case "notification":
            sessions[key] = Session(phase: .notification, lastEvent: now)
        case "error":
            // A failed tool call does not end the turn; Claude keeps going. Flash
            // the reaction but leave the session busy, or the cat would drop to
            // idle in the middle of the work.
            erroredAt = now
            sessions[key] = Session(phase: .working, lastEvent: now)
        case "complete":
            // The rule that stops the cat hopping at random: a completion for a
            // session we never saw start is not ours. `Stop` fires for sessions
            // begun before the app launched, for other projects, and for agents
            // whose start we missed — every one of those would otherwise be a
            // spontaneous celebration.
            if sessions.removeValue(forKey: key) != nil { celebratedAt = now }
        case "idle":
            sessions.removeValue(forKey: key)
        default:
            break
        }
    }

    /// Drops sessions we have stopped believing in. See `idleBackstop`.
    private func expire(_ now: CFAbsoluteTime) {
        for (key, session) in sessions {
            let age = now - session.lastEvent
            if age > sessionTTL {
                sessions.removeValue(forKey: key)
            } else if session.phase != .notification && age > idleBackstop {
                sessions.removeValue(forKey: key)
            }
        }
    }

    // MARK: CatModule

    func update(_ ctx: TickContext) -> ModuleOutput {
        let now = CFAbsoluteTimeGetCurrent()
        // Durations come from the atlas, so "how long is the hop" has one answer
        // and it is in cat.json next to the keyframes that define it.
        let hop = Stage.shared.duration(of: "hop")
        let slump = Stage.shared.duration(of: "slump")

        lock.lock()
        expire(now)
        if let c = celebratedAt, now - c > hop { celebratedAt = nil }
        if let e = erroredAt, now - e > slump { erroredAt = nil }
        let celebrating = celebratedAt != nil
        let failing = erroredAt != nil
        let alerting = sessions.values.contains { $0.phase == .notification }
        let busy = sessions.values.contains { $0.phase != .notification }
        lock.unlock()

        // Highest first. A hop beats everything because it is short and it is the
        // one moment the user actually wanted to be told about.
        let want: (anim: String, overlay: String, state: CatState)?
        if celebrating {
            want = ("hop", "celebrate", .celebrating)
        } else if failing {
            want = ("slump", "error", .errored)
        } else if alerting {
            // Same slumped styling as an error — something is wrong and the cat
            // has stopped — but a red exclamation instead of a sweat drop, because
            // this one is waiting on the user rather than on itself.
            want = ("alert", "permission", .errored)
        } else if busy {
            want = ("think", "think", .thinking)
        } else {
            want = nil
        }

        // Only touch the stage while we have something to say, or on the one frame
        // where we stop having something to say.
        guard want != nil || lastRequested != nil else { return .none }
        Stage.shared.request(want?.anim, overlay: want?.overlay)
        lastRequested = want?.anim

        var out = ModuleOutput()
        out.state = want?.state
        out.overlay = want?.overlay
        if want != nil {
            let sampled = Stage.shared.sample()
            out.squash = sampled.squash
            out.offset = sampled.offset
        }
        return out
    }

    // MARK: menu

    /// The menu bar items for this feature, owned here so `main.swift` only has to
    /// place them.
    func menuItems() -> [NSMenuItem] {
        let installed = HookInstaller.isInstalled()

        let status = NSMenuItem(title: statusTitle(), action: nil, keyEquivalent: "")
        status.isEnabled = false
        statusItem = status

        let connect = NSMenuItem(
            title: "Connect to Claude Code", action: #selector(connect), keyEquivalent: "")
        connect.target = self
        connect.state = installed ? .on : .off
        connectItem = connect

        let disconnect = NSMenuItem(
            title: "Disconnect from Claude Code", action: #selector(disconnect),
            keyEquivalent: "")
        disconnect.target = self
        disconnect.isEnabled = installed
        disconnectItem = disconnect

        return [status, connect, disconnect]
    }

    private func statusTitle() -> String {
        guard let ep = endpoint else { return "Agent listener: off" }
        return "Agent listener: 127.0.0.1:\(ep.port)"
    }

    @objc private func connect() {
        do {
            let script = try deployHookScript()
            let url = HookInstaller.settingsURL
            let root = try HookInstaller.read(url)
            let merged = try HookInstaller.merged(into: root, script: script.path)
            try HookInstaller.write(merged, to: url)
            refreshMenu()
            alert(
                "Connected to Claude Code",
                """
                \(HookInstaller.events.count) hooks registered in ~/.claude/settings.json. \
                The previous file was copied to settings.json.loafcat-backup.

                Every hook is async with a short timeout and exits 0 whatever \
                happens, so it cannot slow a session down — even with loafcat quit.
                """)
        } catch {
            alert("Could not connect", "\(error)", style: .warning)
        }
    }

    @objc private func disconnect() {
        do {
            let url = HookInstaller.settingsURL
            let root = try HookInstaller.read(url)
            try HookInstaller.write(HookInstaller.stripped(from: root), to: url)
            refreshMenu()
            alert("Disconnected", "Only loafcat's hook entries were removed.")
        } catch {
            alert("Could not disconnect", "\(error)", style: .warning)
        }
    }

    private func refreshMenu() {
        let installed = HookInstaller.isInstalled()
        connectItem?.state = installed ? .on : .off
        disconnectItem?.isEnabled = installed
        statusItem?.title = statusTitle()
    }

    /// Copies the hook script into `~/.loafcat/` and returns where it landed.
    ///
    /// Registering a path inside the .app would break the moment the user moved
    /// or replaced the app, and a broken hook path is a failure the user sees as
    /// "Claude got slow" rather than "loafcat is misconfigured".
    private func deployHookScript() throws -> URL {
        let fm = FileManager.default
        let candidates = [
            Bundle.main.bundleURL
                .appendingPathComponent("Contents/Resources/hooks/loafcat-hook.sh"),
            URL(fileURLWithPath: fm.currentDirectoryPath)
                .appendingPathComponent("hooks/loafcat-hook.sh"),
        ]
        guard let source = candidates.first(where: { fm.fileExists(atPath: $0.path) })
        else { throw HookInstaller.Failure.unreadable("hooks/loafcat-hook.sh") }

        let dir = Self.stateDirectory
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        let dest = dir.appendingPathComponent("loafcat-hook.sh")
        try? fm.removeItem(at: dest)
        try fm.copyItem(at: source, to: dest)
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: dest.path)
        return dest
    }

    private func alert(_ title: String, _ body: String, style: NSAlert.Style = .informational) {
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = title
        a.informativeText = body
        a.alertStyle = style
        a.runModal()
    }
}
