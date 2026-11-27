namespace VRCFaceTracking.Core.Services.Updates;

/// <summary>One change, told in a title and a sentence or two.</summary>
/// <param name="Target">
/// Where "Take me there" leads: one of the <see cref="WhatsNew"/> target names, or "" when the
/// change has no place in the app (it just works now).
/// </param>
/// <param name="OnlyWithTarget">
/// Leave the change out when its target isn't in the app here (the Dream Air Eyes page without
/// a Pimax): news about a page someone doesn't have is noise.
/// </param>
public sealed record WhatsNewItem(string Title, string Text, string Target = "", bool OnlyWithTarget = false);

public sealed record WhatsNewRelease(string Version, IReadOnlyList<WhatsNewItem> Items);

/// <summary>
/// What changed, told once to someone who just updated (FORK-NOTES.md, "What's new"). The
/// window is <c>VRCFaceTracking/Views/WhatsNewDialog.cs</c>; this is the content and the rule.
///
/// Adding a release: put an entry at the TOP of <see cref="Releases"/>, under the version it
/// ships as (<c>Fork518Version</c> in Directory.Build.props). Keep it to what a user would
/// notice; CHANGELOG.md has the full list. A release with nothing to show has no entry.
/// </summary>
public static class WhatsNew
{
    /// <summary>The setting that remembers the newest version whose notes this install has had.</summary>
    public const string SeenSettingKey = "WhatsNewSeenVersion";

    /// <summary>Settings, scrolled to the Updates card.</summary>
    public const string TargetUpdates = "updates";

    /// <summary>The Dream Air Eyes page (only in the sidebar on a Pimax setup).</summary>
    public const string TargetDreamAirEyes = "dreamaireyes";

    public static readonly IReadOnlyList<string> Targets = new[] { TargetUpdates, TargetDreamAirEyes };

    public static readonly IReadOnlyList<WhatsNewRelease> Releases = new[]
    {
        new WhatsNewRelease("1.4.0", new[]
        {
            new WhatsNewItem("Face tracking survives quick avatar switches",
                "VRChat doesn't always say when you switch avatars, and the app could stay on the one " +
                "before. It now checks every few seconds which avatar you're in, so face tracking " +
                "comes back by itself."),
            new WhatsNewItem("A stopped tracker lets go of your face",
                "When tracking stops (headset off, Virtual Desktop paused), a native face tracking " +
                "avatar no longer freezes mid-expression: VRChat takes the face back until tracking " +
                "returns."),
            new WhatsNewItem("Pages open without hanging",
                "Module Registry, VRChat Output and the other pages no longer wait behind VRChat's " +
                "stream of avatar parameters."),
            new WhatsNewItem("Dream Air Eyes",
                "See which eye tracker drives lids, wide eyes, squint and brows, and switch with one " +
                "click. SRanibro can be the Tobii bridge now, and the bridge restarts itself when it " +
                "started without the eye tracker.",
                TargetDreamAirEyes, OnlyWithTarget: true),
            new WhatsNewItem("What's new, in the app",
                "After an update, this window tells you what changed. Settings, Updates shows it " +
                "again.",
                TargetUpdates),
        }),
    };

    /// <summary>The notes this build has for its own version and older ones, newest first.</summary>
    public static IReadOnlyList<WhatsNewRelease> UpTo(string currentVersion, IReadOnlyList<WhatsNewRelease>? releases = null)
    {
        var current = ReleaseUpdater.ParseVersion(currentVersion);
        if (current == null)
        {
            return Array.Empty<WhatsNewRelease>();
        }

        return (releases ?? Releases)
            .Select(r => (release: r, version: ReleaseUpdater.ParseVersion(r.Version)))
            .Where(r => r.version != null && r.version <= current)
            .OrderByDescending(r => r.version)
            .Select(r => r.release)
            .ToList();
    }

    /// <summary>
    /// What to show at launch, and what to remember as seen. A first install shows nothing (it
    /// isn't an update) but still remembers, so its first update shows only what's new from then.
    /// An install from before this window existed has no marker: it gets every release this build
    /// has notes for, which is its first update's news. Nothing is shown twice.
    /// </summary>
    /// <returns>The releases to show, newest first, and the version to store (null: leave it).</returns>
    public static (IReadOnlyList<WhatsNewRelease> Show, string? MarkSeen) OnLaunch(
        string? seenVersion, string currentVersion, bool firstInstall, IReadOnlyList<WhatsNewRelease>? releases = null)
    {
        var known = UpTo(currentVersion, releases);
        if (known.Count == 0)
        {
            return (Array.Empty<WhatsNewRelease>(), null);
        }

        // Never lowered: back on an older build and then updating again repeats nothing
        var newest = known[0].Version;
        var seen = ReleaseUpdater.ParseVersion(seenVersion);
        var markSeen = seen == null || ReleaseUpdater.ParseVersion(newest) > seen ? newest : null;
        if (firstInstall)
        {
            return (Array.Empty<WhatsNewRelease>(), markSeen);
        }

        var show = seen == null
            ? known
            : known.Where(r => ReleaseUpdater.ParseVersion(r.Version) > seen).ToList();
        return (show, markSeen);
    }
}
