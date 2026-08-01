// S2 — Zero-permission input telemetry spike.
//
// Question: can we drive every input reaction the cat needs — eye follow, hunt,
// petting, kneading, overheat — without ever triggering a TCC permission dialog?
//
// The claim under test: CGEventSource exposes system-wide event COUNTS and per-type
// idle time with no Accessibility and no Input Monitoring grant. Apple's own docs
// name "prompt a typist to take a break" as the intended use, which is close enough
// to a cat that kneads your keyboard.
//
// This matters more than it looks. Comnyang uses uiohook-napi, whose tap is created
// with kCGEventTapOptionDefault — an ACTIVE filter that can read and suppress every
// keystroke system-wide, and therefore demands the full "control your computer"
// prompt. Most users bounce at that dialog. If the counter path works, we ship a pet
// that asks for nothing.
//
// IMPORTANT: the result is only meaningful if Accessibility is NOT already granted
// to this binary. The spike checks and says so.

import AppKit
import ApplicationServices

// MUST come before any NSEvent / CGEventSource use. Without an initialised
// NSApplication the process has no window-server connection: NSEvent.mouseLocation
// returns a frozen value and the event-source counters never advance. This cost me
// one confusing run.
let app = NSApplication.shared
app.setActivationPolicy(.accessory)

/// Mirrors stdout to a file so the spike can be launched from Finder (via `open`),
/// which is the ONLY way to get an honest permission reading: launched from a shell,
/// the app inherits the terminal's TCC grants as its responsible process, and
/// AXIsProcessTrusted() reports the terminal's answer rather than ours.
let logURL = URL(fileURLWithPath: NSHomeDirectory())
    .appendingPathComponent("loafcat-permission-spike.log")
try? FileManager.default.removeItem(at: logURL)
FileManager.default.createFile(atPath: logURL.path, contents: nil)
let logHandle = try? FileHandle(forWritingTo: logURL)

func emit(_ line: String) {
    print(line)
    fflush(stdout)
    logHandle?.write((line + "\n").data(using: .utf8)!)
}

func now() -> String {
    let f = DateFormatter()
    f.dateFormat = "HH:mm:ss"
    return f.string(from: Date())
}

// AXIsProcessTrusted() reports the grant WITHOUT prompting.
// AXIsProcessTrustedWithOptions(kAXTrustedCheckOptionPrompt: true) would prompt —
// never call that here, it would invalidate the whole test.
let axTrusted = AXIsProcessTrusted()

// Input Monitoring, checked the same way. Preflight does not prompt.
let listenAccess = CGPreflightListenEventAccess()

emit("""

┌─ S2 zero-permission input telemetry ─────────────────────────────
│ Accessibility granted   : \(axTrusted ? "YES  ⚠️ result is CONTAMINATED" : "no  ✅")
│ Input Monitoring granted: \(listenAccess ? "YES  ⚠️ result is CONTAMINATED" : "no  ✅")
├──────────────────────────────────────────────────────────────────
│ Type anywhere (this terminal, a browser, anything) and move the
│ mouse. If the counters move with BOTH permissions denied, the
│ cat can react to you having asked for nothing.
│
│ Watch for a permission dialog. There should not be one.
└──────────────────────────────────────────────────────────────────

""")

/// System-wide count of an event type since login. Needs no permission if the
/// claim holds.
func counter(_ type: CGEventType, _ state: CGEventSourceStateID = .combinedSessionState)
    -> UInt32
{
    UInt32(CGEventSource.counterForEventType(state, eventType: type))
}

/// FINDING: only `.combinedSessionState` is safe to read.
/// Querying `.hidSystemState` or `.privateState` BLOCKS INDEFINITELY for an
/// unprivileged process — the call never returns and never prompts, so the app just
/// hangs with no diagnostic. Do not touch them.
func allStates(_ type: CGEventType) -> String {
    "combined=\(counter(type, .combinedSessionState))"
}

/// Seconds since an event of this type last occurred, system-wide.
func idle(_ type: CGEventType) -> Double {
    CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: type)
}

let baselineKeys = counter(.keyDown)
let baselineClicks = counter(.leftMouseDown)
let baselineScroll = counter(.scrollWheel)

emit("baseline keyDown   \(allStates(.keyDown))")
emit("baseline mouseDown \(allStates(.leftMouseDown))")
emit("baseline scroll    \(allStates(.scrollWheel))")
emit("")
emit("  time     keys  Δkeys   kps   idle(k)  clicks  scroll   cursor        state")
emit("  ───────────────────────────────────────────────────────────────────────────")
fflush(stdout)

// Sliding window for keys-per-second, matching the shape the cat needs:
// a continuous rate, not a binary "is typing".
var keyTimestamps: [Date] = []
var lastKeys = baselineKeys
var ticks = 0

// 1.5s window and the 4→14 kps ramp are the parameters the overheat state needs.
let keyWindow: TimeInterval = 1.5
let kpsMin = 4.0
let kpsMax = 14.0

let timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { _ in
    ticks += 1
    let keys = counter(.keyDown)
    let delta = keys &- lastKeys
    lastKeys = keys

    let stamp = Date()
    for _ in 0..<Int(delta) { keyTimestamps.append(stamp) }
    keyTimestamps.removeAll { stamp.timeIntervalSince($0) > keyWindow }
    let kps = Double(keyTimestamps.count) / keyWindow

    // The overheat ramp: 0 below 4 kps so ordinary typing never reddens the cat,
    // full heat at 14, curved so the middle feels earned.
    let raw = min(max((kps - kpsMin) / (kpsMax - kpsMin), 0), 1)
    let heat = pow(raw, 1.5)

    let mouse = NSEvent.mouseLocation
    let kIdle = idle(.keyDown)

    // The state the cat would actually be in, derived purely from these numbers.
    let state: String
    if heat > 0.6 {
        state = "OVERHEAT \(Int(heat * 100))%"
    } else if kps >= 2.5 {
        state = "kneading"
    } else if kIdle > 120 {
        state = "dozing"
    } else if kIdle > 20 {
        state = "idle"
    } else {
        state = "alert"
    }

    emit(
        String(
            format: "  %@  %6u  %5u  %5.1f  %7.1f  %6u  %6u   (%4.0f,%4.0f)   %@",
            now(), keys, delta, kps, kIdle,
            counter(.leftMouseDown) &- baselineClicks,
            counter(.scrollWheel) &- baselineScroll,
            mouse.x, mouse.y, state))

    if ticks == 4 * 25 {
        emit("(25s elapsed)")
    }
}
RunLoop.main.add(timer, forMode: .common)
app.run()
