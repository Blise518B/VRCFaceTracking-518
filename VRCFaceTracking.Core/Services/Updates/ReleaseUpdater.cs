using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VRCFaceTracking.Core.Helpers;

namespace VRCFaceTracking.Core.Services.Updates;

/// <summary>One downloadable file attached to a GitHub release.</summary>
/// <param name="Sha256">Lower-case hex digest GitHub reports for the file, or null when it reports none.</param>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>The parts of a GitHub release the updater needs.</summary>
/// <param name="Version">The tag as a plain version, e.g. "1.2.0" for the tag "518-v1.2.0".</param>
/// <param name="Zip">The portable zip the updater installs, or null when the release has none (yet).</param>
public sealed record ReleaseInfo(string Tag, string Version, string PageUrl, ReleaseAsset? Zip);

/// <summary>An update step that failed, with a message fit to show the user as it is.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Self-update from this fork's GitHub releases. No UI and no services in here, so every
/// step can be exercised from a console harness:
///
///   1. <see cref="FetchLatestAsync"/> asks GitHub for the newest release (drafts and
///      pre-releases never count) and picks the portable zip attached to it.
///   2. <see cref="DownloadAsync"/> streams that zip to %TEMP% and checks its size and the
///      SHA-256 digest GitHub reports before anything trusts it.
///   3. <see cref="Stage"/> unpacks it next to the download and checks it really is this
///      app, at the version the release claims.
///   4. <see cref="StartSwapHelper"/> starts a hidden PowerShell script that waits for the
///      app to close, copies the staged files over the install folder (backing up what it
///      replaces and putting it back if the copy fails), and starts the app again.
///
/// Only a build that came out of package.ps1 replaces itself: it carries
/// <see cref="ReleaseMarkerFile"/>. A development build in bin\ never does, so an update
/// can never overwrite someone's build output.
/// </summary>
public static class ReleaseUpdater
{
    public const string Repository = "Blise518B/VRCFaceTracking-518";

    /// <summary>
    /// The release asset the updater installs. The name never carries a version, so the
    /// README's download links can point at releases/latest/download/... for good.
    /// </summary>
    public const string ZipAssetName = "VRCFaceTracking-518-Windows.zip";

    /// <summary>Folder at the root of the zip that holds the app.</summary>
    public const string AppFolderInZip = "VRCFaceTracking-518-Edition";

    /// <summary>Written into every packaged build by package.ps1; its presence is what allows self-update.</summary>
    public const string ReleaseMarkerFile = "518-release.json";

    public const string MainExeName = "VRCFaceTracking.exe";

    public static string LatestReleaseApi => $"https://api.github.com/repos/{Repository}/releases/latest";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";
    public static string IssuesPage => $"https://github.com/{Repository}/issues";

    /// <summary>Everything an update downloads and unpacks lives under here, one folder per version.</summary>
    public static string WorkDirectory => Path.Combine(Path.GetTempPath(), "VRCFaceTracking-518-update");

    /// <summary>The swap helper writes what it did here, next to the watchdog's log.</summary>
    public static string LogPath => Path.Combine(Utils.PersistentDataDirectory, "update.log");

    // Far above what this app's zip can be (about 90 MB) and far below a surprise worth
    // writing to someone's disk - a wrong asset or an error page lying about its length.
    private const long MaxAssetBytes = 600L * 1024 * 1024;

    private static readonly Regex VersionPattern =
        new(@"^(?:518-)?[vV]?(\d+(?:\.\d+){0,3})(?:[-+ ].*)?$", RegexOptions.CultureInvariant);

    // -- versions ---------------------------------------------------------------------

    /// <summary>
    /// "518-v1.2.0", "v1.2.0" and "1.2.0" all give 1.2.0; a suffix such as "-rc1" is
    /// ignored. Anything else gives null.
    /// </summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = VersionPattern.Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var parts = match.Groups[1].Value.Split('.').Select(int.Parse).ToList();
        while (parts.Count < 3)
        {
            parts.Add(0);
        }

        return parts.Count == 4
            ? new Version(parts[0], parts[1], parts[2], parts[3])
            : new Version(parts[0], parts[1], parts[2]);
    }

    /// <summary>True only when both versions parse and <paramref name="remote"/> is the newer one.</summary>
    public static bool IsNewer(string? remote, string? local)
    {
        var a = ParseVersion(remote);
        var b = ParseVersion(local);
        return a != null && b != null && a > b;
    }

    // -- what this install can do -----------------------------------------------------

    /// <summary>The folder the running app lives in, without a trailing separator.</summary>
    public static string InstallDirectory =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

    /// <summary>
    /// Null when this install can replace itself, otherwise a sentence saying why not.
    /// </summary>
    public static string? SelfUpdateBlocker(string installDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Updating itself only works on Windows.";
        }

        if (!File.Exists(Path.Combine(installDirectory, ReleaseMarkerFile)))
        {
            return "This is a build from source, so it never overwrites itself. Pull and rebuild instead.";
        }

        if (string.Equals(Path.GetPathRoot(installDirectory)?.TrimEnd('\\', '/'), installDirectory.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            return "The app sits at the root of a drive. Move it into a folder to let it update itself.";
        }

        if (!IsWritable(installDirectory))
        {
            return "Windows doesn't let the app write to its own folder (Program Files?). " +
                   "Move the folder somewhere like Documents to let it update itself.";
        }

        return null;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".update-probe-{Environment.ProcessId}");
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // -- GitHub -----------------------------------------------------------------------

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = HappyEyeballsHttp.CreateHttpClient();
        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VRCFaceTracking-518-updater", ForkVersion.Number));
        return client;
    }

    /// <summary>
    /// The newest published release. Throws <see cref="UpdateException"/> when GitHub
    /// can't be reached or answers with something that isn't a usable release.
    /// </summary>
    public static async Task<ReleaseInfo> FetchLatestAsync(CancellationToken ct = default)
    {
        string json;
        try
        {
            using var client = CreateClient(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.SendAsync(request, ct);

            if ((int)response.StatusCode == 404)
            {
                throw new UpdateException("No release has been published on GitHub yet.");
            }

            if ((int)response.StatusCode is 403 or 429)
            {
                throw new UpdateException("GitHub is rate-limiting update checks from this network. Try again in an hour.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new UpdateException("Couldn't reach GitHub to check for updates.", e);
        }

        return ParseRelease(json) ?? throw new UpdateException("GitHub's answer didn't look like a release.");
    }

    /// <summary>Reads GitHub's release JSON; null when the tag isn't a version.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var version = ParseVersion(tag);
            if (version == null)
            {
                return null;
            }

            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;

            ReleaseAsset? zip = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!string.Equals(name, ZipAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;

                    string? sha = null;
                    var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() ?? ""
                        : "";
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        var hex = digest[7..].Trim().ToLowerInvariant();
                        if (Regex.IsMatch(hex, "^[0-9a-f]{64}$"))
                        {
                            sha = hex;
                        }
                    }

                    zip = new ReleaseAsset(name, url, size, sha);
                    break;
                }
            }

            return new ReleaseInfo(tag, version.ToString(3), page, zip);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // -- download ---------------------------------------------------------------------

    /// <summary>
    /// Downloads <paramref name="asset"/> into this version's work folder and verifies it.
    /// Returns the verified file. A file that fails verification is deleted, never left
    /// around to be unpacked by accident. Throws <see cref="UpdateException"/>.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseAsset asset, string version,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (asset.Size > MaxAssetBytes)
        {
            throw new UpdateException("The release file is far bigger than this app can be, so it wasn't downloaded.");
        }

        var folder = Path.Combine(WorkDirectory, version);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, ZipAssetName);
        var partial = target + ".part";

        try
        {
            using var client = CreateClient(TimeSpan.FromMinutes(15));
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"The download failed: GitHub answered {(int)response.StatusCode}.");
            }

            var total = asset.Size > 0 ? asset.Size : response.Content.Headers.ContentLength ?? 0;
            if (total > MaxAssetBytes)
            {
                throw new UpdateException("The release file is far bigger than this app can be, so it wasn't downloaded.");
            }

            using var sha = SHA256.Create();
            long done = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                var buffer = new byte[1 << 16];
                int read;
                var lastReport = -1.0;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (done > MaxAssetBytes)
                    {
                        throw new UpdateException("The download kept going far past the size this app can be, so it was stopped.");
                    }

                    if (total > 0)
                    {
                        var percent = Math.Min(100.0, done * 100.0 / total);
                        if (percent - lastReport >= 0.5)
                        {
                            lastReport = percent;
                            progress?.Report(percent);
                        }
                    }
                }
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            if (asset.Size > 0 && done != asset.Size)
            {
                throw new UpdateException($"The download is incomplete ({done:N0} of {asset.Size:N0} bytes). Try again.");
            }

            var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (asset.Sha256 != null && actual != asset.Sha256)
            {
                throw new UpdateException("The downloaded file doesn't match the checksum GitHub lists for it, so it was thrown away.");
            }

            File.Move(partial, target, true);
            progress?.Report(100);
            return target;
        }
        catch (UpdateException)
        {
            TryDelete(partial);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(partial);
            throw;
        }
        catch (Exception e)
        {
            TryDelete(partial);
            throw new UpdateException("The download failed. Check your connection and try again.", e);
        }
    }

    // -- unpack -----------------------------------------------------------------------

    /// <summary>
    /// Unpacks a verified zip next to it and returns the folder holding the new app.
    /// Refuses a zip that doesn't contain this app at <paramref name="expectedVersion"/>.
    /// </summary>
    public static string Stage(string zipPath, string expectedVersion)
    {
        var folder = Path.GetDirectoryName(zipPath)!;
        var staged = Path.Combine(folder, "staged");

        try
        {
            if (Directory.Exists(staged))
            {
                Directory.Delete(staged, true);
            }

            // ExtractToDirectory refuses entries that would land outside the folder
            ZipFile.ExtractToDirectory(zipPath, staged);
        }
        catch (Exception e)
        {
            throw new UpdateException("The downloaded update couldn't be unpacked.", e);
        }

        var appDir = Path.Combine(staged, AppFolderInZip);
        if (!File.Exists(Path.Combine(appDir, MainExeName)))
        {
            throw new UpdateException($"The downloaded update doesn't contain {MainExeName}.");
        }

        var markerPath = Path.Combine(appDir, ReleaseMarkerFile);
        string? stagedVersion = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(markerPath));
            stagedVersion = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception)
        {
            // handled below
        }

        if (ParseVersion(stagedVersion) is not { } staged3 || ParseVersion(expectedVersion) is not { } expected3 || staged3 != expected3)
        {
            throw new UpdateException($"The downloaded update says it is version {stagedVersion ?? "unknown"}, not {expectedVersion}.");
        }

        return appDir;
    }

    // -- swap ------------------------------------------------------------------------

    /// <summary>
    /// Starts the hidden helper that installs <paramref name="stagedAppDir"/> over
    /// <paramref name="installDirectory"/> once this process (<paramref name="parentPid"/>)
    /// has exited. The caller exits right after. Throws <see cref="UpdateException"/> when
    /// the helper can't be started - nothing has been touched at that point.
    /// </summary>
    public static Process StartSwapHelper(string stagedAppDir, string installDirectory, int parentPid, bool relaunch = true)
    {
        var workDir = Path.GetDirectoryName(Path.GetDirectoryName(stagedAppDir)!)!;
        var script = Path.Combine(workDir, "apply-update.ps1");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(script, SwapScript, new UTF8Encoding(true));

            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(File.Exists(powershell) ? powershell : "powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };
            foreach (var arg in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                         "-File", script,
                         "-ParentPid", parentPid.ToString(),
                         "-Source", stagedAppDir,
                         "-Target", installDirectory,
                         "-WorkDir", workDir,
                         "-Log", LogPath,
                         "-Relaunch", relaunch ? Path.Combine(installDirectory, MainExeName) : "",
                     })
            {
                info.ArgumentList.Add(arg);
            }

            return Process.Start(info) ?? throw new UpdateException("Windows didn't start the update helper.");
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new UpdateException("The update helper couldn't be started.", e);
        }
    }

    /// <summary>Deletes work folders of versions other than <paramref name="keepVersion"/>.</summary>
    public static void CleanWorkDirectory(string? keepVersion = null)
    {
        try
        {
            if (!Directory.Exists(WorkDirectory))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(WorkDirectory))
            {
                if (keepVersion == null || !string.Equals(Path.GetFileName(dir), keepVersion, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch (Exception)
                    {
                        // a folder still in use is cleaned on a later start
                    }
                }
            }
        }
        catch (Exception)
        {
            // temp housekeeping is never worth an error
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    /// <summary>
    /// The helper. Paths arrive as arguments, never pasted into the script, so a folder
    /// name with quotes or non-ASCII letters can't break it. Trailing backslashes are
    /// trimmed before robocopy sees a path: "C:\x\" would escape its closing quote.
    /// </summary>
    internal const string SwapScript = """
        # Written by VRCFaceTracking 518 Edition's updater. Runs after the app has closed:
        # copies the downloaded version over the install folder, then starts it again.
        param(
            [Parameter(Mandatory = $true)][int]$ParentPid,
            [Parameter(Mandatory = $true)][string]$Source,
            [Parameter(Mandatory = $true)][string]$Target,
            [Parameter(Mandatory = $true)][string]$WorkDir,
            [Parameter(Mandatory = $true)][string]$Log,
            [string]$Relaunch = ''
        )
        $ErrorActionPreference = 'Continue'
        $Source = $Source.TrimEnd('\')
        $Target = $Target.TrimEnd('\')
        $robocopy = Join-Path $env:SystemRoot 'System32\robocopy.exe'

        function Write-Log([string]$Message) {
            try { Add-Content -LiteralPath $Log -Value ('{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $Message) -Encoding UTF8 } catch { }
        }

        Write-Log "Update: waiting for VRCFaceTracking (pid $ParentPid) to close"
        Wait-Process -Id $ParentPid -Timeout 120 -ErrorAction SilentlyContinue

        # Module processes, the crash watchdog or the lite console can hold files in the
        # install folder open for a moment after the app itself is gone.
        $prefix = $Target + '\'
        $busy = @()
        $deadline = (Get-Date).AddSeconds(20)
        do {
            $busy = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
            if ($busy.Count -eq 0) { break }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $deadline)
        if ($busy.Count -gt 0) {
            Write-Log ('Stopping what still runs from the install folder: ' + (($busy | ForEach-Object { '{0} ({1})' -f $_.ProcessName, $_.Id }) -join ', '))
            $busy | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
        }

        # Back up every file the new version replaces, so a copy that fails half-way can be undone
        $backup = Join-Path $WorkDir 'backup'
        $ok = $true
        try {
            Get-ChildItem -LiteralPath $Source -Recurse -File -ErrorAction Stop | ForEach-Object {
                $relative = $_.FullName.Substring($Source.Length + 1)
                $existing = Join-Path $Target $relative
                if (Test-Path -LiteralPath $existing -PathType Leaf) {
                    $dest = Join-Path $backup $relative
                    New-Item -ItemType Directory -Force -Path (Split-Path -Path $dest -Parent) | Out-Null
                    Copy-Item -LiteralPath $existing -Destination $dest -Force -ErrorAction Stop
                }
            }
        } catch {
            Write-Log ('Backing up the current version failed, so it was left alone: ' + $_.Exception.Message)
            $ok = $false
        }

        if ($ok) {
            & $robocopy $Source $Target /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            $code = $LASTEXITCODE
            if ($code -ge 8) {
                Write-Log "Copying the new version failed (robocopy exit $code), putting the previous version back"
                if (Test-Path -LiteralPath $backup) {
                    & $robocopy $backup $Target /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
                    Write-Log "Previous version restored (robocopy exit $LASTEXITCODE)"
                }
            } else {
                Write-Log "Update installed into $Target"
            }
        }

        if ($Relaunch -and (Test-Path -LiteralPath $Relaunch -PathType Leaf)) {
            Start-Process -FilePath $Relaunch -WorkingDirectory $Target
            Write-Log "Started $Relaunch"
        }

        Set-Location -LiteralPath $env:TEMP
        Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
        """;
}
