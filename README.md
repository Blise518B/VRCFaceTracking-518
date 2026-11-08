# VRCFaceTracking 518 Edition

An unofficial fork of [VRCFaceTracking](https://github.com/benaclejames/VRCFaceTracking) that keeps your face tracking running and lets you tune it per expression: crashed modules and the app restart themselves, each expression gets its own range and response curve, and only the modules whose hardware is actually streaming are started.

<p align="center">
  <a href="https://github.com/Blise518B/VRCFaceTracking-518/releases/latest/download/VRCFaceTracking-518-Windows.exe">
    <img src="docs/images/download-badge.svg" alt="Download for Windows: VRCFaceTracking-518-Windows.exe, latest release">
  </a><br>
  <sub><a href="https://github.com/Blise518B/VRCFaceTracking-518/releases/latest/download/VRCFaceTracking-518-Windows.zip">Portable zip</a> · every version on the <a href="https://github.com/Blise518B/VRCFaceTracking-518/releases">Releases</a> page</sub>
</p>

> **Not the official VRCFaceTracking.** This fork is not affiliated with or supported by the VRCFaceTracking developers. Please report problems with it [here](https://github.com/Blise518B/VRCFaceTracking-518/issues), never to the VRCFaceTracking project, its GitHub or its Discord.

## What it adds

**Keeps running**
- **Crash auto-restart.** A tracking module that crashes is started again, and if VRCFaceTracking itself crashes, a small watchdog relaunches it. Both are capped so a crash loop can't run forever. Settings → Crash Auto-Restart shows what restarted and when.
- **Crash reports.** Every crash writes a readable report to `Documents\VRCFaceTracking\crashes\`: what died, how, and whether VRChat, SteamVR or SRanipal were running at the time.
- **Updates itself.** When a new release is out, a banner says so; one click downloads it, checks it and restarts into it. Nothing installs without that click, so tracking never drops out mid-session.

**Tuning**
- **Range Calibration.** Per expression: the raw range that maps to full movement, and a sensitivity curve. If your tracker only ever reports JawOpen up to 0.6, set the ceiling to 0.6 and your avatar's jaw opens all the way. Capture buttons record your neutral face and your maximums for you.
- **Curve Editor.** Draw the full response curve for 100 channels, including eye openness, gaze and head rotation, which no other mutation can reach. Two editors side by side with a live dot where your face is right now, so you can match one eye to the other.
- **Tracking Compatibility.** Avatars made for Quest Pro or Vive behave right on other hardware.

**Setup and diagnosis**
- **Sources page.** Works out which tracking sources are live (Virtual Desktop, Steam Link, ALVR, Quest Link, SRanipal, EyeTrackVR, Project Babble, Varjo, Pimax, Tobii) and starts only the matching modules, each decision with a readable reason. Switching headsets swaps modules by itself.
- **Launch buttons.** Store a launch command per source and start BrokenEye, EyeTrackVR, Babble and friends from inside the app, or automatically.
- **Tracking Debug and VRChat Output pages.** Every expression live, before and after your mutations, and every OSC message actually sent to VRChat.
- **VRChat native face tracking** (open beta): avatars uploaded with "Use All Tracking Keys" get the whole face in one message, with no synced parameters spent on it.
- **Lite Console.** Switch the running session to a tiny console monitor and back. SteamVR can start it instead of the full app.

The full details, with the reasoning behind each, are in [FORK-NOTES.md](FORK-NOTES.md); what changed in each version is in [CHANGELOG.md](CHANGELOG.md).

## Getting started

1. Download `VRCFaceTracking-518-Windows.exe` with the button above and run it. It's one file: it unpacks the app to `%LOCALAPPDATA%\VRCFaceTracking-518` and starts it.
2. Windows may say it protected your PC, because the build isn't code-signed: click **More info**, then **Run anyway**.
3. Install the modules for your hardware from the **Module Registry** page, as in stock VRCFaceTracking, and turn on OSC in VRChat (Action Menu → Options → OSC → Enabled).

Already using VRCFaceTracking? Your settings and modules live in `%APPDATA%\VRCFaceTracking`, not in the app folder, so this build picks them up as they are. It runs instead of a normal install, not alongside it at the same time: starting one closes the other.

**Portable zip instead:** unzip [VRCFaceTracking-518-Windows.zip](https://github.com/Blise518B/VRCFaceTracking-518/releases/latest/download/VRCFaceTracking-518-Windows.zip) somewhere you can write to (Documents is fine, Program Files is not) and run `VRCFaceTracking.exe`.

Setting up an avatar for face tracking works exactly as in the official app: see the [VRCFaceTracking docs](https://docs.vrcft.io/docs/intro/getting-started) and their [parameter list](https://docs.vrcft.io/docs/tutorial-avatars/tutorial-avatars-extras/parameters/).

## Updates

The app asks GitHub once per start whether there is a newer release. If there is, a banner appears above every page: **Update now** downloads the portable zip, checks it against the checksum GitHub lists, closes the app, copies the new version over the old one and starts it again. Your settings and modules are untouched. **Skip this version** keeps it quiet until the next one.

Settings → Updates has the same button, a **Check now** button and the switch for the startup check. A copy in a folder Windows won't let it write to (Program Files) opens the download page instead. The update helper logs what it did to `%APPDATA%\VRCFaceTracking\update.log`.

## Build from source

Needs the .NET 10 SDK on Windows.

```
dotnet build VRCFaceTracking.sln -c Release -p:Platform=x64 -p:WindowsPackageType=None
```

`build.bat` does the same; `run.bat` starts the result, `update.bat` stops the running app, rebuilds and starts it again. `package.ps1` makes the two release files in `dist\`. A build from source never replaces itself through the updater.

GitHub Actions builds every push (the files are attached to the workflow run) and publishes a release when a `518-vX.Y.Z` tag is pushed. How to cut one is at the top of [CHANGELOG.md](CHANGELOG.md).

## Credits and license

VRCFaceTracking is made by [benaclejames](https://github.com/benaclejames), [regzo2](https://github.com/regzo2) and its [contributors](https://github.com/benaclejames/VRCFaceTracking/graphs/contributors). This fork builds on their work under the [Apache License 2.0](LICENSE), as does every change it adds. The changes are listed in [FORK-NOTES.md](FORK-NOTES.md) and [CHANGELOG.md](CHANGELOG.md).

This build sends no telemetry; see [PRIVACY.md](PRIVACY.md) for the few network requests it makes.
