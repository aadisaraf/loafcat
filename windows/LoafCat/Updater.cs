using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace LoafCat;

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
/// filesystem. `UpdateKey` below is the public half. A release that is unsigned, or
/// signed by anything else, is **never installed** — the app says a new version exists
/// and offers to open the release page, and the human decides. That degradation is the
/// point: it is what makes it safe to ship this before any key exists.
///
/// What is still trusted, and cannot be removed from here: TLS to api.github.com, and
/// the integrity of the machine holding the private key.
///
/// =============================================================================
/// The apply dance, and why the update lands on the NEXT launch
/// =============================================================================
/// Windows will not delete a running executable, but it will rename one. So a verified
/// download is staged beside the installed app as `LoafCat.exe.new` and nothing else
/// happens until the next start, where the running executable renames itself out of the
/// way, the new one moves into place, and the process relaunches into it. That happens
/// before any window exists, so it is invisible.
///
/// Nothing is ever swapped underneath a running cat, no download can interrupt anyone,
/// and a half-finished download is a file with the wrong hash that gets deleted rather
/// than an app that will not start.
[SupportedOSPlatform("windows")]
public sealed class Updater : IDisposable
{
    private const string Repo = "aadisaraf/loafcat";

    /// The public half of the update signing key, SPKI DER, base64.
    ///
    /// Compiled in rather than read from `assets\`, deliberately: everything in assets
    /// is meant to be replaceable by whoever owns the machine, and a trust anchor that
    /// can be swapped out by editing a file next to the executable is not one. The
    /// macOS build carries the same value in Updater.swift; they must agree.
    ///
    /// Empty disables automatic installation entirely and leaves only the notification.
    public const string UpdateKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJrdvgJC0o3XeGwh1NW4rHnMkr2wBLk6rsmIyRVO4" +
        "lROV7sMwePs1fIvYWyIjb4kcUIRhc8247fv7CWgu+HyzyQ==";

    /// First check well after launch, then a few times a day. Slow on purpose: this is
    /// a desktop pet, not a browser, and an update that arrives six hours late has cost
    /// nobody anything.
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private System.Threading.Timer? _timer;
    private readonly Action<string> _announce;
    private int _busy;

    /// Set once a newer version has been seen. Read by the tray menu.
    public string? AvailableVersion { get; private set; }
    public bool StagedAndReady { get; private set; }

    public static bool Enabled
    {
        get => !Prefs.Has("updateAutomatically") || Prefs.GetBool("updateAutomatically");
        set => Prefs.Set("updateAutomatically", value);
    }

    public Updater(Action<string> announce)
    {
        _announce = announce;
        _http.DefaultRequestHeaders.Add("User-Agent", $"loafcat/{Branding.Version}");
    }

    public void Start()
    {
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(quiet: true),
            null, FirstCheck, Interval);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
    }

    // MARK: - the swap, which happens before there is a window

    private static string InstalledExe => Environment.ProcessPath ?? "";
    private static string Staged => InstalledExe + ".new";
    private static string Superseded => InstalledExe + ".old";

    /// Called from Main before anything else. Returns true when it relaunched, meaning
    /// this process should exit and let the new one take over.
    public static bool ApplyStagedUpdate()
    {
        if (InstalledExe.Length == 0) return false;

        // Last update's carcass. Deletable now that nothing is running it.
        try { if (File.Exists(Superseded)) File.Delete(Superseded); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        if (!File.Exists(Staged)) return false;

        try
        {
            // Rename rather than delete: Windows holds the running image open, and this
            // is the one operation it still allows on it.
            File.Move(InstalledExe, Superseded, overwrite: true);
            try
            {
                File.Move(Staged, InstalledExe, overwrite: true);
            }
            catch
            {
                // Put it back. An app that fails to update is a nuisance; an app that
                // deletes itself trying is a support request with no way to answer it.
                File.Move(Superseded, InstalledExe, overwrite: true);
                throw;
            }

            Log.Line("update  applied — restarting");
            Process.Start(new ProcessStartInfo(InstalledExe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(InstalledExe)!,
            });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"update: could not apply the staged version ({e.Message})");
            try { File.Delete(Staged); }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    // MARK: - checking

    /// `quiet` is the scheduled check, which says nothing unless there is news. The
    /// Settings button passes false, because a button that can produce no visible result
    /// is a button people press twice.
    public async Task CheckAsync(bool quiet)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (quiet && !Enabled) return;

            Release? release = await LatestRelease().ConfigureAwait(false);
            if (release is null)
            {
                if (!quiet) _announce("Could not reach GitHub.");
                return;
            }

            if (!IsNewer(release.Version, Branding.Version))
            {
                if (!quiet) _announce($"loafcat {Branding.Version} is the latest version.");
                return;
            }

            AvailableVersion = release.Version;

            if (!Enabled)
            {
                _announce($"loafcat {release.Version} is available.");
                return;
            }

            if (release.SignatureUrl is null)
            {
                // Unsigned. Never installed automatically — see the note at the top.
                Log.Warn($"update: {release.Version} carries no signature — not installing");
                _announce($"loafcat {release.Version} is available (unsigned — "
                          + "install it yourself).");
                return;
            }

            if (await Download(release).ConfigureAwait(false))
            {
                StagedAndReady = true;
                _announce($"loafcat {release.Version} is ready — it will be running "
                          + "the next time you open the app.");
            }
            else if (!quiet)
            {
                _announce("That update did not verify. Nothing was installed.");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                    or JsonException or IOException)
        {
            Log.Warn($"update: check failed ({e.Message})");
            if (!quiet) _announce("Could not check for updates.");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private sealed record Release(string Version, string AssetUrl, string? ChecksumUrl,
                                  string? SignatureUrl);

    private async Task<Release?> LatestRelease()
    {
        // `/latest` excludes prereleases, which is what keeps a release candidate from
        // reaching anyone who did not ask for it by name.
        string json = await _http.GetStringAsync(
            $"https://api.github.com/repos/{Repo}/releases/latest").ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        string version = tag.TrimStart('v', 'V');
        if (version.Length == 0) return null;

        string? asset = null, sum = null, sig = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!name.Contains("win-x64", StringComparison.Ordinal)) continue;
            if (name.EndsWith(".exe.sha256", StringComparison.Ordinal)) sum = url;
            else if (name.EndsWith(".exe.sig", StringComparison.Ordinal)) sig = url;
            else if (name.EndsWith(".exe", StringComparison.Ordinal)) asset = url;
        }
        return asset is null ? null : new Release(version, asset, sum, sig);
    }

    /// Downloads, verifies, and stages. Returns false and leaves nothing behind if
    /// anything about it fails to check out.
    private async Task<bool> Download(Release release)
    {
        string temp = Staged + ".part";
        try
        {
            byte[] payload = await _http.GetByteArrayAsync(release.AssetUrl)
                                        .ConfigureAwait(false);

            if (release.ChecksumUrl is { } sumUrl)
            {
                string published = (await _http.GetStringAsync(sumUrl).ConfigureAwait(false))
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.ToLowerInvariant() ?? "";
                string actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
                if (published != actual)
                {
                    Log.Warn($"update: checksum mismatch, discarding {release.Version}");
                    return false;
                }
            }

            byte[] signature = await _http.GetByteArrayAsync(release.SignatureUrl!)
                                          .ConfigureAwait(false);
            if (!VerifySignature(payload, signature))
            {
                Log.Warn($"update: signature does not verify, discarding {release.Version}");
                return false;
            }

            // Written under a temporary name and moved into place, so a download that
            // dies halfway cannot leave something that looks staged.
            await File.WriteAllBytesAsync(temp, payload).ConfigureAwait(false);
            File.Move(temp, Staged, overwrite: true);
            Log.Line($"update  staged {release.Version}, applies on next start");
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                    or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"update: download failed ({e.Message})");
            return false;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// ECDSA P-256 over the file, DER-encoded, against the compiled-in public key.
    internal static bool VerifySignature(byte[] payload, byte[] signature)
    {
        if (UpdateKey.Length == 0) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateKey), out _);
            return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                                    DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// Strictly greater, component by component. A build that somehow finds itself
    /// newer than the release must never "update" backwards into a loop.
    internal static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => v.Split('-')[0].Split('.')
            .Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();

        int[] a = Parts(candidate), b = Parts(current);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    public static void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://github.com/{Repo}/releases/latest")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Log.Warn($"could not open the release page ({e.Message})");
        }
    }
}
