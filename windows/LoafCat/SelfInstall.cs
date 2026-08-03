using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LoafCat;

/// What a downloaded executable should do about itself.
///
/// A pure decision, kept apart from the doing of it so `--selftest` can check every
/// branch on a machine with nothing installed. Getting this wrong is expensive in a way
/// that is hard to notice: every outcome except `Fresh` used to be the same silent
/// nothing.
public enum InstallPlan
{
    /// Not a bare download. The .zip, a source build, the installed copy itself, or
    /// installing was turned off.
    None,

    /// Nothing is installed yet.
    Fresh,

    /// An older copy is installed. This is the manual update route, and it is the one
    /// that has to work while the old copy is running, because it always is.
    Replace,

    /// The same version is already installed. Open it rather than reinstalling it.
    Same,

    /// A NEWER copy is installed, so this download is a downgrade. Offered, never
    /// silent — rolling back is the thing you want most on the day an update is bad.
    Older,
}

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

    internal static string Target => Path.Combine(Root, "loafcat.exe");

    /// Does the whole install with no window and opens the installed copy, for CI.
    ///
    /// The window is what a person gets and it waits for them, which a runner cannot
    /// do. This drives the same `Install` underneath, so what CI proves is the work
    /// itself: the copy, the replacement of a running copy, and the Start menu entry.
    public const string UnattendedFlag = "--install-unattended";

    private static string Shortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "loafcat.lnk");

    /// One step of the install, for the progress bar. A negative percentage means
    /// there is no way to know how long this part takes.
    internal readonly record struct Step(int Percent, string Status);

    /// Returns true when this process has nothing left to do — it either handed over to
    /// the installed copy or the user closed the installer. False means run in place.
    public static bool Promote(string[] args)
    {
        var plan = Plan(args);
        if (plan == InstallPlan.None) return false;
        return InstallWindow.Run(plan);
    }

    /// Everything that decides whether there is an install to offer, and which one.
    internal static InstallPlan Plan(string[] args)
    {
        // The test entry points all start the app for real, and none of them should
        // leave anything behind on the machine that ran them.
        if (args.Contains("--portable") || args.Contains("--demo-drag")) return InstallPlan.None;
        if (Environment.GetEnvironmentVariable("LOAFCAT_NO_INSTALL") is { Length: > 0 })
            return InstallPlan.None;

        string? self = Environment.ProcessPath;
        if (self is null) return InstallPlan.None;

        // The art was found on disk, so this is the unpacked .zip or a source build.
        if (!Assets.UsingEmbeddedPayload) return InstallPlan.None;

        // This IS the installed copy, just starting normally.
        if (string.Equals(Path.GetDirectoryName(self), Root, StringComparison.OrdinalIgnoreCase))
            return InstallPlan.None;

        return Decide(InstalledVersion(), Branding.Version);
    }

    /// Which of the four things a download can be. Split out with no I/O in it so the
    /// self-test can check every branch.
    internal static InstallPlan Decide(string? installed, string ours)
    {
        if (installed is null) return InstallPlan.Fresh;
        if (Updater.IsNewer(ours, installed)) return InstallPlan.Replace;
        if (Updater.IsNewer(installed, ours)) return InstallPlan.Older;
        return InstallPlan.Same;
    }

    /// The version of the installed copy, read off the file rather than by running it.
    internal static string? InstalledVersion()
    {
        if (!File.Exists(Target)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(Target);
            // ProductVersion is the informational version, which is the one Branding
            // reports, so the two are comparable. It carries the same "+<sha>" suffix.
            string? v = info.ProductVersion ?? info.FileVersion;
            if (string.IsNullOrWhiteSpace(v)) return null;
            int plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or FileNotFoundException)
        {
            return null;
        }
    }

    /// Copies this executable into place and leaves a Start menu entry beside it.
    /// Throws when it could not; the caller says so and runs in place instead.
    internal static void Install(IProgress<Step>? progress)
    {
        string self = Environment.ProcessPath
            ?? throw new IOException("this executable has no path on disk");

        Directory.CreateDirectory(Root);
        MakeWay(progress);

        progress?.Report(new Step(0, "Copying loafcat…"));
        // Written as a new file rather than copied over an existing one. A file created
        // here has no alternate data streams, which drops the mark of the web the
        // browser attached to the download — otherwise the installed copy would raise
        // SmartScreen again every single time it started, having already been allowed
        // to run once.
        var source = new FileInfo(self);
        long total = Math.Max(source.Length, 1);
        using (var src = source.OpenRead())
        using (var dst = File.Create(Target))
        {
            // 1MB at a time. Small enough that the bar moves on a 66MB executable,
            // large enough that reporting is not what the copy spends its time on.
            byte[] buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dst.Write(buffer, 0, read);
                done += read;
                progress?.Report(new Step((int)(done * 100 / total), "Copying loafcat…"));
            }
        }

        progress?.Report(new Step(100, "Adding it to the Start menu…"));
        // The Start menu entry is what makes loafcat answer to its name in the search
        // box, so a failure here is worth a line in the log rather than a silent
        // one-less-feature. It is not worth failing the install over.
        if (!WriteShortcut(Shortcut, Target, Root, ShortcutDescription))
            Log.Warn("installed, but without a Start menu entry");
        Log.Line($"installed to {Root}");
    }

    /// Gets the installed copy out of the way of its replacement.
    ///
    /// Windows will not let go of a running executable, so an installed copy that is
    /// running makes its own file undeletable — and this used to be where installing
    /// gave up. That was wrong in the one case that matters most: the cat is *always*
    /// running, because a desktop pet you have to close first is a desktop pet you
    /// stopped using, so hand-installing a newer build could never work. It reported
    /// nothing, installed nothing, and quietly brought the old cat to the front.
    private static void MakeWay(IProgress<Step>? progress)
    {
        if (!File.Exists(Target) || TryDelete()) return;

        progress?.Report(new Step(-1, "Closing the copy that is running…"));
        Log.Line("install  the installed copy is running — asking it to stand down");
        AskRunningCopyToQuit();
        if (WaitForDelete(40)) return;   // 4s, and a clean exit puts the tray icon away

        // It did not go: a build old enough to predate the quit channel, or one wedged.
        // Ending it is what install.ps1 has always done here, for the same reason.
        Log.Warn("install  it did not stand down — closing it");
        foreach (var p in RunningCopies())
        {
            using (p)
            {
                try { p.Kill(); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception
                                            or NotSupportedException) { }
            }
        }
        if (WaitForDelete(20)) return;   // 2s

        throw new IOException(
            "loafcat is still running and will not let go of its own file. "
            + "Quit it from the tray cat and try again.");
    }

    private static bool WaitForDelete(int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (TryDelete()) return true;
            Thread.Sleep(100);
        }
        return !File.Exists(Target);
    }

    private static bool TryDelete()
    {
        try
        {
            File.Delete(Target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// Every running process that is the installed copy, and not merely something else
    /// called loafcat.
    private static IEnumerable<Process> RunningCopies()
    {
        foreach (var p in Process.GetProcessesByName("loafcat"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception
                                        or NotSupportedException) { }

            if (path is not null && string.Equals(path, Target, StringComparison.OrdinalIgnoreCase))
                yield return p;
            else
                p.Dispose();
        }
    }

    /// True when a copy of the installed executable is running right now.
    internal static bool InstalledCopyIsRunning()
    {
        foreach (var p in RunningCopies()) { p.Dispose(); return true; }
        return false;
    }

    private static void AskRunningCopyToQuit() => Signal(Program.QuitEventName);

    /// Brings the copy that is already running to the front, for when there is nothing
    /// to install. Returns false when nothing answered.
    internal static bool WakeRunningCopy() => Signal(Program.ReopenEventName);

    private static bool Signal(string name)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            handle.Set();
            return true;
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException
                                    or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// Starts the installed copy. This process is finished once it has.
    internal static void StartInstalled()
    {
        Process.Start(new ProcessStartInfo(Target)
        {
            UseShellExecute = true,
            WorkingDirectory = Root,
        });
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
