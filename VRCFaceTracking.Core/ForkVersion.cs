using System.Reflection;

namespace VRCFaceTracking.Core;

/// <summary>
/// This fork's own release number, which is deliberately NOT the assembly version.
///
/// The assembly version tracks the upstream VRCFaceTracking release this fork is based
/// on, because tracking modules bind against it - changing it can stop existing modules
/// from loading. The fork's releases are numbered independently, starting at 1.0.0, and
/// that is the number on the GitHub release, in the .exe filename, on the Settings page
/// and in crash reports.
///
/// Both come from <c>Directory.Build.props</c>: bump <c>Fork518Version</c> there and
/// everything else follows.
/// </summary>
public static class ForkVersion
{
    /// <summary>Upstream release this fork is based on, e.g. "5.4.5".</summary>
    public static string UpstreamBase { get; } = ResolveAssemblyVersion();

    /// <summary>This fork's release number, e.g. "1.0.0".</summary>
    public static string Number { get; } = ResolveInformationalVersion();

    /// <summary>
    /// What a human should be shown, e.g. "1.0.0 (518 Edition, based on upstream 5.4.5)".
    /// Always says which upstream release is underneath, so a bug report against this
    /// build can never be mistaken for one against stock VRCFaceTracking.
    /// </summary>
    public static string Display => $"{Number} (518 Edition, based on upstream {UpstreamBase})";

    /// <summary>Short form for window titles and log lines, e.g. "518 Edition v1.0.0".</summary>
    public static string Short => $"518 Edition v{Number}";

    private static string ResolveInformationalVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(ForkVersion).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(informational))
            {
                return "unknown";
            }

            // Strip any "+<commit sha>" build metadata the SDK may append
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ResolveAssemblyVersion()
    {
        try
        {
            var version = (Assembly.GetEntryAssembly() ?? typeof(ForkVersion).Assembly).GetName().Version;
            return version == null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
