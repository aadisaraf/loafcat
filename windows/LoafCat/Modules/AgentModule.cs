using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LoafCat.Modules;

// =============================================================================
// The cat reacts to Claude Code.
//
// Claude Code fires hooks; a tiny script POSTs the event to a loopback listener this
// module owns; the module turns a stream of events into one coherent mood and asks the
// stage for the matching reaction.
//
// Three things in here are load-bearing and easy to get wrong:
//
//  1. SECURITY. A loopback HTTP port is reachable from any web page the user has open.
//     See `AgentEndpoint.Handle` for the four gates and why each one is needed.
//     Payloads carry prompt text and shell commands, so nothing from a request body is
//     ever written to a log.
//
//  2. NEVER STALLING THE USER. Everything on the hook side is fire-and-forget with
//     sub-second timeouts and an unconditional `exit 0`. If this app is not running,
//     the hook must be a silent no-op. See hooks/loafcat-hook.ps1.
//
//  3. `Stop` DOES NOT FIRE ON ESC. A session that only ever ends via `Stop` gets stuck
//     looking busy the first time the user interrupts, which is constantly. Hence the
//     idle backstop in `Expire`.
// =============================================================================

// MARK: - Hook installation

/// Merges our hooks into `~/.claude/settings.json`, and takes them out again.
///
/// Read-modify-write only. That file is the user's, it is shared with every other tool
/// that installs hooks, and clobbering it would break their setup in a way they would
/// blame on us and never find.
[SupportedOSPlatform("windows")]
public static class HookInstaller
{
    public readonly record struct Spec(string Event, string State, int Timeout, string? Matcher);

    /// The single source of truth for what we register and what each event means.
    ///
    /// `State` is passed to the script as its second argument and travels in the
    /// payload, so the mapping is visible in the user's settings file rather than
    /// buried in a binary. `AgentModule` falls back to this same table when a payload
    /// arrives without a state, which is what keeps there from being a second copy.
    ///
    /// Every entry is async with a short explicit timeout. A sync hook BLOCKS Claude's
    /// execution and the default timeout is 600 seconds — a hook that misbehaves would
    /// stall the user's real coding session, which is the one failure mode this feature
    /// is not allowed to have.
    ///
    /// `MessageDisplay` is deliberately absent and must stay that way — it holds every
    /// streamed batch until the hook returns.
    public static readonly Spec[] Events =
    [
        new("SessionStart",       "idle",         5, null),
        new("SessionEnd",         "idle",         2, null),
        new("UserPromptSubmit",   "thinking",     5, null),
        new("PreToolUse",         "working",      5, "*"),
        new("PostToolUse",        "working",      5, "*"),
        new("PostToolUseFailure", "error",        5, "*"),
        new("Stop",               "complete",     5, null),
        new("StopFailure",        "error",        5, null),
        new("Notification",       "notification", 5, null),
        new("PermissionRequest",  "notification", 5, "*"),
        new("SubagentStart",      "working",      5, null),
        new("SubagentStop",       "working",      5, null),
    ];

    /// Every command we write contains this, and nothing else does. It is how "remove
    /// only our entries" stays precise.
    ///
    /// Deliberately the stem rather than the full filename: the macOS build writes
    /// `loafcat-hook.sh` and this one writes `loafcat-hook.ps1`, and somebody who syncs
    /// a settings.json between two machines should be able to disconnect from either.
    public const string Marker = "loafcat-hook";

    public static string SettingsPath => Paths.ClaudeSettings;

    public sealed class Failure(string message) : Exception(message);

    // --- the pure part, so the merge can be exercised without a real settings file

    /// Our entries, merged in additively. Idempotent: it strips ours first, so
    /// installing twice leaves exactly one copy rather than stacking duplicates.
    ///
    /// Hook entries merge additively across settings levels and identical handlers are
    /// deduplicated, so appending is safe — but every existing entry has to survive,
    /// which is why this reaches in and rebuilds only the arrays it touches instead of
    /// assembling a fresh `hooks` object.
    public static JsonObject Merged(JsonObject root, string command)
    {
        var outv = Stripped(root);
        var hooks = outv["hooks"] as JsonObject ?? [];

        foreach (var spec in Events)
        {
            var groups = hooks[spec.Event] as JsonArray ?? [];
            var entry = new JsonObject
            {
                ["type"] = "command",
                ["command"] = command + $" {spec.Event} {spec.State}",
                ["timeout"] = spec.Timeout,
                ["async"] = true,
            };
            var group = new JsonObject { ["hooks"] = new JsonArray(entry) };
            if (spec.Matcher is { } m) group["matcher"] = m;
            groups.Add(group);
            hooks[spec.Event] = groups;
        }

        outv["hooks"] = hooks;
        return outv;
    }

    /// Removes only our entries, leaving every other hook — and every group that merely
    /// sits next to one of ours — exactly as it was.
    public static JsonObject Stripped(JsonObject root)
    {
        var outv = root.DeepClone().AsObject();
        if (outv["hooks"] is not JsonObject hooks) return outv;

        foreach (string eventName in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray groups) continue;

            var kept = new JsonArray();
            foreach (var groupNode in groups.ToList())
            {
                if (groupNode is not JsonObject group || group["hooks"] is not JsonArray inner)
                {
                    // A shape we do not recognise: hands off.
                    kept.Add(groupNode?.DeepClone());
                    continue;
                }

                var survivors = new JsonArray();
                int total = 0;
                foreach (var hookNode in inner)
                {
                    total++;
                    if (hookNode is JsonObject hook && IsOurs(hook)) continue;
                    survivors.Add(hookNode?.DeepClone());
                }

                if (survivors.Count == total)
                {
                    kept.Add(group.DeepClone());      // nothing of ours in here
                }
                else if (survivors.Count > 0)
                {
                    var g = group.DeepClone().AsObject();
                    g["hooks"] = survivors;           // ours removed, someone else's kept
                    kept.Add(g);
                }
                // else: the group held only our entry, so the group goes too
            }

            if (kept.Count == 0) hooks.Remove(eventName);
            else hooks[eventName] = kept;
        }

        if (hooks.Count == 0) outv.Remove("hooks");
        return outv;
    }

    public static bool IsOurs(JsonObject hook) =>
        hook["command"]?.GetValue<string>()?.Contains(Marker, StringComparison.Ordinal) ?? false;

    public static bool IsInstalled(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        foreach (var (_, value) in hooks)
        {
            if (value is not JsonArray groups) continue;
            foreach (var groupNode in groups)
            {
                if (groupNode is not JsonObject group || group["hooks"] is not JsonArray inner)
                    continue;
                foreach (var hookNode in inner)
                {
                    if (hookNode is JsonObject hook && IsOurs(hook)) return true;
                }
            }
        }
        return false;
    }

    // --- the file part

    public static JsonObject Read(string path)
    {
        if (!File.Exists(path)) return [];
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new Failure($"could not read {path}");
        }
        if (text.Trim().Length == 0) return [];
        try
        {
            return JsonNode.Parse(text) as JsonObject
                ?? throw new Failure($"{path} is not a JSON object");
        }
        catch (JsonException)
        {
            throw new Failure(
                $"{path} is not valid JSON — refusing to touch it, fix it by hand first");
        }
    }

    /// Writes the settings back, after copying the original aside.
    ///
    /// Two deliberate differences from the macOS build, both improvements:
    ///
    ///   * Key order is PRESERVED rather than sorted. The macOS build sorts, which
    ///     silently rearranges a file the user reads and edits by hand.
    ///   * `UnsafeRelaxedJsonEscaping` keeps `/` and `\` in paths readable. The macOS
    ///     build has to post-process the output to undo `\/`; choosing the right
    ///     encoder means there is nothing to undo.
    public static void Write(JsonObject root, string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            string backup = path + ".loafcat-backup";
            try { File.Copy(path, backup, overwrite: true); }
            catch (IOException e) { throw new Failure($"could not back up {path}: {e.Message}"); }
        }

        string text = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        // Write-then-move, so an interrupted write cannot leave the user with a
        // truncated settings.json and a Claude Code that refuses to start.
        string tmp = path + ".loafcat-tmp";
        File.WriteAllText(tmp, text + "\n", new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static bool IsInstalled()
    {
        try { return IsInstalled(Read(SettingsPath)); }
        catch (Failure) { return false; }
    }
}

// MARK: - Loopback endpoint

/// A single-purpose HTTP listener on 127.0.0.1, on a port the kernel picks.
///
/// A raw `TcpListener` rather than `HttpListener` because the security rules here are
/// all about exactly which requests are refused, and that is easiest to read — and to
/// audit — when the request is parsed in one place with no server framework in between.
/// `HttpListener` would also drag in http.sys, whose own header handling would have to
/// be reasoned about before any of the gates below could be trusted.
[SupportedOSPlatform("windows")]
public sealed class AgentEndpoint
{
    /// Only the five fields we actually use. Hook payloads also carry the user's prompt
    /// text and the full shell command of every tool call; those are never read out of
    /// the body, never stored and never logged.
    public readonly record struct Event(
        string AgentId, string EventName, string State, string SessionId, string Cwd);

    /// 8KB. A legitimate payload from the hook script is a few hundred bytes.
    private const int MaxBody = 8 * 1024;
    private const int MaxRequest = 16 * 1024;

    public int Port { get; private set; }
    public string Token { get; }
    private readonly byte[] _tokenBytes;

    private TcpListener? _listener;
    private readonly Action<Event> _onEvent;
    private volatile bool _stopping;

    public AgentEndpoint(Action<Event> onEvent)
    {
        _onEvent = onEvent;
        // 32 bytes from the system CSPRNG. Long enough that guessing it is not a
        // strategy, so the other three gates only have to defend against a page that
        // never learns it in the first place.
        byte[] raw = RandomNumberGenerator.GetBytes(32);
        Token = Convert.ToBase64String(raw);
        _tokenBytes = Encoding.UTF8.GetBytes(Token);
    }

    public void Start()
    {
        // Loopback ONLY. Binding IPAddress.Any would put this on every interface the
        // machine has, including whatever coffee-shop wifi it is on.
        //
        // Port 0: the kernel assigns a free one, read back below. A fixed port would
        // collide with whatever else the user runs and would make the endpoint findable
        // without reading the token file.
        _listener = new TcpListener(IPAddress.Loopback, 0);
        // Deliberately NOT SO_REUSEADDR: on Windows that flag lets an unrelated process
        // bind the same port and steal our connections, which is the opposite of what
        // it means on BSD sockets. The kernel picks a free port for us anyway.
        _listener.ExclusiveAddressUse = true;
        _listener.Start(16);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var t = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "loafcat.agent.accept",
        };
        t.Start();
    }

    public void Stop()
    {
        _stopping = true;
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
    }

    private void AcceptLoop()
    {
        while (!_stopping)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch (Exception) when (_stopping)
            {
                return;                     // listener closed: we are shutting down
            }
            catch (SocketException)
            {
                continue;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            // A client that connects and then says nothing must not hold a worker open
            // indefinitely.
            client.ReceiveTimeout = 2000;
            client.SendTimeout = 2000;
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    // MARK: request handling

    /// The four gates.
    ///
    /// The threat is not a program on the machine — anything running as the user can
    /// read the token file anyway. It is a **web page**: any site the user has open can
    /// POST to any localhost port, no permission needed, and the browser will happily
    /// send it.
    ///
    ///  1. Bearer token, compared in constant time. A page cannot read
    ///     `~/.loafcat/endpoint.json`, so it cannot produce one.
    ///  2. `Content-Type: application/json`. This is the important one. A form POST or
    ///     a `sendBeacon` is a *simple request* and needs no preflight, so a page can
    ///     fire one blind. `application/json` is not on the simple list, which forces an
    ///     `OPTIONS` preflight — and we answer no preflight, so the browser refuses to
    ///     send the real request at all.
    ///  3. Any `Origin` header at all is a refusal. A browser attaches one to every
    ///     cross-origin request; our hook script never sends one.
    ///  4. Method and path pinned, body capped at 8KB.
    private void Handle(TcpClient client)
    {
        using (client)
        {
            NetworkStream stream;
            try { stream = client.GetStream(); }
            catch (InvalidOperationException) { return; }

            var request = ReadRequest(stream);
            if (request is not { } req) { Respond(stream, 400, "Bad Request"); return; }
            if (req.Oversize) { Reject(stream, 413, "Payload Too Large"); return; }

            string[] lines = req.Head.Split("\r\n");
            if (lines.Length == 0) { Respond(stream, 400, "Bad Request"); return; }
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) { Respond(stream, 400, "Bad Request"); return; }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[i][..colon].ToLowerInvariant()] = lines[i][(colon + 1)..].Trim();
            }

            if (requestLine[0] != "POST") { Reject(stream, 405, "Method Not Allowed"); return; }
            if (requestLine[1] != "/agent-state") { Reject(stream, 404, "Not Found"); return; }
            if (headers.ContainsKey("origin")) { Reject(stream, 403, "Forbidden"); return; }

            string mediaType = (headers.GetValueOrDefault("content-type") ?? "")
                .Split(';')[0].Trim().ToLowerInvariant();
            if (mediaType != "application/json")
            {
                Reject(stream, 415, "Unsupported Media Type");
                return;
            }
            if (req.Body.Length > MaxBody) { Reject(stream, 413, "Payload Too Large"); return; }

            string presented = headers.GetValueOrDefault("authorization") ?? "";
            if (!presented.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !ConstantTimeEqual(Encoding.UTF8.GetBytes(presented[7..]), _tokenBytes))
            {
                Reject(stream, 401, "Unauthorized");
                return;
            }

            JsonObject? obj;
            try { obj = JsonNode.Parse(req.Body) as JsonObject; }
            catch (JsonException) { Respond(stream, 400, "Bad Request"); return; }
            if (obj is null) { Respond(stream, 400, "Bad Request"); return; }

            // Only these five keys are ever read. Truncated because they end up as
            // dictionary keys and there is no reason for any of them to be long.
            string Field(string key)
            {
                string v = obj[key]?.GetValue<string>() ?? "";
                return v.Length > 200 ? v[..200] : v;
            }

            try
            {
                _onEvent(new Event(
                    Field("agentId"), Field("event"), Field("state"),
                    Field("sessionId"), Field("cwd")));
            }
            catch (InvalidOperationException)
            {
                // A non-string value where a string was expected. Malformed, not fatal.
                Respond(stream, 400, "Bad Request");
                return;
            }

            Respond(stream, 204, "No Content");
        }
    }

    /// Refusals are logged by reason only — never the request, never a header value, and
    /// never one byte of the body.
    private static void Reject(Stream stream, int status, string reason)
    {
        Log.Warn($"loafcat agent: refused a request ({status} {reason})");
        Respond(stream, status, reason);
    }

    private readonly record struct Request(string Head, byte[] Body, bool Oversize);

    private static Request? ReadRequest(Stream stream)
    {
        var data = new MemoryStream();
        var buf = new byte[2048];
        int headEnd = -1;
        int contentLength = 0;
        bool oversize = false;

        while (data.Length < MaxRequest)
        {
            if (headEnd >= 0 && data.Length >= headEnd + contentLength) break;
            int n;
            try { n = stream.Read(buf, 0, buf.Length); }
            catch (IOException) { break; }
            if (n <= 0) break;
            data.Write(buf, 0, n);

            if (headEnd < 0)
            {
                byte[] all = data.GetBuffer();
                int sep = IndexOfCrlfCrlf(all, (int)data.Length);
                if (sep < 0) continue;
                headEnd = sep + 4;
                string head = Encoding.UTF8.GetString(all, 0, sep);
                foreach (string line in head.Split("\r\n"))
                {
                    if (!line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase))
                        continue;
                    int.TryParse(line[15..].Trim(), out contentLength);
                }
                // Refuse an oversized body from its declared length, before reading it —
                // the point of a cap is not to buffer the thing first and complain
                // afterwards.
                if (contentLength > MaxBody) { oversize = true; break; }
            }
        }

        byte[] final = data.ToArray();
        int separator = IndexOfCrlfCrlf(final, final.Length);
        if (separator < 0) return null;
        int bodyStart = separator + 4;

        // An undeclared body that ran past the cap counts too.
        if (final.Length - bodyStart > MaxBody) oversize = true;

        string headText = Encoding.UTF8.GetString(final, 0, separator);
        byte[] body = final.Length > bodyStart ? final[bodyStart..] : [];
        return new Request(headText, body, oversize);
    }

    private static int IndexOfCrlfCrlf(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' &&
                data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    /// Compares over the longer of the two so the loop count leaks nothing about how
    /// much of the token was right.
    public static bool ConstantTimeEqual(byte[] a, byte[] b)
    {
        uint diff = (uint)(a.Length ^ b.Length);
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            byte x = i < a.Length ? a[i] : (byte)0;
            byte y = i < b.Length ? b[i] : (byte)0;
            diff |= (uint)(x ^ y);
        }
        return diff == 0;
    }

    private static void Respond(Stream stream, int status, string reason)
    {
        // No CORS headers of any kind, deliberately: a browser that somehow got a
        // request through still cannot read the answer.
        string text = $"HTTP/1.1 {status} {reason}\r\n"
            + "Content-Length: 0\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n"
            + "\r\n";
        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch (IOException)
        {
            // The hook uses --max-time 0.5 and hangs up the moment it expires, so
            // writing to a closed socket is the expected case, not an error.
        }
        catch (ObjectDisposedException) { }
    }
}

// MARK: - The module

/// Turns a stream of hook events into one mood.
[SupportedOSPlatform("windows")]
public sealed class AgentModule : ICatModule
{
    /// A singleton because the tray menu is built before modules are registered and both
    /// need the same instance. Registration in `Program.cs` hands this very object to
    /// the registry.
    public static readonly AgentModule Shared = new();

    public string Id => "agent";

    private enum Phase { Thinking, Working, Notification }

    private struct Session(Phase phase, double lastEvent)
    {
        public Phase Phase = phase;
        public double LastEvent = lastEvent;
    }

    /// Concurrent sessions, keyed `agentId:sessionId`. Two terminals running Claude at
    /// once are two sessions, and the cat should look busy until BOTH are done rather
    /// than until the first one finishes.
    private readonly Dictionary<string, Session> _sessions = [];
    private double? _celebratedAt;

    /// How long the cursor has rested on the cat while it is alerting. Reaching
    /// `AcknowledgeAfter` dismisses the alert.
    private double _dwellOnCat;
    private double? _erroredAt;
    /// Guards `_sessions`, `_celebratedAt` and `_erroredAt`, which the accept loop's
    /// worker threads write and the 120Hz tick reads.
    private readonly object _lock = new();

    /// Ten minutes with no events at all and a session is assumed gone — the app was
    /// quit, the terminal was closed, the machine slept.
    private const double SessionTtl = 600;

    /// Ninety seconds of "busy" with nothing arriving and we stop believing it.
    ///
    /// This is not belt-and-braces, it is the primary exit for a very common case:
    /// `Stop` does NOT fire when the user presses Esc. Without this the cat would sit
    /// there thinking, forever, the first time anyone interrupts Claude.
    private const double IdleBackstop = 90;

    /// How long an unattended alert stays up.
    private const double AlertTimeout = 45;

    /// Cursor dwell on the cat that counts as having read an alert.
    private const double AcknowledgeAfter = 0.5;

    private AgentEndpoint? _endpoint;
    private string? _lastRequested;

    /// Raised whenever connecting or disconnecting changes the hook registration, so
    /// anything showing that state can refresh without polling for it.
    public event Action? ConnectionChanged;

    private AgentModule()
    {
        StartEndpoint();
    }

    // MARK: endpoint lifecycle

    private void StartEndpoint()
    {
        var ep = new AgentEndpoint(Ingest);
        try
        {
            ep.Start();
            WriteEndpointFile(ep.Port, ep.Token);
            _endpoint = ep;
            Log.Line($"  agent   listening on 127.0.0.1:{ep.Port}, " +
                     "handshake at ~/.loafcat/endpoint.json (owner-only)");
        }
        catch (Exception e) when (e is SocketException or IOException
                                      or UnauthorizedAccessException)
        {
            Log.Warn($"loafcat agent: {e.Message}");
        }
    }

    /// The handshake file. Readable only by the current user, in a directory the same.
    ///
    /// Any process running as the user could read it anyway, but there is no reason to
    /// hand it to anything else on a shared machine. The macOS build says this with
    /// `chmod 0600`; Windows has no mode bits, so the equivalent is an explicit DACL
    /// with inheritance switched off — without `SetAccessRuleProtection` the entry we
    /// add would sit alongside whatever the parent directory already grants.
    private static void WriteEndpointFile(int port, string token)
    {
        string dir = Paths.State;
        Directory.CreateDirectory(dir);
        RestrictToCurrentUser(dir, isDirectory: true);

        string path = Path.Combine(dir, "endpoint.json");
        File.WriteAllText(path, $"{{\"port\": {port}, \"token\": \"{token}\"}}\n",
            new UTF8Encoding(false));
        RestrictToCurrentUser(path, isDirectory: false);
    }

    private static void RestrictToCurrentUser(string path, bool isDirectory)
    {
        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null) return;

            if (isDirectory)
            {
                var info = new DirectoryInfo(path);
                var acl = info.GetAccessControl();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                acl.SetOwner(user);
                acl.AddAccessRule(new FileSystemAccessRule(
                    user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(acl);
            }
            else
            {
                var info = new FileInfo(path);
                var acl = info.GetAccessControl();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                acl.SetOwner(user);
                acl.AddAccessRule(new FileSystemAccessRule(
                    user, FileSystemRights.FullControl,
                    AccessControlType.Allow));
                info.SetAccessControl(acl);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
                                      or PlatformNotSupportedException
                                      or InvalidOperationException)
        {
            // A filesystem that cannot express this (a network share, some FAT volumes)
            // is not a reason to refuse to run. Say so once and carry on.
            Log.Warn($"agent: could not restrict permissions on {path} ({e.Message})");
        }
    }

    /// Removing the handshake file on quit is what makes the hook a no-op when the app
    /// is not running: no file, no POST, no connection attempt at all.
    public void CleanUp()
    {
        _endpoint?.Stop();
        try { File.Delete(Path.Combine(Paths.State, "endpoint.json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // MARK: event ingestion

    /// Called off the UI thread, from a connection worker.
    private void Ingest(AgentEndpoint.Event e)
    {
        string resolved = e.State;
        if (resolved.Length == 0)
        {
            foreach (var spec in HookInstaller.Events)
            {
                if (spec.Event == e.EventName) { resolved = spec.State; break; }
            }
        }
        string key = $"{(e.AgentId.Length == 0 ? "main" : e.AgentId)}:{e.SessionId}";
        double now = Clock.Now;

        lock (_lock)
        {
            switch (resolved)
            {
                case "thinking":
                    _sessions[key] = new Session(Phase.Thinking, now);
                    break;
                case "working":
                    _sessions[key] = new Session(Phase.Working, now);
                    break;
                case "notification":
                    _sessions[key] = new Session(Phase.Notification, now);
                    break;
                case "error":
                    // A failed tool call does not end the turn; Claude keeps going.
                    // Flash the reaction but leave the session busy, or the cat would
                    // drop to idle in the middle of the work.
                    _erroredAt = now;
                    _sessions[key] = new Session(Phase.Working, now);
                    break;
                case "complete":
                    // The rule that stops the cat hopping at random: a completion for a
                    // session we never saw start is not ours. `Stop` fires for sessions
                    // begun before the app launched, for other projects, and for agents
                    // whose start we missed — every one of those would otherwise be a
                    // spontaneous celebration.
                    if (_sessions.Remove(key)) _celebratedAt = now;
                    break;
                case "idle":
                    _sessions.Remove(key);
                    break;
            }
        }
    }

    /// Clears anything that is only waiting to be noticed.
    ///
    /// Deliberately does NOT touch thinking or working sessions: those describe what the
    /// agent is doing, and dismissing them would just be lying about it.
    private void Acknowledge()
    {
        lock (_lock)
        {
            foreach (string key in _sessions
                         .Where(kv => kv.Value.Phase == Phase.Notification)
                         .Select(kv => kv.Key).ToList())
            {
                _sessions.Remove(key);
            }
            _celebratedAt = null;
        }
    }

    /// Drops sessions we have stopped believing in. See `IdleBackstop`.
    private void Expire(double now)
    {
        foreach (var (key, session) in _sessions.ToList())
        {
            double age = now - session.LastEvent;
            if (age > SessionTtl)
            {
                _sessions.Remove(key);
            }
            else if (session.Phase == Phase.Notification)
            {
                if (age > AlertTimeout) _sessions.Remove(key);
            }
            else if (age > IdleBackstop)
            {
                _sessions.Remove(key);
            }
        }
    }

    // MARK: ICatModule

    public ModuleOutput Update(in TickContext ctx)
    {
        double now = Clock.Now;
        var stage = CatStage.Shared;
        // Durations come from the atlas, so "how long is the hop" has one answer and it
        // is in cat.json next to the keyframes that define it.
        double hop = stage.Duration("hop");
        double slump = stage.Duration("slump");

        // Hovering the cat while it is alerting counts as reading the alert.
        if (ctx.CursorOnCat) _dwellOnCat += ctx.Dt; else _dwellOnCat = 0;
        if (_dwellOnCat >= AcknowledgeAfter)
        {
            _dwellOnCat = 0;
            Acknowledge();
        }

        bool celebrating, failing, alerting, busy;
        lock (_lock)
        {
            Expire(now);
            if (_celebratedAt is { } c && now - c > hop) _celebratedAt = null;
            if (_erroredAt is { } er && now - er > slump) _erroredAt = null;
            celebrating = _celebratedAt is not null;
            failing = _erroredAt is not null;
            alerting = _sessions.Values.Any(s => s.Phase == Phase.Notification);
            busy = _sessions.Values.Any(s => s.Phase != Phase.Notification);
        }

        // Highest first. A hop beats everything because it is short and it is the one
        // moment the user actually wanted to be told about.
        (string Anim, string Overlay, CatState State)? want;
        if (celebrating) want = ("hop", "celebrate", CatState.Celebrating);
        else if (failing) want = ("slump", "error", CatState.Errored);
        // Same slumped styling as an error — something is wrong and the cat has stopped
        // — but a red exclamation instead of a sweat drop, because this one is waiting
        // on the user rather than on itself.
        else if (alerting) want = ("alert", "permission", CatState.Errored);
        else if (busy) want = ("think", "think", CatState.Thinking);
        else want = null;

        // Only touch the stage while we have something to say, or on the one frame where
        // we stop having something to say.
        if (want is null && _lastRequested is null) return ModuleOutput.None;
        stage.Request(want?.Anim, want?.Overlay);
        _lastRequested = want?.Anim;

        var outv = new ModuleOutput();
        if (want is { } w)
        {
            outv.State = w.State;
            outv.Overlay = w.Overlay;

            var sampled = stage.Sample();
            outv.Squash = sampled.Squash;
            // The whole-body offset, including the -26px apex of the hop. It goes
            // through `ModuleOutput` like any other module's motion — the window carries
            // a transparent margin far taller than the leap, so nothing is clipped and
            // the window never has to move. The contact shadow stays on the floor,
            // because the rig withholds the vertical component from it.
            outv.Offset = sampled.Offset;

            // The status glyph, resolved through the flipbook and posted like any other
            // overlay so the view has exactly one overlay path. Its drift with the head
            // turn comes from `follow` in the atlas, not from here.
            if (stage.OverlayFrame() is { } frame)
            {
                stage.Overlays.Add(new OverlayInstance(frame, Pt.Zero, 1));
            }
        }
        return outv;
    }

    // MARK: connection

    /// Whether loafcat's hooks are currently registered in `~/.claude/settings.json`.
    /// Read from the file every time rather than cached — the user can edit that file by
    /// hand, and a stale checkbox is worse than a slightly slow one.
    public bool IsConnected => HookInstaller.IsInstalled();

    public string ListenerStatus =>
        _endpoint is { } ep ? $"Agent listener: 127.0.0.1:{ep.Port}" : "Agent listener: off";

    /// How many hooks a connection registers, for the settings copy.
    public int HookCount => HookInstaller.Events.Length;

    /// Returns null on success, or a message explaining what went wrong.
    public string? Connect()
    {
        try
        {
            string script = DeployHookScript();
            var root = HookInstaller.Read(HookInstaller.SettingsPath);
            var merged = HookInstaller.Merged(root, HookCommand(script));
            HookInstaller.Write(merged, HookInstaller.SettingsPath);
            ConnectionChanged?.Invoke();
            return null;
        }
        catch (Exception e) when (e is HookInstaller.Failure or IOException
                                      or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    public string? Disconnect()
    {
        try
        {
            var root = HookInstaller.Read(HookInstaller.SettingsPath);
            HookInstaller.Write(HookInstaller.Stripped(root), HookInstaller.SettingsPath);
            ConnectionChanged?.Invoke();
            return null;
        }
        catch (Exception e) when (e is HookInstaller.Failure or IOException
                                      or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// The command written into settings.json.
    ///
    /// PowerShell rather than a .cmd wrapper because the hook has to read JSON from
    /// stdin, which batch cannot do without a temporary file. `-NoProfile` matters more
    /// than it looks: a user with a slow profile would otherwise pay for it on every
    /// tool call, and "Claude got slow" is exactly the failure this feature may not have.
    ///
    /// `-ExecutionPolicy Bypass` applies to this invocation only and changes nothing
    /// system-wide. Without it the default RemoteSigned policy refuses to run an
    /// unsigned script and every hook would fail silently.
    private static string HookCommand(string scriptPath) =>
        $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"";

    /// Copies the hook script into `~/.loafcat/` and returns where it landed.
    ///
    /// Registering a path inside the install directory would break the moment the user
    /// moved or reinstalled the app, and a broken hook path is a failure the user sees
    /// as "Claude got slow" rather than "loafcat is misconfigured".
    private static string DeployHookScript()
    {
        string source = Assets.HookScript()
            ?? throw new HookInstaller.Failure("could not find hooks/loafcat-hook.ps1");

        string dir = Paths.State;
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, "loafcat-hook.ps1");
        File.Copy(source, dest, overwrite: true);
        return dest;
    }
}
