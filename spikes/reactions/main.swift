import AppKit

// A harness for tuning and regression-testing the input-reaction modules.
//
// It links the REAL module sources -- TypingModule, HuntModule, PettingModule,
// ScrollModule, plus the registry and the stage -- and feeds them synthetic input
// through the same signal path main.swift builds: a 1.5s sliding keystroke window,
// the cursor converted to logical pixels relative to the cat's centre, and the same
// EMA on velocity. Nothing about the modules is stubbed.
//
// It runs in REAL TIME rather than fast-forward, because the modules time their own
// phases off CFAbsoluteTimeGetCurrent. That also means dt is genuine 120Hz jitter
// rather than a constant, which is the thing most likely to break a per-frame
// constant that was never normalised.
//
// Why this exists: the alternative for "does a wiggle hunt but a sweep not?" is a
// human waggling a mouse and reporting a feeling. This is repeatable, and the
// numbers it prints are the ones quoted in the commit message.
//
// Reproduce: ./spikes/reactions/build.sh && ./spikes/reactions/build/ReactionSpike

// --- mirrors of the three constants main.swift owns ------------------------
// Copied, not shared: main.swift holds the app's top-level code and cannot be linked
// into a second executable. If one of these changes, change both.
let keyWindow: Double = 1.5
let emaAlpha: CGFloat = 0.25
let renderScale: CGFloat = 2

let tickHz: Double = 120

struct Scenario {
    let name: String
    let note: String
    let seconds: Double
    /// Keystrokes per second the user is producing at time t.
    var keys: (Double) -> Double = { _ in 0 }
    /// Cursor in SCREEN POINTS relative to the cat's centre, y-up (as AppKit reports
    /// it). The harness converts to the logical, y-down frame modules see.
    var cursor: (Double) -> CGPoint = { _ in CGPoint(x: 600, y: 400) }
    /// Wheel events per second.
    var scroll: (Double) -> Double = { _ in 0 }
    /// States that must appear, and states that must not.
    var wants: [CatState] = []
    var forbids: [CatState] = []
}

struct Run {
    var seen: Set<String> = []
    var maxHeat: CGFloat = 0
    var maxEnergy: CGFloat = 0
    var maxKps: CGFloat = 0
    var maxSpeed: CGFloat = 0
    var firstAt: [String: Double] = [:]
    var lastAt: [String: Double] = [:]
}

func smoothstep(_ t: Double) -> Double {
    let x = min(max(t, 0), 1)
    return x * x * (3 - 2 * x)
}

// ---------------------------------------------------------------------------

func run(_ s: Scenario, atlas: Atlas) -> Run {
    // A fresh registry per scenario, so no module carries state across cases.
    let reg = ModuleRegistry()
    reg.register(TypingModule())
    reg.register(HuntModule())
    reg.register(PettingModule())
    reg.register(ScrollModule())

    var result = Run()
    var keyStamps: [Double] = []
    var keyCredit = 0.0
    var scrollCredit = 0.0
    var lastKey = -999.0
    var lastCursor: CGPoint?
    var velocity = CGPoint.zero

    let t0 = CFAbsoluteTimeGetCurrent()
    var last = t0
    var nextPrint = 0.0

    print("\n=== \(s.name) — \(s.note)")
    print("      t   state       owner     kps   heat  hunt.e  hunt.rev  spd(lpx/s)")

    while true {
        let now = CFAbsoluteTimeGetCurrent()
        let t = now - t0
        if t >= s.seconds { break }
        let dt = CGFloat(min(now - last, 0.1))
        last = now

        // --- keystrokes: discrete arrivals, then the same 1.5s sliding window
        keyCredit += s.keys(t) * Double(dt)
        while keyCredit >= 1 {
            keyCredit -= 1
            keyStamps.append(t)
            lastKey = t
        }
        keyStamps.removeAll { t - $0 > keyWindow }

        scrollCredit += s.scroll(t) * Double(dt)
        var scrollDelta: UInt32 = 0
        while scrollCredit >= 1 { scrollCredit -= 1; scrollDelta += 1 }

        // --- cursor, exactly as main.swift derives it
        let screen = s.cursor(t)
        let cursor = CGPoint(x: screen.x / renderScale, y: -screen.y / renderScale)
        if let prev = lastCursor, dt > 0 {
            let vx = (cursor.x - prev.x) / dt
            let vy = (cursor.y - prev.y) / dt
            velocity.x += (vx - velocity.x) * emaAlpha
            velocity.y += (vy - velocity.y) * emaAlpha
        }
        lastCursor = cursor

        let kps = CGFloat(Double(keyStamps.count) / keyWindow)
        let ctx = TickContext(
            dt: dt, cursor: cursor, cursorVelocity: velocity,
            cursorOnCat: false, keysPerSecond: kps, scrollDelta: scrollDelta,
            secondsSinceKey: t - lastKey,
            frame: NSRect(x: 0, y: 0, width: atlas.canvas * renderScale,
                          height: atlas.canvas * renderScale),
            scale: renderScale)

        _ = reg.update(ctx)

        let m = CatStage.shared.metrics
        let name = reg.state.rawValue
        result.seen.insert(name)
        if result.firstAt[name] == nil { result.firstAt[name] = t }
        result.lastAt[name] = t
        result.maxHeat = max(result.maxHeat, CatStage.shared.heat)
        result.maxEnergy = max(result.maxEnergy, m["hunt.e"] ?? 0)
        result.maxKps = max(result.maxKps, kps)
        result.maxSpeed = max(result.maxSpeed, m["hunt.spd"] ?? 0)

        if t >= nextPrint {
            nextPrint += 0.25
            print(String(
                format: "  %5.2f   %-11@ %-8@ %5.2f  %5.2f   %5.2f    %5.0f     %7.0f",
                t, name as NSString, reg.stateOwner as NSString,
                Double(kps), Double(CatStage.shared.heat),
                Double(m["hunt.e"] ?? 0), Double(m["hunt.rev"] ?? 0),
                Double(m["hunt.spd"] ?? 0)))
        }

        // Pace to the tick rate the app actually runs at.
        let sleep = (1.0 / tickHz) - (CFAbsoluteTimeGetCurrent() - now)
        if sleep > 0 { Thread.sleep(forTimeInterval: sleep) }
    }
    return result
}

// ---------------------------------------------------------------------------
// Scenarios. Cursor amplitudes are in screen POINTS; the modules see logical
// pixels, which at the default 2x render scale is half of these.

// The cat's head centre in screen points relative to the window centre: the atlas
// puts the head above the canvas midpoint, and screen y is up.
let headScreenY: CGFloat = 5 * renderScale

let scenarios: [Scenario] = [
    Scenario(
        name: "idle", note: "nothing happens for 3s",
        seconds: 3, forbids: [.kneading, .overheat, .hunting, .purring, .scrolling]),

    Scenario(
        name: "type-isolated", note: "one keypress every 1.2s — must never knead",
        seconds: 6,
        keys: { t in t.truncatingRemainder(dividingBy: 1.2) < 0.05 ? 20 : 0 },
        forbids: [.kneading, .overheat]),

    Scenario(
        name: "type-burst", note: "5 keys inside 1s, then silence — kneads, releases ~180ms later",
        seconds: 5,
        keys: { t in t >= 0.5 && t < 1.3 ? 6.25 : 0 },
        wants: [.kneading], forbids: [.overheat]),

    Scenario(
        name: "type-normal", note: "60wpm ≈ 5 keys/s sustained — kneads, stays cool",
        seconds: 6,
        keys: { t in t < 4.5 ? 5 : 0 },
        wants: [.kneading], forbids: [.overheat]),

    Scenario(
        name: "type-fast", note: "16 keys/s sustained — reaches overheat",
        seconds: 8,
        keys: { t in t < 6 ? 16 : 0 },
        wants: [.kneading, .overheat]),

    Scenario(
        name: "cursor-sweep", note: "1700pt sweeps across the screen — must NOT hunt",
        seconds: 8,
        cursor: { t in
            // Sweep across in 0.45s, rest, sweep back. A straight traverse of a wide
            // display, which is most of what a cursor ever does.
            let p = t.truncatingRemainder(dividingBy: 2.1)
            let x: Double
            if p < 0.45 { x = -850 + 1700 * smoothstep(p / 0.45) }
            else if p < 1.05 { x = 850 }
            else if p < 1.5 { x = 850 - 1700 * smoothstep((p - 1.05) / 0.45) }
            else { x = -850 }
            return CGPoint(x: x, y: 400)
        },
        forbids: [.hunting]),

    Scenario(
        name: "cursor-wiggle", note: "±120pt at 5Hz — the cat-toy gesture, must hunt",
        seconds: 6,
        cursor: { t in
            guard t > 0.5 else { return CGPoint(x: 0, y: 300) }
            let w = 2 * Double.pi * 5
            return CGPoint(x: 120 * sin(w * (t - 0.5)),
                           y: 300 + 40 * sin(2 * w * (t - 0.5)))
        },
        wants: [.hunting]),

    Scenario(
        name: "cursor-drift", note: "slow aimless mousing — must NOT hunt",
        seconds: 6,
        cursor: { t in
            CGPoint(x: 200 * sin(2 * Double.pi * 0.4 * t),
                    y: 300 + 150 * cos(2 * Double.pi * 0.3 * t))
        },
        forbids: [.hunting]),

    Scenario(
        name: "pet-stroke", note: "cursor stroked across the head — purrs, then stops",
        seconds: 6,
        cursor: { t in
            guard t > 0.5 && t < 3.5 else {
                // Parked on the head but motionless: inside the region, not petting.
                return CGPoint(x: 0, y: Double(headScreenY))
            }
            return CGPoint(x: 14 * sin(2 * Double.pi * 1.4 * (t - 0.5)),
                           y: Double(headScreenY) + 6 * sin(2 * Double.pi * 0.9 * (t - 0.5)))
        },
        wants: [.purring]),

    Scenario(
        name: "pet-parked", note: "cursor motionless on the head — must NOT purr",
        seconds: 4,
        cursor: { _ in CGPoint(x: 0, y: Double(headScreenY)) },
        forbids: [.purring]),

    Scenario(
        name: "scroll", note: "a burst of wheel events, then nothing",
        seconds: 4,
        scroll: { t in t < 1.5 ? 12 : 0 },
        wants: [.scrolling]),
]

// ---------------------------------------------------------------------------

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let theme = CommandLine.arguments.contains("--theme")
    ? CommandLine.arguments[CommandLine.arguments.firstIndex(of: "--theme")! + 1]
    : "mono"
let atlas = try! Atlas.load(from: root.appendingPathComponent("assets/themes/\(theme)"))
CatStage.shared.publish(atlas: atlas)

let only = CommandLine.arguments.contains("--only")
    ? CommandLine.arguments[CommandLine.arguments.firstIndex(of: "--only")! + 1]
    : nil

print("reaction spike — theme \(theme), \(Int(tickHz))Hz, render scale \(Int(renderScale))x")

var failures: [String] = []
for s in scenarios where only == nil || s.name == only! {
    let r = run(s, atlas: atlas)
    var verdict: [String] = []
    for w in s.wants where !r.seen.contains(w.rawValue) {
        verdict.append("MISSING \(w.rawValue)")
    }
    for f in s.forbids where r.seen.contains(f.rawValue) {
        verdict.append("UNWANTED \(f.rawValue)")
    }
    let ok = verdict.isEmpty
    if !ok { failures.append("\(s.name): \(verdict.joined(separator: ", "))") }
    print(String(
        format: "  -> %@  states=%@  peak kps %.1f, heat %.2f, hunt.e %.2f, speed %.0f lpx/s",
        (ok ? "PASS" : "FAIL") as NSString,
        r.seen.sorted().joined(separator: "/") as NSString,
        Double(r.maxKps), Double(r.maxHeat), Double(r.maxEnergy), Double(r.maxSpeed)))
    for (state, at) in r.firstAt.sorted(by: { $0.value < $1.value }) where state != "idle" {
        print(String(format: "       %-11@ %.2fs .. %.2fs",
                     state as NSString, at, r.lastAt[state] ?? at))
    }
}

print("")
if failures.isEmpty {
    print("all scenarios passed")
} else {
    for f in failures { print("FAILED  \(f)") }
    exit(1)
}
