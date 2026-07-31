using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Params.Data.Mutation;

namespace VRCFaceTracking.Core.Params.Data;

/// <summary>
/// Container of all functions and structures retaining to mutating the incoming Expression Data to be usable for output parameters.
/// </summary>
public partial class UnifiedTrackingMutator : ObservableObject
{
    [ObservableProperty]
    [property: SavedSetting("MutatorEnabled", true)]
    private bool _enabled;

    private readonly ILogger<UnifiedTrackingMutator> _logger;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IDispatcherService _dispatcherService;
    private readonly object _mutationsLock = new();
    private UnifiedTrackingData _inputBuffer;
    public ObservableCollection<TrackingMutation> _mutations = new();

    public UnifiedTrackingMutator(ILogger<UnifiedTrackingMutator> logger, ILocalSettingsService localSettingsService, IDispatcherService dispatcherService)
    {
        UnifiedTracking.Mutator = this;
        _logger = logger;
        _localSettingsService = localSettingsService;
        _dispatcherService = dispatcherService;

        Enabled = false;
        _inputBuffer = new UnifiedTrackingData();
    }

    /// <summary>
    /// Takes in the latest base expression Weight data from modules and mutates into the Weight data for output parameters.
    /// </summary>
    /// <returns> Mutated Expression Data. </returns>
    public UnifiedTrackingData MutateData(UnifiedTrackingData input)
    {
        if (!Enabled)
        {
            return input;
        }

        _inputBuffer.CopyPropertiesOf(input);

        lock (_mutationsLock)
        {
            // Indexed loop, no LINQ: this runs 100 times a second on the tracking thread,
            // and Where() allocates an enumerator per call.
            for (var i = 0; i < _mutations.Count; i++)
            {
                var mutation = _mutations[i];
                if (mutation is { IsActive: true })
                {
                    mutation.MutateData(ref _inputBuffer);
                }
            }
        }

        return _inputBuffer;
    }

    public async Task Save()
    {
        _logger.LogDebug("Saving mutation configuration...");
        await _localSettingsService.Save(this);

        // This is cringe but since the collection can change mid-save, we'll just make a quick copy of em
        List<TrackingMutation> mutationsToSave;
        lock (_mutationsLock)
        {
            mutationsToSave = _mutations.ToList();
        }

        foreach (var mutation in mutationsToSave)
        {
            _logger.LogInformation($"Saving {mutation.Name} data.");
            await _localSettingsService.SaveSettingAsync(mutation.Name, mutation, true);
        }

        _logger.LogDebug("Mutation data saved.");
    }

    public void Initialize()
    {
        // Try to load config and propogate data into Unified if they exist.
        _logger.LogDebug("Initializing mutations...");
        lock (_mutationsLock)
        {
            foreach (var mutation in _mutations)
            {
                if (mutation == null)
                {
                    continue;
                }

                try
                {
                    _logger.LogInformation($"Initializing {mutation.Name}");
                    mutation.Initialize(UnifiedTracking.Data);
                }
                catch (Exception ex)
                {
                    // One bad mutation must not stop the rest from initializing
                    _logger.LogError($"Initializing {mutation.Name} failed: {ex.Message}");
                }
            }
        }

        _logger.LogDebug("Mutations initialized successfully.");
    }
    
    /// <summary>
    /// Loads a mutation's saved settings and prepares it for use. Returns null if the mutation
    /// can't be prepared at all, in which case it's left out rather than published broken.
    /// </summary>
    private async Task<TrackingMutation?> CreateMutationAsync(TrackingMutation mutation)
    {
        try
        {
            _logger.LogInformation($"Loading {mutation.Name}");

            Type mutationType = mutation.GetType();

            MethodInfo readSettingMethod = typeof(ILocalSettingsService)
                .GetMethod("ReadSettingAsync")
                .MakeGenericMethod(mutationType);

            var task = (Task)readSettingMethod.Invoke(_localSettingsService, new object[] { mutation.Name, mutation, true });
            await task.ConfigureAwait(false);

            var typedMutation = task.GetType().GetProperty("Result")?.GetValue(task);

            // Saved data can deserialize to null (missing or corrupt setting). Keep the fresh
            // instance in that case instead of carrying a null forward.
            if (typedMutation is TrackingMutation loaded)
            {
                mutation = loaded;
            }
            else
            {
                _logger.LogWarning($"Saved data for {mutation.Name} was unusable. Using defaults.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Reading saved data for {mutation.Name} failed, using defaults. {ex.Message}");
        }

        try
        {
            mutation.Logger = _logger;
            mutation.LocalSettingsService = _localSettingsService;
            mutation.CreateProperties();
            return mutation;
        }
        catch (Exception ex)
        {
            // Previously this ran in a catch block that could throw the very same exception
            // again, which in an async void method took the whole app down.
            _logger.LogError($"Could not prepare mutation {mutation?.Name}. Skipping it. {ex.Message}");
            return null;
        }
    }

    public async void Load()
    {
        _logger.LogDebug("Loading mutation data...");

        try
        {
            var mutations = TrackingMutation.GetImplementingMutations(true);
            await _localSettingsService.Load(this);

            // Build every mutation to completion *before* publishing any of them.
            //
            // This used to call an async void CreateMutation inside the lock: each call
            // returned at its first await, so Load() released the lock and ran Initialize()
            // while the continuations were still doing _mutations.Add(...) on threadpool
            // threads with no lock held. Initialize() then enumerated a collection being
            // mutated underneath it, which threw "Collection was modified" - or saw a
            // half-published entry and threw NullReferenceException - and killed the process.
            var prepared = new List<TrackingMutation>();
            foreach (var mutation in mutations)
            {
                var ready = await CreateMutationAsync(mutation).ConfigureAwait(false);
                if (ready != null)
                {
                    prepared.Add(ready);
                }
            }

            // Publish and initialize on the UI thread: _mutations is an ObservableCollection
            // bound to the mutations page, so adding to it from a background thread is its own
            // crash once that page has been opened.
            _dispatcherService.Run(() =>
            {
                try
                {
                    lock (_mutationsLock)
                    {
                        foreach (var mutation in prepared)
                        {
                            _mutations.Add(mutation);
                        }
                    }

                    _logger.LogDebug("Mutation data loaded.");
                    Initialize();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Publishing mutations failed: {ex}");
                }
            });
        }
        catch (Exception ex)
        {
            // Load is async void (it's fire-and-forget from startup), so anything escaping
            // here would be an unhandled exception on a threadpool thread.
            _logger.LogError($"Loading mutations failed: {ex}");
        }
    }
}