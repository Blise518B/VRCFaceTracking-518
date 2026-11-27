# avatar-sync-check

Offline check of how avatar changes are loaded: `AvatarSync` plus `OscQueryConfigParser`
(FORK-NOTES.md, "Avatar changes").

```
dotnet run --project tools/avatar-sync-check
```

Exit code 0 when every check passes. Takes about 30 s. It uses its own loopback ports only,
binds no mDNS socket and reads no VRChat files, so it is safe to run while VRChat and
VRCFaceTracking are open.

| Part | What runs | What it shows |
| --- | --- | --- |
| A | `AvatarSync` against a scripted VRChat, with short timings | a change waits for the settle time and loads once; doubled or rapid messages fold into one load of the last avatar; an avatar VRChat switched to without a message is found by the check and loaded (and logged as such), but not when the message follows within the settle time; a tree that still shows the old avatar is read again and never applied, and after the retries the avatar VRChat shows is used; loads never overlap and the newest change wins; nothing is loaded while VRChat doesn't answer; failing loads back off but a message still loads at once; a stream of changes that never stops still loads within the max settle time; `Request` never throws |
| B | the real `OscQueryConfigParser` against a fake VRChat OSCQuery server | VRChat's `/avatar/change` answer is read (2 s timeout when VRChat hangs or is closed); a tree is mapped for the avatar asked for; a tree that shows another avatar resets no parameter (Stale); a native face tracking avatar maps none |
| C | both together | the 2026-11-27 case: VRChat switches back to a native face tracking avatar and sends no `/avatar/change`, and the avatar is loaded anyway; a message that runs ahead of the tree loads once the tree catches up, without applying the old tree |

## Against the running VRChat

```
dotnet run --project tools/avatar-sync-check -- --live 58654
```

The port is VRChat's OSCQuery port; VRCFaceTracking's log names it
(`OSCQuery detected at 127.0.0.1:PORT`). This only reads `/avatar/change` and `/avatar`, and
the parameter mapping it changes is its own process's. On 2026-11-27 it read a native face tracking
avatar's id in 152 ms, loaded its tree as native face tracking in 130 ms, and returned Stale when asked for another
avatar.

## Not covered

- `OscQueryService`'s wiring (the message handler, the first load after discovery, the host
  info hook) is compile-checked only; it is a few lines that hand over to `AvatarSync`.
- Whether VRChat always rebuilds its tree within the 1.5 s settle time. If it doesn't, the
  stale re-reads cover a tree that still shows the old avatar, but not one that already shows
  the new avatar's id with half of its parameters.
- The plain-OSC path (VRChat never found over mDNS), which reads VRChat's OSC config file by
  the id in the message. It goes through the same worker but has nothing to check against.
