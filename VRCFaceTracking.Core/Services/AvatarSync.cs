using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VRCFaceTracking.Core.Services;

/// <summary>What one attempt to load VRChat's current avatar came to.</summary>
public enum AvatarLoadOutcome
{
    /// <summary>Loaded and applied; the id is the avatar now in use.</summary>
    Loaded,

    /// <summary>VRChat still served another avatar than the one asked for. Nothing was applied.</summary>
    Stale,

    /// <summary>Nothing could be loaded (VRChat didn't answer, no config file for the id).</summary>
    Failed,
}

/// <param name="AvatarId">The avatar loaded (Loaded) or the one VRChat still served (Stale).</param>
public readonly record struct AvatarLoadResult(AvatarLoadOutcome Outcome, string? AvatarId);

/// <summary>
/// Keeps the loaded avatar in step with the one VRChat has on (FORK-NOTES.md, "Avatar changes").
///
/// VRChat says which avatar is on in two places: the <c>/avatar/change</c> OSC message it sends
/// when one loads, and the <c>/avatar/change</c> node of its OSCQuery tree. Upstream only
/// listened for the message, read the tree the moment it came, and ran every load at once.
/// Three things went wrong with that when avatars are switched quickly:
/// <list type="bullet">
/// <item>VRChat doesn't always send the message. On 2026-11-27 it sent none for one switch back
/// to a native face tracking avatar (VRCFaceTracking and OscGoesPurrr both missed it), and the
/// app stayed on the avatar before: no face tracking until the next switch. So the node is
/// read every <see cref="DefaultCheckEvery"/>, and an avatar VRChat has on that isn't the loaded
/// one gets loaded.</item>
/// <item>The message comes about half a second before VRChat has rebuilt the tree. A load waits
/// until no change came for <see cref="DefaultSettle"/> (as OscGoesPurrr does), which also
/// folds VRChat's doubled messages into one load. And a tree that still shows another avatar
/// than the message named is read again instead of applied.</item>
/// <item>Loads overlapped. Each one resets every parameter's mapping, and whichever finished
/// last won, older or not. Here one worker runs every load, one at a time.</item>
/// </list>
/// Request is called on the OSC receive loops: it never blocks and never throws. The worker runs
/// on the thread pool.
/// </summary>
public sealed class AvatarSync
{
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan DefaultMaxSettle = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultCheckEvery = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultStaleRetryDelay = TimeSpan.FromSeconds(1);
    public const int DefaultStaleRetries = 4;

    /// <summary>Longest wait between check-started loads after loads kept failing.</summary>
    private static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger;
    private readonly Func<string?, CancellationToken, Task<AvatarLoadResult>> _load;
    private readonly Func<CancellationToken, Task<string?>> _readVrchatAvatarId;
    private readonly long _settleMs, _maxSettleMs, _staleRetryDelayMs;
    private readonly TimeSpan _checkEvery;
    private readonly int _staleRetries;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private Task? _worker;

    // A load asked for and not started yet. Guarded by _gate.
    private readonly object _gate = new();
    private bool _pending;
    private string? _pendingId;
    private bool _pendingFromVrchat;
    private long _pendingFirstMs, _pendingDueMs;
    private long _requests;

    // Worker only
    private int _failures;
    private long _checkLoadAllowedAtMs;
    private bool _checkUnansweredLogged;
    private string? _loadedId;

    /// <param name="load">
    /// Loads VRChat's current avatar and applies it, unless VRChat still serves another one
    /// than the id given (then Stale, nothing applied). A null id means take whatever is on.
    /// </param>
    /// <param name="readVrchatAvatarId">
    /// The avatar VRChat has on, or null when that can't be known (no OSCQuery, no answer).
    /// </param>
    public AvatarSync(
        ILogger logger,
        Func<string?, CancellationToken, Task<AvatarLoadResult>> load,
        Func<CancellationToken, Task<string?>> readVrchatAvatarId,
        TimeSpan? settle = null,
        TimeSpan? maxSettle = null,
        TimeSpan? checkEvery = null,
        TimeSpan? staleRetryDelay = null,
        int staleRetries = DefaultStaleRetries)
    {
        _logger = logger;
        _load = load;
        _readVrchatAvatarId = readVrchatAvatarId;
        _settleMs = (long)(settle ?? DefaultSettle).TotalMilliseconds;
        _maxSettleMs = (long)(maxSettle ?? DefaultMaxSettle).TotalMilliseconds;
        _checkEvery = checkEvery ?? DefaultCheckEvery;
        _staleRetryDelayMs = (long)(staleRetryDelay ?? DefaultStaleRetryDelay).TotalMilliseconds;
        _staleRetries = staleRetries;
    }

    /// <summary>The avatar the last successful load applied, null before the first.</summary>
    public string? LoadedAvatarId => Volatile.Read(ref _loadedId);

    // Not Environment.TickCount64: that moves in 15.6 ms steps
    private static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>Starts the worker on the thread pool. A second call does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            // Task.Run, not a bare call: started from App.OnLaunched, an inline async loop would
            // keep resuming on the UI thread (see OscRouterClient.StartListening).
            _worker ??= Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public void Stop() => _cts.Cancel();

    /// <summary>
    /// VRChat said the avatar changed (<paramref name="avatarId"/> from the message, if any),
    /// or asked for our host info. The load starts once no further change came for the settle
    /// time; <paramref name="immediate"/> skips that wait (the first load after discovery).
    /// </summary>
    public void Request(string? avatarId, bool immediate = false)
    {
        try
        {
            lock (_gate)
            {
                Arm(avatarId, fromVrchat: true, immediate);
            }

            _wake.Release();
        }
        catch (Exception)
        {
            // Called on the OSC receive loops, which must not see an exception from here
        }
    }

    /// <summary>Caller holds _gate.</summary>
    private void Arm(string? avatarId, bool fromVrchat, bool immediate)
    {
        var now = NowMs;
        if (!_pending)
        {
            _pending = true;
            _pendingFirstMs = now;
            _pendingId = null;
            _pendingFromVrchat = false;
        }

        if (!string.IsNullOrEmpty(avatarId))
        {
            _pendingId = avatarId;
        }

        _pendingFromVrchat |= fromVrchat;
        _pendingDueMs = immediate ? now : now + _settleMs;
        _requests++;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                long? dueIn;
                lock (_gate)
                {
                    dueIn = _pending
                        ? Math.Min(_pendingDueMs, _pendingFirstMs + _maxSettleMs) - NowMs
                        : null;
                }

                if (dueIn is null)
                {
                    // Nothing asked for: wait for a request, or look at VRChat once the interval is up
                    if (!await _wake.WaitAsync(_checkEvery, ct))
                    {
                        await CheckAsync(ct);
                    }

                    continue;
                }

                if (dueIn > 0)
                {
                    // Another request in the meantime only moves the due time: work it out again
                    await _wake.WaitAsync(TimeSpan.FromMilliseconds(dueIn.Value), ct);
                    continue;
                }

                string? id;
                bool fromVrchat;
                lock (_gate)
                {
                    id = _pendingId;
                    fromVrchat = _pendingFromVrchat;
                    _pending = false;
                    while (_wake.CurrentCount > 0 && _wake.Wait(0))
                    {
                    }
                }

                await LoadAsync(id, fromVrchat, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // The delegates catch their own errors; this is the last line. A dead worker
                // would mean no avatar change is ever loaded again.
                _logger.LogWarning("Avatar sync: {message}", e.Message);
                try
                {
                    await Task.Delay(1000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task LoadAsync(string? askedId, bool fromVrchat, CancellationToken ct)
    {
        var expectedId = askedId;
        var startedMs = NowMs;
        long requestsBefore;
        lock (_gate)
        {
            requestsBefore = _requests;
        }

        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0 && NewerRequestWaiting(requestsBefore))
            {
                return; // A newer change came during the retry wait; that one is the avatar to load
            }

            var result = await _load(expectedId, ct);
            if (result.Outcome == AvatarLoadOutcome.Stale && expectedId == null)
            {
                // Nothing was asked for, so nothing can be stale; a loader saying so is a bug
                result = result with { Outcome = AvatarLoadOutcome.Failed };
            }

            switch (result.Outcome)
            {
                case AvatarLoadOutcome.Loaded:
                {
                    var previous = _loadedId;
                    Volatile.Write(ref _loadedId, result.AvatarId);
                    _failures = 0;
                    _checkLoadAllowedAtMs = 0;
                    if (!fromVrchat && previous != null && previous != result.AvatarId)
                    {
                        _logger.LogInformation(
                            "VRChat switched to avatar {id} without an /avatar/change message (loaded was {previous}). Loaded it now.",
                            result.AvatarId, previous);
                    }

                    return;
                }

                case AvatarLoadOutcome.Failed:
                    _failures++;
                    var backoff = Math.Min(MaxFailureBackoff.TotalMilliseconds,
                        _checkEvery.TotalMilliseconds * Math.Pow(2, Math.Min(_failures - 1, 10)));
                    _checkLoadAllowedAtMs = NowMs + (long)backoff;
                    return;

                case AvatarLoadOutcome.Stale:
                    if (NewerRequestWaiting(requestsBefore))
                    {
                        return; // A newer change is waiting; that one is the avatar to load
                    }

                    if (attempt >= _staleRetries)
                    {
                        // What VRChat serves is what is on; the change it named may never have finished
                        _logger.LogInformation(
                            "VRChat still shows avatar {served} {seconds:0.0} s after a change to {asked}; using the one it shows.",
                            result.AvatarId, (NowMs - startedMs) / 1000.0, askedId);
                        expectedId = null;
                        continue;
                    }

                    _logger.LogDebug(
                        "VRChat's avatar tree still shows {served}, not {asked}; reading it again in {delay} ms.",
                        result.AvatarId, askedId, _staleRetryDelayMs);
                    await Task.Delay(TimeSpan.FromMilliseconds(_staleRetryDelayMs), ct);
                    break;
            }
        }
    }

    private bool NewerRequestWaiting(long requestsBefore)
    {
        lock (_gate)
        {
            return _pending && _requests != requestsBefore;
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (NowMs < _checkLoadAllowedAtMs)
        {
            return;
        }

        var current = await _readVrchatAvatarId(ct);
        if (string.IsNullOrEmpty(current))
        {
            if (!_checkUnansweredLogged && _loadedId != null)
            {
                _checkUnansweredLogged = true;
                _logger.LogDebug("Avatar check: VRChat's OSCQuery didn't say which avatar is on (closed or busy); trying again every {seconds} s.",
                    _checkEvery.TotalSeconds);
            }

            return;
        }

        _checkUnansweredLogged = false;
        if (string.Equals(current, _loadedId, StringComparison.Ordinal))
        {
            return;
        }

        lock (_gate)
        {
            if (_pending)
            {
                return; // A load is already on its way
            }

            // Through the settle wait too: VRChat updates the node before it has rebuilt the tree,
            // and the /avatar/change message usually follows within it (then it isn't a miss).
            Arm(current, fromVrchat: false, immediate: false);
        }

        _logger.LogDebug("Avatar check: VRChat has {current} on, the loaded one is {loaded}; loading it unless /avatar/change comes first.",
            current, _loadedId ?? "none");
    }
}
