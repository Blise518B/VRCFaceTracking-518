# VRCFaceTracking-518

Public fork of [VRCFaceTracking](https://github.com/benaclejames/VRCFaceTracking) (Apache 2.0),
released as **VRCFaceTracking 518 Edition** at <https://github.com/Blise518B/VRCFaceTracking-518>.
These notes explain each addition and why it looks the way it does; README.md is the
user-facing summary and CHANGELOG.md says what changed in each release. It started with
three additions:

1. **Module crash auto-restart** — dead tracking module processes are respawned automatically.
2. **App crash watchdog** — if VRCFaceTracking itself crashes, it relaunches itself.
3. **Range Calibration** — per-expression floor/ceiling remapping with sensitivity curves.

---

## 1. Module crash auto-restart

`VRCFaceTracking.Core/Library/UnifiedLibManager.cs`

A background supervisor thread polls every 2 seconds for sandbox module processes
(`VRCFaceTracking.ModuleProcess.exe`) that died unexpectedly. When one is found:

- Its update pump is stopped and it is removed from the module list.
- The eye/expression tracking slots it occupied are freed.
- If it exited with a **non-zero** exit code (crash), it is respawned and re-initialized
  exactly like at startup. Clean exits (code 0) are not restarted.
- Restarts are capped at **5 per module within 5 minutes** to prevent crash loops. When a
  module hits the cap it is left dead and marked inactive in the UI.

Intentional teardowns (re-initialize, app shutdown) never trigger restarts.

A module that initializes **no** tracking slot - because another module already provides
everything it can do - is not left running either: without an update pump its process
would kill itself after 60 s and look like a crash. It is shut down and put on **standby**
instead, shown greyed out on the Modules page with the module holding the slots. The
supervisor's hot-plug tick starts it again once a slot it can fill frees up (its holder
died or went dormant). A slot it was offered and declined does not count, so a module
configured for face only never cycles on a free eye slot. Any full re-initialize clears
standby.

## 2. App crash watchdog

`VRCFaceTracking.Watchdog/` + wiring in `VRCFaceTracking/App.xaml.cs`

On startup the app spawns `VRCFaceTracking.Watchdog.exe`, a tiny windowless process that
waits for the app to exit:

- **Exit code 0 (normal close):** watchdog exits, nothing happens.
- **Non-zero exit code (crash):** watchdog waits 2 seconds and relaunches the app.
- If another VRCFaceTracking instance is already running (e.g. you started a new one and it
  killed the old one), the watchdog does **not** relaunch.
- Crash loops are capped: max **5 consecutive restarts**. Surviving 2+ minutes resets the
  counter. The counter is threaded through relaunches via the `--restarted-count` argument.

The watchdog logs every decision to `%APPDATA%\VRCFaceTracking\watchdog.log`.

### Crash reports

`VRCFaceTracking.Core/Services/CrashReportService.cs`

Restarting automatically is only half the job — you also want to know *why*. Every crash now
writes a readable report to **`Documents\VRCFaceTracking\crashes\`**, alongside a one-line-per-crash
`history.log` index. The newest 20 reports are kept. Settings → **Crash reports** shows the most
recent one and has a button to open the folder.

Each report contains the app version, how long the session lasted, whether it was itself an
automatic relaunch, an environment block (OS, .NET, memory, and **whether VRChat / SteamVR /
SRanipal were running** — a lot of tracking crashes only happen in particular combinations), and
the last 150 log lines.

Three kinds of crash are covered, because they need completely different handling:

| Kind | How it's caught | What you get |
| --- | --- | --- |
| Managed exception | `Application.UnhandledException`, plus `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` — the latter two matter because tracking code runs on background threads and faulted tasks, which the UI-thread handler never sees | Full exception chain with types, messages and stack traces, inner exceptions included |
| Hard crash | Nothing managed runs at all (native access violation, stack overflow, process killed). Detected on the *next* start via a run marker the dead process left behind | Previous PID/version, how long it survived, and the archived previous log |
| Module process crash | Reported by the sandbox supervisor | Module name, decoded exit code (e.g. `0xC0000005 ACCESS_VIOLATION`), and the module's own stdout/stderr tail |

Two things this required fixing along the way:

- `LogFileProvider` opens `latest.log` with `FileMode.Create`, so **every start wiped the previous
  run's log**. With the watchdog relaunching automatically, the evidence was being destroyed
  seconds after the crash. The log is now archived to `crashes\previous-run.log` before logging
  initialises.
- Module process stdout/stderr was only redirected in `DEBUG` builds and thrown away otherwise.
  It's now captured in every configuration into a bounded rolling buffer, because native tracking
  SDKs report their failures there rather than through .NET.

> Note: killing the app from Task Manager is indistinguishable from a hard crash, so it will be
> reported as one. `update.bat` clears the marker itself, so rebuilding doesn't create a phantom
> report.

### Settings section

Settings has a **Crash Auto-Restart** section (on by default) that controls both the module
supervisor and the app watchdog. Turning it off kills the watchdog immediately and stops
module resurrection; turning it on brings both back without a restart. The expander shows
how many module restarts and app relaunches have happened, a timestamped log of the last
50 restart events, and a reset button. Counters and the log persist across sessions.

## 3. Range Calibration

`VRCFaceTracking.Core/Params/Data/Mutation/RangeCalibration.cs`, UI in `MutatorPage.xaml`

A new entry on the **Tracking Mutations** page. For **every one of the 88 unified
expressions** you get:

- **Enabled** checkbox — exclude individual expressions from remapping.
- **Floor / Ceiling** range slider — the raw tracking range that maps to 0..1 output.
  Example: if your tracker only ever reports JawOpen up to 0.6, set the ceiling to 0.6 and
  you get full jaw movement in game.
- **Curve** slider (0.25–4) — per-expression sensitivity response:
  - `< 1` = more sensitive: small real movements produce big avatar movements early.
  - `1` = linear.
  - `> 1` = less sensitive: needs bigger real movement before the avatar reacts.

Global controls:

- **Effect Blend** — fades the whole remap in/out (1 = fully applied).
- **Capture Neutral (3s)** — hold a relaxed face; observed noise levels + margin become the
  floors so your resting face maps to zero.
- **Capture Maximums (10s)** — exaggerate your expressions; observed peaks become the
  ceilings. Only expressions you actually performed are updated.
- **Reset All Ranges** / **Save Ranges Now**.

Settings persist like all other mutations (also saved when toggling the mutation and on exit).

> Tip: the stock "Calibration" and "Parameter Adjustment" mutations overlap with this one.
> Don't run them at the same time as Range Calibration unless you know what you're doing.

## 4. Curve Editor

`VRCFaceTracking.Core/Params/Data/Mutation/Curves/`, `CurveCalibration.cs`,
UI in `Controls/CurveEditorControl.xaml` + `Views/CurvesPage.xaml`

A **Curve Editor** page in the nav bar. Each tracking channel gets a full input/output response
curve you shape by hand instead of a single sensitivity number:

- **Click** empty plot space to add a point, **drag** points to reshape, **right-click** a point
  to delete it. Endpoints stay pinned horizontally so the curve always covers the full range.
- Interpolation is monotone cubic (Fritsch-Carlson): smooth, but mathematically incapable of
  overshooting the points you placed, so output never wiggles outside 0..1.
- A dashed diagonal shows the untouched 1:1 response, and an **orange live dot** shows where your
  face currently sits on the curve — so you can tune while making the expression.
- Tick channels in the left list to open them; **two editors sit side by side**, so you can open
  **Eye Openness Left and Right together and compare them live**. "Open both eyes" does it in one
  click. Open channels persist while you navigate around the app.

**100 channels are covered**, importantly including ones that are *not* unified expression
shapes and so are invisible to every other mutation:

| Category | Channels |
| --- | --- |
| Eyes | Eye openness left / right |
| Eye Gaze | Gaze horizontal + vertical, per eye |
| Head | Rotation yaw/pitch/roll, position X/Y/Z |
| Expressions | All 88 unified expression shapes |

Bipolar channels (gaze, head) are folded into 0..1 for editing and expanded back afterwards, so
every editor reads the same way.

Nothing changes until you bend a curve: every curve starts as a straight 1:1 line, each channel
has its own on/off switch, and there's a master "Apply curves" toggle (which also enables the
mutator, so curves can't silently do nothing). Curves are saved automatically shortly after each
edit.

*Use case this was built for:* one eye tracking as more open than the other. Open both openness
channels side by side, drag the over-open eye's top-right endpoint down until the eyes match, and
leave the other one alone.

---

## 6. Source auto-detection

`VRCFaceTracking.Core/Services/TrackingSourceDetector.cs` + `ModuleActivationService.cs`,
UI on the **Sources** page

Stock VRCFT launches a process for *every* installed module and lets each one fail its own
initialisation. This works out which tracking sources are actually live and starts only the
matching modules — so if you're on Virtual Desktop, the ETVR module doesn't start at all.

Detection uses read-only OS queries: running process names, memory-mapped files and named
pipes. Nothing is opened exclusively, so probing can never take a device from the module that
needs it. Known sources: Virtual Desktop, ALVR, Steam Link, Meta/Oculus Link,
SRanipal (Vive), EyeTrackVR, Project Babble, Varjo, Pimax, Tobii, and a "simulated" entry that
never auto-detects.

Two Quest Pro sources need more than a process name, because both run through SteamVR:
**Virtual Desktop** counts while its face data in `VirtualDesktop.BodyState` is changing, and
**Steam Link** counts while SteamVR's headset was created by Steam Link's driver, `vrlink`.
That comes from the HMD's `Prop_ParentDriver_Uint64`, named through SteamVR's driver list.
Nothing the headset reports about itself works: over Steam Link a Quest Pro has manufacturer
"Oculus", tracking system "oculus" and registered device type "oculus/VRLINKHMDQUESTPRO"
(measured 2026-11-07). SteamVR merely running is not evidence for Steam Link: as weak
evidence it won whenever Virtual Desktop's data paused, and the hot-swap flipped modules every
time the headset came off. The driver comes from the app's OpenVR connection
(`TrackingSourceDetector.HmdDriverResolver`); the lite build has none, so there the Steam Link
module needs **Always**. Steam Link itself needs SteamVR → Steam Link: Enable OSC, eye and face
sharing, and OSC output port 9015, where the Steam Link module listens.

Each module can be set to **Auto** (follow detection), **Always** or **Never**, and every
decision comes with a readable reason shown in the UI and written to the log — a silent skip
would be indistinguishable from a bug. Two deliberate design rules:

- **Unrecognised modules still start.** A module we have no mapping for might be brand-new
  hardware; refusing to start it would look like the app is broken.
- **Shared VR runtimes lose to specific sources.** `OVRServer_x64` runs whenever the Meta
  software is installed, so it's marked as weak evidence. If something specific is streaming
  (Virtual Desktop's live face data, say) then modules backed only by a generic runtime
  are skipped. With nothing specific anywhere, a generic runtime is accepted rather than leaving
  you with no tracking at all.

> No UDP port probing, deliberately. Measuring a live setup showed port 8889 held by
> VRCFaceTracking's **own** ETVR module process — a port probe reports our own listener, so it
> would keep a module "detected" forever once it had started once.

---

## 7. Companion app launch buttons

`VRCFaceTracking.Core/Services/CompanionAppService.cs`, UI on the **Sources** page

Source detection tells you BrokenEye isn't running — the launch buttons fix it without
alt-tabbing. Each tracking source row on the Sources page has a gear that stores a **launch
command** (an exe path, optionally quoted with arguments, or a URI like
`steam://rungameid/4091970` for Steam apps). Whenever a configured source is *not* detected,
a **Launch** button appears; one click starts the app, and the row flips to ACTIVE as soon as
detection sees it (the page re-scans itself a few seconds after launching).

Design rules:

- **Launch-only.** Nothing is monitored, restarted, or killed. Quitting an app mid-session
  (e.g. swapping EyeTrackVR for Baballonia to A/B compare eye tracking) is never fought.
- **Never double-starts.** If the configured exe already has a process, the button reports
  "already running" instead of launching a second copy.
- Exe launches get their working directory set to the exe's folder (apps like BrokenEye load
  their config relative to the exe). URIs go through the shell, so Steam handles its own apps.
- Commands persist in settings under `CompanionAppCommands`. Defaults until overridden:
  Project Babble = the Baballonia Steam URI, BrokenEye and EyeTrackVR = this machine's
  installs (path defaults only apply while the file actually exists, so they vanish cleanly
  on another PC instead of showing a broken button). A **Browse...** button in the flyout
  picks exe/bat/lnk files without path-pasting.
- Process detection supports a trailing `*` for prefix matches — BrokenEye's release exes
  carry the version in the name (`BrokenEye_v1.4.0.exe`), so its source entry uses
  `BrokenEye*` and survives updates.

### Startup auto-launch

Each source's gear flyout also has a **"Launch with VRCFaceTracking"** toggle (persisted under
`CompanionAppAutoLaunch`). BrokenEye and EyeTrackVR default to **on** (they are required for
Dream Air eye tracking); everything else defaults to off. At app startup, every source with
the toggle on and a command configured is started — unless detection already sees it running. Combined with the SteamVR
launch-mode switch, one action brings up the whole stack: SteamVR → VRCFT → BrokenEye + camera
apps → modules auto-activate.

- The skip check makes startup idempotent: watchdog relaunches after a crash never spawn
  duplicate companion apps.
- After launching, the detector cache is force-refreshed so the module start decisions that
  follow in the same startup see the new processes. (Steam-URI apps can take longer to appear
  than plain exes; those sources may need one re-initialise on a cold start.)
- Auto-launch failures are logged and never block startup.

Also added a **BrokenEye (Dream Air Tobii bridge)** entry to the known tracking sources
(process names `BrokenEye`/`BrokenEyeApp`), and the Baballonia process names to the Project
Babble entry — so both detect properly and can carry launch buttons.

---

## 8. Tracking Debug page

`VRCFaceTracking/Views/TrackingDebugPage.xaml` + `ViewModels/TrackingDebugViewModel.cs`

A **Tracking Debug** entry in the nav pane: a live table of every tracking value —
eight eye signals (per-eye gaze X/Y, openness, pupil mm) and all 88 unified expression
shapes. Per row: a bar, the **raw** value (what the module wrote), the **mut** value
(after Tracking Mutations / curves — so you can also see what your curves are doing),
the **source**, and how long ago it last changed (amber dot = moved in the last 0.4 s).
Filter box + "only moving" checkbox cut the list down while making a face. Refreshes at
10 Hz, only while the page is on screen.

Source attribution has two levels:

- **Slot owners** (always available): which module owns the eye slot and which owns the
  expression slot, straight from the lib manager.
- **Fine-grained routing** (when the DreamAir518 module runs): the sandboxed module
  can't be inspected from the app, so it publishes `DreamAirRouting.json` next to its
  DLL (rewritten 4×/s) with its internal routing — gaze=Tobii/fallback, lids/shapes=
  ETVR or Babble with message rates, pupil source, face-relay state. The page picks the
  file up from CustomLibs, treats it as stale after 3 s, and labels the eye-range rows
  with the true per-signal source.

### Expression source switch

The page tops out with a big **EYE EXPRESSIONS: ETVR** (cyan) / **BABBLE** (yellow)
badge showing what is *actually* driving right now, plus an Auto / ETVR / Babble
selector. Switching sends `/command/set/ExpressionSource` over the module's own OSC
command channel (the same mechanism ETVR's GUI uses for settings), so the change is
instant, restart-free, and persisted by the module itself. The selector reflects the
mode back from the routing file (2 s grace after a click so the lagging file can't
snap it back); it disables itself when the module doesn't publish routing info.

---

## 5. Neon518 look

`VRCFaceTracking/Styles/NeonTheme.xaml` (merged last in `App.xaml`)

The interface is restyled to match the default **"neon"** theme of
[VRC Parameter Relay](https://github.com/Blise518B/VRCParameterRelay), so both apps look like
one set. Palette taken verbatim from that project's `ui/theme.py`:

| Role | Colour |
| --- | --- |
| Background | `#0a0c0a` |
| Panel / nav / header | `#0f120f` |
| Card | `#121712` (alt `#0e120e`) |
| Inputs | `#121a13` |
| Hairline border | `#36a35c` |
| Accent | `#31f272` (on-accent text `#04150b`) |
| Text | `#e6efe8` → dim `#94a698` → faint `#5f6f63` |
| Danger / switch off | `#ff5561` / `#a51830` |
| Amber (live marker) | `#ffb454` |

Corner radii are bumped to match (controls 8px, cards 12px, chips 10px). Toggle switches go
red-when-off / green-when-on like the relay's ON/OFF pills, sliders use the green track, and the
curve editor plot uses the accent green for the curve with the amber live dot.

Implementation note: this is done purely by **overriding WinUI theme brushes and corner radii** —
no control templates are rewritten. That keeps it robust across WinUI updates and avoids breaking
built-in control behaviour. The brushes sit at dictionary top level rather than inside
`ThemeDictionaries`, so the neon look applies regardless of the Windows light/dark setting.

---

## 9. VRChat native face tracking

`VRCFaceTracking.Core/OSC/FaceTrackingBlob.cs`, `VRCFaceTracking.Core/Services/NativeFaceTrackingSender.cs`

VRChat's open beta (Avatars SDK 3.10.6-beta.1 and up) can drive an avatar's face itself. An
avatar uploaded with **Use All Tracking Keys** in its Face Tracking settings takes all 102
Unified Expressions keys from one OSC message, and VRChat syncs them to other players without
any synced parameters. This feeds it the way upstream 6.0 does, ported onto the 5.4.5 base:

- **Detection.** VRChat's OSCQuery marks such an avatar with `/avatar/avatarfullfacetracking`
  (`IAvatarInfo.FullFaceTracking`; `fullFaceTracking` in the OSC config JSON counts too).
- **Sending.** For those avatars every tracking tick sends `/tracking/face/v1`: one blob of
  exactly 102 bytes, one byte per key (value x 255), in VRChat's fixed order. First the 14 eye
  keys (look in/out/up/down per eye from gaze, closed from openness, dilation and constriction
  from pupil size around 5 mm), then the 88 expressions in `UnifiedExpressions` order. It is
  encoded after the mutations, so curves, calibration and Neutral Override all apply.
- **Nothing else.** Such an avatar gets no v2 parameters and no `/tracking/eye/*` messages,
  whatever it declares. Loading a normal avatar turns them back on.
- **Built in C#, not by fti_osc.** The 132-byte datagram's header is written once; each tick
  only rewrites the payload, without allocating. Upstream rebuilt `fti_osc` with a blob type
  instead, which changes the `OscValue` layout under every message sent and received - only
  worth taking together with the rest of 6.0.
- **Avatar name.** VRChat 2026.4.1 (open beta) serves `/avatar/name` over OSCQuery, used when
  present. Older builds still get the name from the OSC config JSON, but only the name: that
  lookup used to reset every parameter a second time and clear the send queue on every load.

Where to see it: the Home page's avatar card shows **Native face tracking** (and 0 parameters),
the lite monitor shows "native face tracking" instead of a parameter count, the VRChat Output
page lists `/tracking/face/v1` as a 132-byte blob at about 100/s, and
`tools/osc-capture/check.py` prints a `[native]` line with the blob rate and sizes.

Not verified against VRChat yet: the eye-look scale. Gaze goes out as its raw tangent clamped
to 0-1 (1 = 45 degrees), the same as upstream; VRChat doesn't document what angle
`EyeLook* = 1` stands for.

Upstream counterparts, for later syncs: `BinaryFaceDataSender` and `UnifiedTrackingData.CopyTo`
(merged with the Avalonia rewrite, PR #372), the pupil bytes from PR #395, `/avatar/name` from
PR #393.

---

## 10. Updates and releases

`VRCFaceTracking.Core/Services/Updates/` (`ReleaseUpdater.cs` does the work, `UpdateService.cs`
is what the UI binds to), `Helpers/UpdateInstaller.cs`, the banner in `ShellPage.xaml`, the
Updates card in `SettingsPage.xaml`, and `.github/workflows/build.yml`.

**Releases are built by GitHub.** The Build workflow builds and packages every push (files
attached to the run for 14 days). A pushed `518-vX.Y.Z` tag publishes a release, after
checking the tag equals `<Fork518Version>` and CHANGELOG.md has a `## [X.Y.Z]` section, which
becomes the release notes. The steps are at the top of CHANGELOG.md. Asset names never carry
a version (`VRCFaceTracking-518-Windows.exe` / `.zip`): the README links to
`releases/latest/download/<name>` and the updater picks the zip by name. Upstream's Steam
publish workflow was removed: it needs upstream's Crowdin and Steam secrets and failed on
every push here.

**The updater** checks `releases/latest` once per start, 15 s after launch (drafts and
pre-releases never count). A newer version shows a banner above every page; nothing
installs without a click, because an update restarts the app and tracking must not drop out
mid-session over a release. **Update now**:

1. downloads `VRCFaceTracking-518-Windows.zip` to `%TEMP%\VRCFaceTracking-518-update\<version>\`,
   checking the size and the SHA-256 digest the GitHub API lists for the asset;
2. unpacks it and checks it contains `VRCFaceTracking.exe` and a `518-release.json` naming
   the version the release tag names;
3. saves `UpdatePendingVersion`, starts `apply-update.ps1` hidden, then shuts down like the
   lite hand-off: watchdog off first (it would otherwise relaunch the old version as a
   "crash"), clean teardown, crash marker cleared, exit code 0;
4. the script waits for the app, then for anything else running from the install folder
   (module processes, watchdog, lite; stopped after 20 s), backs up every file the new
   version replaces, `robocopy /E`s the new files over the folder (never `/MIR`: a portable
   folder may share a directory with the user's own files), restores the backup if robocopy
   fails, starts the app and deletes the work folder. It logs to
   `%APPDATA%\VRCFaceTracking\update.log`;
5. the next start compares its version with `UpdatePendingVersion` and says in the banner
   whether the update installed.

Only a folder with `518-release.json` (written by `package.ps1`) replaces itself, so a build
from source in `bin\` is never overwritten; neither is a folder it can't write to (Program
Files) or a drive root. Those get the release page instead. The one-file exe unpacks to
`%LOCALAPPDATA%\VRCFaceTracking-518\app\`, which carries the marker, so it updates in place
too; running an older one-file exe afterwards still starts the updated app, because its
payload hash matches the `installed.txt` it wrote.

Tested with a console harness (local HTTP server, the real release zip): version parsing,
asset picking, wrong-digest and truncated downloads rejected and deleted, the helper waiting
for its parent, an unrelated file in the install folder kept, a path with a space, a quote and
non-ASCII letters, and a file locked for writing making robocopy fail and the backup come
back. The real path from one published release to the next can only be tried once a second
release exists.

---

## Building

Requires .NET 10 SDK on Windows.

```
dotnet build VRCFaceTracking.sln -c Release -p:Platform=x64 -p:WindowsPackageType=None
```

Output: `VRCFaceTracking\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\VRCFaceTracking.exe`
(with `VRCFaceTracking.ModuleProcess.exe` and `VRCFaceTracking.Watchdog.exe` alongside).

## Syncing with upstream

```
git fetch upstream
git merge upstream/master
```

`origin` is this fork (`Blise518B/VRCFaceTracking-518`, public since 2026-11-08), `upstream` is
`benaclejames/VRCFaceTracking`.
