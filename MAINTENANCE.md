# Maintenance notes & backlog

Living list of known debt in this fork, roughly in priority order. Anything
finished moves to **Done** at the bottom with the commit that did it, so the
history of *why* a thing looks the way it does stays with the code.

Priority = (Impact + Risk) x (6 - Effort), each scored 1-5.

---

## Invariants — read before touching the tracking path

These are load-bearing. Breaking one produces a symptom that looks like
"tracking is janky" and takes hours to trace back.

1. **Nothing on the 100Hz path may block.** The send loop is
   `ParameterSenderService.ExecuteAsync` -> `UnifiedTracking.UpdateData` ->
   mutations -> parameters -> `OscSendService`. No file I/O, no process
   enumeration, no sockets with timeouts, no `Task.Run` hops, no LINQ
   allocations. If you need a source name or a module name there, cache it
   (`TrackingSourceDetector.ScanCached`, not `Scan`).
2. **Handlers on the IPC receive path may not throw.** An exception there used
   to fault the receive task and silently stop *all* tracking. There are now two
   catch-alls (`HandleModulePacket`, `UdpFullDuplex.ListenAsync`); keep both.
3. **`AvailableSandboxModules` is only touched under `lock`.** The IPC thread
   adds to it on handshake while the initialize worker tears it down.
4. **Unhandled exceptions on a plain `Thread` kill the process.** Every
   long-lived thread body here is wrapped. Keep it that way.
5. **Mutation fields must be instance fields to persist.** Newtonsoft ignores
   statics, so a `static` tuning value silently never saves. (Upstream's
   `Filter` mutation still has this bug.)
6. **`ReplyUpdatePacket`'s wire layout is shared by two processes.** Layout is
   `[4B magic][4B type][4B size][16 header floats][EXPRESSION_COUNT floats]`.
   Both sides ship together, but changing the order breaks tracking silently.
   The native face tracking blob is the other fixed layout, and VRChat owns that one:
   `/tracking/face/v1`, exactly 102 bytes (VRChat drops any other size), bytes 14-101 in
   `UnifiedExpressions` enum order. `FaceTrackingBlob` relies on that order, so the enum
   never gets an entry inserted or moved.
7. **No unbounded UI over data that lives all session.** Every live XAML object is
   visited by every garbage collection, and those collections pause the send loop
   too. The Output page once held 5,163 log lines with six behaviours each and made
   every GC a 90 ms - 1.4 s tracking freeze. Cap what a page shows, virtualize the
   list, and detach from static collections on `Unloaded` (a bound `ItemsSource` on a
   static collection keeps the whole page alive).
8. **A module process must never be left without an update pump.** It kills itself
   after 60 s of silence and the supervisor reads that as a crash. A module that
   initializes nothing goes on standby (`UnifiedLibManager.PutOnStandby`).
9. **A parameter that is dropped must be switched off, not just forgotten.** A
   relevant `BaseParam` is subscribed to the static `OnUnifiedDataUpdated`, which keeps
   it alive and sending every tick. `BinaryBaseParameter` once recreated its bits on
   every reset and dropped the old ones, and after a day each bit went out 108 times a
   tick. Reuse child parameters, or call `Deactivate()` on the ones you let go.
10. **Values are sent on change, so a lost datagram must be repaired by a re-send.**
   VRChat does drop OSC messages. `ParameterSenderService.ResendUnchanged` re-sends
   settled values (100 ms after settling, then about once a second); don't remove it
   to save traffic without measuring confirmation rates first.

---

## Backlog

### 1. No automated tests at all — P: 40 (Impact 4, Risk 4, Effort 2)
The fork has zero test coverage; every change is validated by putting on a
headset. The cheapest high-value coverage, in order:
- `ReplyUpdatePacket` encode -> decode round-trip (this serialization was
  rewritten by hand and is only verified by "tracking still works").
- `TrackingCompatibility` Quest Pro / Vive transforms against known input
  vectors — pure math, no hardware.
- `TrackingSourceDetector.Decide()` truth table (Auto/Always/Never x
  detected/not x strong/weak) — pure logic, no probes.
- `ResponseCurve.Evaluate` boundary behaviour.
A `VRCFaceTracking.Tests` xunit project against Core would cover all of it,
runs in CI-less local `dotnet test`, and needs no hardware.

### 2. Global mutable statics make behaviour order-dependent — P: 21 (I 3, R 4, E 3)
`UnifiedTracking.Data`, `UnifiedTracking.OnUnifiedDataUpdated`,
`ParameterSenderService.SendQueue`, `UnifiedLibManager.EyeStatus/ExpressionStatus`
and the static `_sandboxServer` are process-wide singletons. This is inherited
from upstream and is why item 1 is hard. Not worth a big-bang refactor; the
pragmatic path is to introduce interfaces where a test needs a seam and leave
the rest alone.

### 3. Dead receive machinery in `UdpFullDuplex` — P: 20 (I 2, R 2, E 1)
`ReceiveThread()`, `ReceiveCallback()`, `ReceivePackets()` and
`OnReceiveShouldBeQueued` are a `BeginReceive`-based path superseded by
`ListenAsync`. `ReceivePackets()` is still *called* from `ModuleProcessMain`,
but only behind `_shouldCallReceive`, which is only set from the callback that
path would install — so it is unreachable in practice. Confusing rather than
harmful; delete it and the `_shouldCallReceive` plumbing.

### 4. Two overlapping module registries — P: 18 (I 3, R 3, E 3)
`_moduleThreads` and `AvailableSandboxModules` hold overlapping state and are
both torn down (the second pass no-ops because the process has exited). One
list with a clear lifecycle would remove a whole class of "which one is
authoritative?" questions.

### 5. `Initialize()` has no re-entrancy guard — P: 16 (I 2, R 2, E 1)
Nothing stops two initialize workers running at once if `Initialize()` is
called again while one is in flight. `ModuleHotSwapService` guards its own
calls with `_reinitInFlight`, but that protects only that caller. Guard it in
`UnifiedLibManager` itself.

### 6. `ModuleProcessMain` polls with `Thread.Sleep(1)` — P: 15 (I 1, R 2, E 1)
1000 wakeups/sec per module process purely to check a queue. Correct but
wasteful on a laptop; a `ManualResetEventSlim` would idle properly.

### 7. `OscMessage` owns native memory with no `Dispose` — P: 12 (I 2, R 2, E 3)
The `(address, type)` constructor `AllocHGlobal`s and only the *parse*
constructor's pointer is freed in the finalizer, so those allocations are never
released. In practice this is a fixed ~600-message allocation made once at
startup and reused forever, so it does not grow — but it is a leak by
construction and would bite if parameters were ever rebuilt per avatar.
Make it `IDisposable` and free `_meta.Value`.

### 8. Three empty `catch {}` blocks — P: 12 (I 2, R 2, E 1)
Each should either log at Debug or carry a comment saying why silence is
correct. Silent swallowing is how the IPC bug stayed invisible for so long.

### 9. `Encode`/`Decode` still route through `Task.Run` in `OscMessage` — P: 9
`OscMessage.Encode` is now unused by the hot path (the send service calls the
native function directly) but still exists with its old signature. Retire it or
make it synchronous so nobody reintroduces the hop.

---

## Done

- **Sentry reporting to upstream's project removed** (`e43082a`). The fork
  shipped upstream's hardcoded DSN in `App.xaml.cs` (x2) and
  `appsettings.json`, so every crash — including ones in fork-only code — was
  filed into the upstream maintainer's error tracker, and every user's crash
  telemetry went to a third party they never agreed to. `CrashReportService`
  writes better local reports anyway.
- **IPC packet handler could kill all tracking silently** (`bdf17e1`). Indexing
  the module list with a `-1` "not found" sentinel threw into the UDP receive
  loop, faulting it permanently. Also fixed: teardown iterating a live
  collection (process-killing `InvalidOperationException` on the initialize
  worker) and a `NullReferenceException` in the teardown failure path. The
  220-line lambda inside `Initialize()` became named handler methods.
- **Periodic ~430ms tracking freeze** (`6ff5c47`). Curve-profile auto-switch ran
  a blocking source scan on the tracking thread every 5s. Added
  `ScanCached()`, moved the sweep out of the cache lock, and streamlined the
  100Hz path (no threadpool hops, reused buffers, no per-frame LINQ). Also
  fixed a leaked 100Hz UI handler, 100ms added OSC receive latency, and an
  unsynchronized queue in the module process.
- **Loop ran at 64Hz instead of 100Hz** (`5c38b5f`). Windows 11 ignores
  `timeBeginPeriod` for minimized/occluded windows — i.e. the whole VR session.
  Opted out via `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION`.
