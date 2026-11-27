# whats-new-check

Offline check of when the "What's new" window shows after an update, and what it shows
(FORK-NOTES.md, "What's new").

```
dotnet run --project tools/whats-new-check
```

Exit code 0 when every check passes; takes a second and touches nothing outside its process.

1. **The notes this build carries** (`WhatsNew.Releases`): every version parses, newest at the
   top and none twice, no release without changes, every "Take me there" target exists, and a
   change that needs its page names one. It also prints which version the newest notes are for,
   so a release cut without notes for itself shows up.
2. **The rule** (`WhatsNew.OnLaunch`) against notes for 1.4.0, 1.5.0 and 1.6.0: a first install
   shows nothing but remembers its version; its first update shows only that update; an install
   from before the window existed gets the notes this build has; nothing shows twice; an update
   that skipped versions shows each of them, newest first; notes newer than the build never show;
   going back to an older build never lowers the marker; an unreadable marker counts as none.

Not covered: the window itself (`VRCFaceTracking/Views/WhatsNewDialog.cs`) and whether
`LocalSettingsService.HadSettingsFile` is read before anything saves settings. Both are checked
by starting the app.
