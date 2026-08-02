using System.Runtime.InteropServices;
using System.Text;

namespace LoafCat;

/// Where `print` goes on a platform that does not give a GUI app a console.
///
/// The macOS app is a plain executable inside a bundle, so `print` lands in the
/// terminal that launched it and in Console.app otherwise. A Windows `WinExe` has no
/// console at all — `Console.WriteLine` writes to a handle that is not there, and the
/// launch banner every CI smoke test greps for would simply vanish.
///
/// So: attach to the parent console when there is one (running the exe from
/// PowerShell prints normally, which is what the CI job depends on), and always
/// append to a file so a user who is not in a terminal still has something to send
/// when they report a bug.
public static class Log
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static bool _console;

    public static string FilePath { get; private set; } = "";

    /// Called once, as early as possible — before the first line anyone wants to see.
    public static void Start()
    {
        lock (Gate)
        {
            // Only succeeds when a console actually launched us. Failure is the
            // normal case (double-clicked from Explorer) and is not worth reporting.
            _console = AttachConsole(AttachParentProcess);

            try
            {
                string dir = Paths.AppData;
                Directory.CreateDirectory(dir);
                FilePath = Path.Combine(dir, "loafcat.log");

                // Truncate a log that has grown past a megabyte rather than rotating
                // it. This is a desktop pet: nobody is going to want yesterday's.
                var info = new FileInfo(FilePath);
                var mode = info.Exists && info.Length > 1024 * 1024
                    ? FileMode.Create
                    : FileMode.Append;

                _file = new StreamWriter(
                    new FileStream(FilePath, mode, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                _file.WriteLine();
                _file.WriteLine($"=== loafcat {Branding.Version} started {DateTime.Now:u} ===");
            }
            catch (Exception)
            {
                // A log we cannot open must never stop the app from running.
                _file = null;
            }
        }
    }

    public static void Line(string message)
    {
        lock (Gate)
        {
            if (_console)
            {
                try { Console.Out.WriteLine(message); Console.Out.Flush(); }
                catch (IOException) { _console = false; }
            }
            try { _file?.WriteLine(message); }
            catch (IOException) { /* disk full, or the file was deleted underneath us */ }
        }
    }

    public static void Warn(string message)
    {
        lock (Gate)
        {
            if (_console)
            {
                try { Console.Error.WriteLine(message); Console.Error.Flush(); }
                catch (IOException) { _console = false; }
            }
            try { _file?.WriteLine("WARN: " + message); }
            catch (IOException) { }
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            try { _file?.Flush(); _file?.Dispose(); } catch (IOException) { }
            _file = null;
        }
    }
}

/// Every path the app writes to, in one place.
///
/// The macOS app uses `~/.loafcat` for its agent handshake and `UserDefaults` for
/// settings. Windows has no UserDefaults, and dotfiles in the user profile are a Unix
/// convention — but `~/.claude` already lives there on both platforms, and the agent
/// handshake sits beside the hook script that reads it, so `~/.loafcat` is kept
/// verbatim. Settings go where Windows expects them instead.
public static class Paths
{
    public static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// `%LOCALAPPDATA%\loafcat` — settings and the log.
    public static string AppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "loafcat");

    /// `~/.loafcat` — the agent handshake and the deployed hook script. Deliberately
    /// the same path the macOS build uses: the hook script resolves it the same way
    /// on both, so one contract covers both platforms.
    public static string State => Path.Combine(Home, ".loafcat");

    /// `~/.claude/settings.json` — the user's, shared with every other tool.
    public static string ClaudeSettings => Path.Combine(Home, ".claude", "settings.json");
}
