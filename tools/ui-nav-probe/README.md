# ui-nav-probe

Times page switches in a **running** VRCFaceTracking from the outside, to tell a page that
does too much work apart from a window that is starved of CPU. It never starts, stops or
restarts the app and never moves the mouse: it clicks nav items through UI Automation.

```
dotnet run --project tools/ui-nav-probe -c Release -- --rounds 5 --pages "Module Registry,VRChat Output" --base Sources
```

While it runs it pings the window with `WM_NULL` every ~15 ms (a ping only returns when the
UI thread pumps messages, so its latency is how long that thread was blocked) and samples the
UI thread's own CPU time and the process priority class with every ping. Per switch:

| column | meaning |
| --- | --- |
| `ready` | click until the UI thread answered four pings in a row promptly again |
| `invoke` | how long the UI Automation click itself took |
| `uiCpu` | CPU the UI thread burned in that span |
| `wait` | `ready` minus `uiCpu`: time the UI thread was not running. Large `wait` with small `uiCpu` = starved (low priority, busy cores) or blocked (GC, a lock) |
| `maxBlk` | longest single unanswered ping |
| `prio` | priority class seen during the switch (BelowNormal = Process Lasso ProBalance) |
| `dwell ui%` | how busy the UI thread stays while the page is just showing |

`--out file.csv` writes every row. The app's own `UiWatch` log lines say the same about
freezes that happen in normal use.

Measured 2026-11-26 (VRChat with a native face tracking avatar, OSC Router 518 running), before
the router receive loop moved off the UI thread: switches took 10-600 ms, but the UI thread was
16-27 % busy while idle, woke ~3,900 times a second (nearly all kernel time) and the process was
lowered to BelowNormal by ProBalance for 5-17 s about once a minute.

After the fix (same day, same avatar, same CPU 0-15 lock): the UI thread is 1.7 % busy while
idle and wakes ~80 times a second, the whole process uses 10 % of a core instead of 40 %, and
the UI thread stays 2-5 % busy on every page during the probe instead of 16-27 %.
