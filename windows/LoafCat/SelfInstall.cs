using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LoafCat;

/// Turns a downloaded executable into an installed app.
///
/// A single-file `loafcat.exe` is a complete app in the sense that it runs, but it is
/// not one in the sense that matters to the person running it. It sits in Downloads,
/// beside everything else they have ever downloaded, absent from the Start menu, and a
/// second copy of it appears there as `loafcat (1).exe` the next time they update by
/// hand. macOS has no equivalent problem because a `.app` is a folder you drag to
/// Applications and the bundle carries its own name; Windows has no such convention for
/// one loose binary, so the app has to do it.
///
/// So on first run it copies itself to exactly where `install.ps1` would have put it and
/// leaves the same Start menu entry, then hands over to the installed copy. The two
/// download routes converge on one layout, and `install.ps1 -Uninstall` removes either.
///
/// Deliberately narrow. It only fires for a bare executable — one whose art had to be
/// unpacked from inside it, because there was none beside it. The `.zip` and a source
/// tree both fail that test, and both should: someone who unpacked the zip chose that
/// folder, possibly to edit the art in it, and lifting one file out of it would leave
/// the rest behind.
///
/// Per-user, under %LOCALAPPDATA%, and never elevated. Nothing here needs a part of the
/// machine the user does not already own.
[SupportedOSPlatform("windows")]
public static class SelfInstall
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "loafcat");

    private static string Target => Path.Combine(Root, "loafcat.exe");

    /// Passed to the copy it starts, so that copy — which owns the tray icon, and is
    /// therefore the only one able to say anything — can tell the user where it went.
    public const string JustInstalledFlag = "--installed";

    private static string Shortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "loafcat.lnk");

    /// Returns true when it installed and started the installed copy, meaning this
    /// process has nothing left to do.
    public static bool Promote(string[] args)
    {
        if (args.Contains("--portable")) return false;
        if (Environment.GetEnvironmentVariable("LOAFCAT_NO_INSTALL") is { Length: > 0 })
            return false;

        string? self = Environment.ProcessPath;
        if (self is null) return false;

        // The art was found on disk, so this is the unpacked .zip or a source build.
        if (!Assets.UsingEmbeddedPayload) return false;

        if (string.Equals(Path.GetDirectoryName(self), Root, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            Directory.CreateDirectory(Root);

            // Deleted and rewritten rather than copied over. A file created here is a
            // new file with no alternate data streams, which drops the mark of the web
            // the browser attached to the download — otherwise the installed copy would
            // raise SmartScreen again every time it started, having already been allowed
            // to run once. And the delete is the load-bearing half: Windows will not let
            // go of a running executable, so an installed copy that is already running
            // throws here and sends us down the single-instance path instead.
            if (File.Exists(Target)) File.Delete(Target);
            using (var src = File.OpenRead(self))
            using (var dst = File.Create(Target))
            {
                src.CopyTo(dst);
            }

            // The Start menu entry is what makes loafcat answer to its name in the
            // search box, so a failure here is worth a line in the log rather than a
            // silent one-less-feature.
            if (!WriteShortcut(Shortcut, Target, Root, ShortcutDescription))
                Log.Warn("installed, but without a Start menu entry");
            Log.Line($"installed to {Root}");

            Process.Start(new ProcessStartInfo(Target)
            {
                UseShellExecute = true,
                WorkingDirectory = Root,
                Arguments = JustInstalledFlag,
            });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or System.ComponentModel.Win32Exception)
        {
            // Never fatal. Running from wherever it is works perfectly well, and an app
            // that refused to start because it could not tidy itself away would be a
            // worse app than one with an untidy name.
            Log.Warn($"could not install to {Root} ({e.Message}) — running in place");
            return false;
        }
    }

    public const string ShortcutDescription =
        "A pixel cat that reacts to your cursor and your typing";

    /// Writes a Start menu entry, which is the whole of what makes loafcat findable by
    /// typing its name: Windows Search indexes `Start Menu\Programs` for the current
    /// user, so a `.lnk` there IS the app as far as the search box is concerned. There
    /// is no separate registration, and nothing else in the system needs telling.
    ///
    /// Late-bound through WScript.Shell rather than by declaring `IShellLink`: a
    /// shortcut is written once, at install time, and the COM interface declarations
    /// would be more code than the feature. `install.ps1` creates the same shortcut the
    /// same way, which is why the two install routes are interchangeable.
    ///
    /// Takes its paths as arguments so `--selftest` can write one somewhere harmless and
    /// prove the mechanism works. That is worth doing: whether late-bound COM survives
    /// into a single-file self-contained build is not something the compiler checks, and
    /// the failure mode is a silently missing Start menu entry.
    internal static bool WriteShortcut(string linkPath, string target, string workingDir,
                                       string description)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            Log.Warn("WScript.Shell is unavailable — no Start menu entry");
            return false;
        }
        object? shell = Activator.CreateInstance(shellType);
        if (shell is null) return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            object? link = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            if (link is null) return false;

            Type linkType = link.GetType();
            void Set(string property, string value) => linkType.InvokeMember(
                property, BindingFlags.SetProperty, null, link, new object[] { value });

            Set("TargetPath", target);
            Set("WorkingDirectory", workingDir);
            Set("Description", description);
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            return File.Exists(linkPath);
        }
        catch (Exception e) when (e is COMException or MissingMethodException
                                    or MissingMemberException or InvalidComObjectException
                                    or UnauthorizedAccessException or IOException)
        {
            // The app works either way; it is just harder to find.
            Log.Warn($"could not write the Start menu entry ({e.Message})");
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    /// Reads a shortcut's target back. Only `--selftest` uses this.
    internal static string? ReadShortcutTarget(string linkPath)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return null;
        object? shell = Activator.CreateInstance(shellType);
        if (shell is null) return null;
        try
        {
            object? link = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            return link?.GetType().InvokeMember("TargetPath",
                BindingFlags.GetProperty, null, link, null) as string;
        }
        catch (Exception e) when (e is COMException or MissingMemberException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }
}
