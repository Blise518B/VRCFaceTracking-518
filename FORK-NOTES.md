# VRCFaceTracking-518

Private fork of [VRCFaceTracking](https://github.com/benaclejames/VRCFaceTracking) (Apache 2.0) with three additions:

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

`origin` is the private fork (`Blise518B/VRCFaceTracking-518`), `upstream` is
`benaclejames/VRCFaceTracking`.
