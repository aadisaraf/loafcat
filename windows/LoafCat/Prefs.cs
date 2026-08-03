using System.Text.Json;
using System.Text.Json.Nodes;

namespace LoafCat;

/// The counterpart to `UserDefaults`, which Windows has no equivalent of.
///
/// A JSON file rather than the registry, for the same reason the rest of this project
/// generates its art from a script: someone should be able to look at what the app
/// stores about them without a tool. `%LOCALAPPDATA%\loafcat\settings.json` is
/// readable, diffable, and deleting it is an obvious reset.
///
/// Keys are kept IDENTICAL to the macOS build's UserDefaults keys ("theme", "scale",
/// "wellness.stretchMinutes", …). Nothing reads across platforms today, but a
/// divergence in key names is the kind of thing that quietly makes a future settings
/// sync impossible, and it costs nothing to avoid now.
///
/// Writes are debounced: a settings pane can write on every keystroke of a text
/// field, and this is a file.
public static class Prefs
{
    private static readonly object Gate = new();
    private static JsonObject _root = new();
    private static string _path = "";
    private static bool _dirty;
    private static System.Threading.Timer? _flushTimer;

    public static void Load()
    {
        lock (Gate)
        {
            _path = Path.Combine(Paths.AppData, "settings.json");
            try
            {
                if (File.Exists(_path))
                {
                    var parsed = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
                    if (parsed is not null) _root = parsed;
                }
            }
            catch (Exception e) when (e is IOException or JsonException or
                                          UnauthorizedAccessException)
            {
                // A corrupt settings file must not stop the cat from appearing.
                // Defaults are all sensible, and the file is rewritten on first change.
                Log.Warn($"settings: could not read {_path} ({e.Message}) — using defaults");
                _root = new JsonObject();
            }
        }
    }

    public static bool Has(string key)
    {
        lock (Gate) return _root.ContainsKey(key);
    }

    public static string GetString(string key, string fallback = "")
    {
        lock (Gate)
        {
            return _root.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
                   jv.TryGetValue(out string? s)
                ? s
                : fallback;
        }
    }

    public static int GetInt(string key, int fallback = 0)
    {
        lock (Gate)
        {
            return _root.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
                   jv.TryGetValue(out double d)
                ? (int)Math.Round(d)
                : fallback;
        }
    }

    public static double GetDouble(string key, double fallback = 0)
    {
        lock (Gate)
        {
            return _root.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
                   jv.TryGetValue(out double d)
                ? d
                : fallback;
        }
    }

    public static bool GetBool(string key, bool fallback = false)
    {
        lock (Gate)
        {
            if (!_root.TryGetPropertyValue(key, out var v) || v is not JsonValue jv)
                return fallback;
            if (jv.TryGetValue(out bool b)) return b;
            if (jv.TryGetValue(out double d)) return d != 0;
            return fallback;
        }
    }

    public static void Set(string key, string value) => Store(key, JsonValue.Create(value));
    public static void Set(string key, int value) => Store(key, JsonValue.Create(value));
    public static void Set(string key, double value) => Store(key, JsonValue.Create(value));
    public static void Set(string key, bool value) => Store(key, JsonValue.Create(value));

    private static void Store(string key, JsonNode? value)
    {
        lock (Gate)
        {
            _root[key] = value;
            _dirty = true;
            // 400ms after the last change. Typing into the pinned-note field would
            // otherwise write the file once per keystroke.
            _flushTimer ??= new System.Threading.Timer(_ => Flush(), null,
                Timeout.Infinite, Timeout.Infinite);
            _flushTimer.Change(400, Timeout.Infinite);
        }
    }

    /// Writes now. Called on quit, and by the debounce timer.
    public static void Flush()
    {
        string path;
        string text;
        lock (Gate)
        {
            if (!_dirty || _path.Length == 0) return;
            _dirty = false;
            path = _path;
            text = _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write-then-move, so a crash mid-write cannot leave a truncated settings
            // file that reads as "every preference reset".
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text + Environment.NewLine);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"settings: could not write {path} ({e.Message})");
        }
    }
}
