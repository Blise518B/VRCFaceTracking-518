// Offline check: when the "What's new" window shows after an update, and what it shows
// (FORK-NOTES.md, "What's new"). The rule is WhatsNew.OnLaunch; the window is not built here.
//
//   dotnet run --project tools/whats-new-check
//
// Exit code 0 when every check passes. Touches nothing outside this process.

using VRCFaceTracking.Core;
using VRCFaceTracking.Core.Services.Updates;

var passed = 0;
var failed = 0;
void That(bool ok, string what)
{
    if (ok) passed++; else failed++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
}

string Versions(IReadOnlyList<WhatsNewRelease> releases) =>
    releases.Count == 0 ? "nothing" : string.Join(", ", releases.Select(r => r.Version));

Console.WriteLine("1. The notes this build carries");
var parsed = WhatsNew.Releases.Select(r => ReleaseUpdater.ParseVersion(r.Version)).ToList();
That(parsed.All(v => v != null), "every release's version parses");
That(parsed.Zip(parsed.Skip(1)).All(p => p.First > p.Second), "newest at the top, no version twice");
That(WhatsNew.Releases.All(r => r.Items.Count > 0), "no release without changes");
var unknown = WhatsNew.Releases.SelectMany(r => r.Items)
    .Where(i => i.Target != "" && !WhatsNew.Targets.Contains(i.Target)).Select(i => i.Target).ToList();
That(unknown.Count == 0, $"every Take me there target exists ({(unknown.Count == 0 ? "all known" : string.Join(", ", unknown))})");
That(WhatsNew.Releases.SelectMany(r => r.Items).All(i => !i.OnlyWithTarget || i.Target != ""),
    "a change that needs its page names one");
var mine = WhatsNew.UpTo(ForkVersion.Number);
Console.WriteLine($"  note: this build is {ForkVersion.Number}; its newest notes are for {(mine.Count > 0 ? mine[0].Version : "no version")}");

Console.WriteLine("2. The rule, with notes for 1.4.0, 1.5.0 and 1.6.0");
var releases = new[]
{
    new WhatsNewRelease("1.6.0", new[] { new WhatsNewItem("Six", "") }),
    new WhatsNewRelease("1.5.0", new[] { new WhatsNewItem("Five", "") }),
    new WhatsNewRelease("1.4.0", new[] { new WhatsNewItem("Four", "") }),
};

void Case(string what, string? seen, string current, bool firstInstall, string expectShow, string? expectMark)
{
    var (show, mark) = WhatsNew.OnLaunch(seen, current, firstInstall, releases);
    That(Versions(show) == expectShow && mark == expectMark,
        $"{what}: shows {Versions(show)}, remembers {mark ?? "(unchanged)"}");
}

Case("first install of 1.5.0", "", "1.5.0", firstInstall: true, "nothing", "1.5.0");
Case("its first update, to 1.6.0", "1.5.0", "1.6.0", false, "1.6.0", "1.6.0");
Case("an install from before the window (1.3.0) updates to 1.4.0", "", "1.4.0", false, "1.4.0", "1.4.0");
Case("the next start of 1.4.0", "1.4.0", "1.4.0", false, "nothing", null);
Case("1.4.0 to 1.6.0, skipping 1.5.0", "1.4.0", "1.6.0", false, "1.6.0, 1.5.0", "1.6.0");
Case("a 1.5.0 build that has 1.6.0's notes in its list", "1.5.0", "1.5.0", false, "nothing", null);
Case("back on 1.5.0 after 1.6.0 (the marker isn't lowered)", "1.6.0", "1.5.0", false, "nothing", null);
Case("then 1.6.0 again", "1.6.0", "1.6.0", false, "nothing", null);
Case("an unreadable marker", "garbage", "1.5.0", false, "1.5.0, 1.4.0", "1.5.0");
Case("a release candidate tag", "1.4.0", "1.5.0-rc1", false, "1.5.0", "1.5.0");
Case("an unreadable own version", "1.4.0", "dev", false, "nothing", null);
Case("a build older than every note", "", "1.3.0", false, "nothing", null);

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
