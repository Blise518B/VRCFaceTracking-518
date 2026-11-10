# mdns-endpoint-check

Offline check that a second VRChat mDNS record only replaces the OSCQuery endpoint in use once
that endpoint stops answering (FORK-NOTES.md, section 11, "Two VRChat records"). It needs no
VRChat and puts nothing on the network: the records go straight into
`MulticastDnsService.ResolveVrChatClient` (serialized and parsed again, like a received
packet), and fake OSCQuery servers on 127.0.0.1 stand in for VRChat.

```
dotnet run --project tools/mdns-endpoint-check
```

It refuses to run while VRChat or ChilloutVR is running, because their real records would
reach the service under test. Exit code 0 when every check passes. The steps:

| # | situation | expected |
| --- | --- | --- |
| 1 | first record | taken before `ResolveVrChatClient` returns, `OnVrcClientDiscovered` once, no HTTP |
| 2 | same record again | nothing changes, the event fires again (as upstream), no HTTP |
| 3 | a dead record next to the live one | stays; one `GET /?HOST_INFO` to the live one; logged once |
| 4 | the dead record 50 more times within 5 s | stays, no new request, no new log line |
| 5 | a second VRChat that answers too | the first one stays |
| 6 | the one in use closes its port, a new one answers | moves (about 2 s: Windows' refusal time) |
| 7 | the one in use accepts but never replies (hung) | the call returns at once; moves after the 2 s timeout |
| 8 | the one in use is gone and the other record doesn't answer | stays |
| 9 | that record's server comes up later | no re-check within 5 s; the next announcement after that moves |

Reference run (2026-11-10): 26 passed, 0 failed, 18 s. `ResolveVrChatClient` returned in
0-7 ms every time; each move took 2,022-2,024 ms.
