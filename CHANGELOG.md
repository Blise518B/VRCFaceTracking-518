# Changelog

Release numbering for **VRCFaceTracking 518 Edition**, a personal fork. These numbers
are the fork's own and start at 1.0.0; they are unrelated to upstream VRCFaceTracking's
version, which is recorded per release as the base it was built from.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

**Cutting a release**
1. Bump `<Fork518Version>` in `Directory.Build.props` and add a section here.
2. Rebuild (`build.bat`), then run `package.ps1` — it refuses to package a build whose
   stamped version disagrees with the props file.
3. Tag as **`518-vX.Y.Z`**, not `vX.Y.Z`. This repo inherited upstream's tags
   (`v1.0` … `v4.0.0`), so an unprefixed fork tag would collide with an existing
   upstream one the moment the fork reaches 1.1.1, 1.2.0 or 2.0.0.
4. Attach both version-stamped files from `dist\` to the GitHub release.

---

## [1.0.0] - 2026-07-31
Based on upstream VRCFaceTracking 5.4.5.

First numbered release. Everything below already existed in unnumbered builds shared
privately; it is listed here so the starting point is on the record.

### Added
- **Dream Air support** via a merged eye module: Tobii gaze over BrokenEye, with eye
  expressions from either EyeTrackVR or the Baballonia (Project Babble) alpha, switchable
  at runtime. Hybrid eyelid mode uses the camera for graded openness and the headset's
  own tracker as the blink gate.
- **Tracking Compatibility** — translate the tracking dialect between ecosystems, so an
  avatar authored for Quest Pro or Vive behaves correctly on other hardware.
- **Curve Editor** — draw the response curve for ~100 channels including eye gaze,
  per-eye openness and head pose, with per-source profiles that follow the live hardware.
- **Range Calibration** — per-expression floor/ceiling and sensitivity.
- **Sources page** — starts only the modules whose hardware is actually streaming, each
  decision shown with a readable reason.
- **Launch Apps page** — start BrokenEye / EyeTrackVR / Babble from inside the app, or
  automatically, gated on which headset SteamVR reports.
- **Tracking Debug page** — live values for all 88 unified shapes plus eye signals,
  before and after mutations, with source attribution and update ages.
- **VRChat Output page** — what is actually on the wire, read from the real send path.
- **Crash auto-restart** for both modules and the app, with local crash reports written
  to `Documents\VRCFaceTracking\crashes\`.
- **Lite Console** mode and a Neon518 theme.
- **PipelineWatch** — always-on performance instrumentation: per-stage timings, loop
  cadence, and a stall watchdog that logs where the time went.

### Fixed
- **Tracking froze for ~0.5 s every few seconds.** Curve-profile auto-switching ran a
  blocking tracking-source scan (~430 ms measured) on the 100 Hz tracking thread every
  time its 5 s cache expired.
- **A single malformed IPC packet could stop all tracking silently.** The module packet
  handler indexed its module list with a `-1` "not found" sentinel; the resulting
  exception faulted the UDP receive loop permanently, with nothing in the log.
- **Crashes during headset swaps.** Module teardown iterated a collection the IPC thread
  could modify, on a thread with no exception handler — which kills the process.
- **Update loop ran at 64 Hz instead of 100 Hz.** Windows 11 ignores `timeBeginPeriod`
  for minimized or occluded windows, i.e. for the entire VR session.
- Latency and leaks: 100 ms of avoidable delay on every inbound OSC message, a 100 Hz UI
  handler leaked on each visit to Settings, and per-frame allocations throughout the
  send path.

### Changed
- **No crash telemetry.** Upstream's Sentry DSN was still compiled in, which meant this
  fork's crashes were being filed into the upstream maintainer's error tracker and users'
  crash data went to a third party. Removed; crash reports are local-only.
- Distributed files are now version-stamped
  (`VRCFaceTracking-518-Edition-v1.0.0.exe` / `.zip`).
- The app reports its fork version everywhere a version is shown, always alongside the
  upstream release it is based on, so a report from this build is never mistaken for one
  against stock VRCFaceTracking.

[1.0.0]: https://github.com/Blise518B/VRCFaceTracking-518/releases/tag/518-v1.0.0
