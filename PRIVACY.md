# Privacy: VRCFaceTracking 518 Edition

This build collects nothing and sends no telemetry. Upstream VRCFaceTracking reports errors to Sentry; this fork removed that, so no crash, error or usage data leaves your PC. Crash reports are written to `Documents\VRCFaceTracking\crashes\` and stay there unless you send one to someone yourself.

## What it connects to

- **GitHub, for updates.** Once per start the app asks `api.github.com` for this fork's newest release (Settings → Updates switches this off). When you click Update, it downloads the release file from GitHub. These requests carry the app's version in the user agent and nothing else.
- **GitHub, for the credits.** Settings → Credits loads the list of upstream VRCFaceTracking contributors from `api.github.com`.
- **The module registry.** The Module Registry page and module updates use `registry.vrcft.io`, run by the VRCFaceTracking project, exactly as in the official app.
- **VRChat, on your PC or network.** OSC, OSCQuery and mDNS traffic to VRChat, as in the official app.

Tracking modules are separate programs written by their own authors. What they connect to is up to them.

## The official app

The official VRCFaceTracking's privacy policy is [in its repository](https://github.com/benaclejames/VRCFaceTracking/blob/master/PRIVACY.md). It describes the official app, not this build.

Questions about this build: [open an issue](https://github.com/Blise518B/VRCFaceTracking-518/issues).
