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

## [Unreleased]

### Added
- **VRChat native face tracking (open beta).** Avatars uploaded with "Use All Tracking
  Keys" get the whole face from one `/tracking/face/v1` message per tick - all 102 Unified
  Expressions keys, eyes and pupils included - and VRChat syncs it itself, so no synced
  parameters are spent on it. Such an avatar gets no v2 parameters or `/tracking/eye`
  messages; the Home page and the lite monitor say when an avatar uses it. The avatar name
  now comes from VRChat's new `/avatar/name`, and the fallback lookup in the OSC config
  JSON no longer resets every parameter a second time on each avatar load.
- **Lost parameter values repair themselves.** VRCFT only sends a value when it changes,
  so a message VRChat drops left that parameter wrong until it happened to change again -
  an eyelid stuck half-closed after a blink, or an expression's sign bit wrong for
  seconds. A value that has settled is now sent once more 100 ms later, and unchanged
  values are refreshed about once a second (spread across ticks, ~135 messages/s for a
  fully still face). VRChat ignores values it already has, so nothing extra is synced
  over the network. `/vrcft/settings/resendUnchanged` (bool) switches it at runtime.
  Measured A/B after the leak fix below, one minute each: without it two values stayed
  wrong for 275 and 355 ms; with it nothing stayed wrong for 100 ms or more.

### Fixed
- **Binary-encoded parameters multiplied with every avatar load, until the face
  stuttered and stuck.** Each load created a new set of bit parameters and dropped the
  old set without unsubscribing it, so every set ever made kept computing and sending
  each tick. Two resets run per avatar load: after a day of 54 loads, every bit of an
  OSCm/Binary avatar went out 108 times per tick, 16,000 messages a second. The send
  loop slowed from 97 to 57 ticks/s with gaps up to 90 ms, and VRChat failed to confirm
  about a quarter of the messages that were not duplicated - one sign bit stayed wrong
  for 8 s. Bits are now reused across loads and the ones a new avatar lacks are switched
  off; resets are serialized, since native OSC and the 518 router both trigger them.
  Measured live afterwards: 1,454 messages/s, and VRChat confirms 99.8% of changes.
  `tools/osc-capture` records and checks this without touching VRChat's port.
- **Starting iFacialMocap mid-session restarted tracking three times.** Bringing the
  phone up while VRCFT was already running (desktop mode) got its module started within
  ten seconds, then torn down and started twice more, with two tracking blackouts in
  between, before it settled about a minute later. Two causes: the hot-swap check
  compared the new module set against the last set it had *applied* rather than what was
  actually *running*, so a module the hot-plug check had just started counted as a change
  worth a full re-initialize; and that re-initialize read a stale probe cache that still
  said "port in use" from our own just-killed listener, so the phone looked absent until
  the next hot-plug tick. The hot-swap check now adopts the set when the matching modules
  already run, and every re-initialize looks at the hardware fresh after tearing down.
- **Tracking froze for up to 1.4 s at a time after a few hours.** The "[PerfWatch] Send
  loop stalled" warnings were garbage-collection pauses. The Output page rendered every
  log line it had ever received, each carrying six trigger behaviours, and the log never
  shrank: after seven hours that was 5,163 rendered lines and about 220,000 objects that
  every collection had to visit, so each one paused tracking for 90 ms to 1.4 s. The page
  now shows the newest 2,000 lines in a virtualized list with no behaviours, the full
  buffer stops at 10,000 lines (`latest.log` still keeps everything), and the page lets go
  of the log when you navigate away. Copy and Save no longer rebuild the text once per line.
- **A module that got no tracking slot "crashed" once a minute, all session.** With
  Virtual Desktop providing eyes and face, Project Babble was started, offered nothing,
  and left running without an update pump. Its process heard nothing for 60 s, assumed
  the app was gone and killed itself; the supervisor wrote a crash report and restarted
  it into the same situation - 586 times in one day. Such a module now goes on standby:
  its process is shut down, the Modules page says which module holds the slots, and it
  starts again by itself once a slot it can fill frees up.
- **A module coming up with eyes only marked the face slot free while another module
  owned it.** The next module to start would have been offered a slot that was taken.
  Slots are now only claimed at init, and only released when their owner goes away.
- **Crash (access violation) with the VRChat Output page open.** A send-on-load parameter
  can be queued before its first value is written, and its native value memory was never
  initialized, so reading it followed a garbage string pointer. New messages now start
  with a valid empty value, and the Output tap reads numbers without touching the string.
- **Log flooded with "mDNS send ... failed (AddressNotAvailable)".** The mDNS responder
  kept one sender per address found at startup and used it forever. When an adapter lost
  its address, every query failed on it, about eight times a second - 81% of one
  session's log. A sender whose address is gone is now retired, and the senders follow
  network changes in both directions.

## [1.1.0] - 2026-08-01
Based on upstream VRCFaceTracking 5.4.5.

### Added
- **Neutral Override — panic switches for testing.** Two toggles on the Tracking Debug
  page (and on Tracking Mutations): hold the eyes and/or the face at a calm neutral pose
  while modules keep running underneath. Only the outgoing buffer is overridden, so the
  raw column on the debug page still shows live hardware and switching back is instant.
  Always active and state is persisted, so it can never silently do nothing.
- **Idle animation for held eyes.** Rather than a frozen stare, held eyes glance around
  and blink like an idle avatar: centre-biased fixations of 0.5–3 s, 100–180 ms
  smoothstepped saccades, a whisper of drift, and blinks every few seconds (occasionally
  a double). A checkbox switches back to the static pose.
- **Companion apps are discovered instead of hardcoded.** A running instance's own path
  is used when available, otherwise a bounded search of the usual folders; whatever is
  found is remembered. Unconfigured rows now show a **Set path…** button that opens the
  file picker directly.
- **Restart tracking** button on the Sources page — the manual override that tears
  everything down and re-runs the start decisions immediately.

### Fixed
- **Headset swaps could end with no tracking at all.** Five causes fixed together:
  the merged Dream Air eye module wasn't mapped to the BrokenEye source (it ships
  under the stock ETVR name); a stale probe cache restarted a dead iFacialMocap
  module forever; the module process's 60 s connection timeout shot modules that
  legitimately take longer to initialize; a busy stream port counted as source
  evidence no matter who owned it; and SteamVR's shutdown/restart cycle was ignored,
  so the app got force-killed on SteamVR exit and kept a dead OpenVR handle after
  SteamVR restarts. A **Restart tracking** button on the Sources page is the manual
  override for anything automatic switching ever gets wrong.
- **Companion-app discovery could pick the wrong program** (it matched
  "EyeTrackVR Firmware Flashing Tool"), and a startup race could leave a module unstarted
  until the supervisor's next sweep.
- **Abandoned socket receives during headset swaps** were written up as crash reports;
  they are now logged as the routine teardown they are.
- Dev builds run from a `bin` folder no longer claim the SteamVR auto-launch
  registration, which is how SteamVR kept resurrecting stale builds.

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

[1.1.0]: https://github.com/Blise518B/VRCFaceTracking-518/releases/tag/518-v1.1.0
[1.0.0]: https://github.com/Blise518B/VRCFaceTracking-518/releases/tag/518-v1.0.0
