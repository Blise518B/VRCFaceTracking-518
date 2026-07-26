using System.Reflection;
using Newtonsoft.Json;
using VRCFaceTracking.Core;
using VRCFaceTracking.Core.Contracts.Services;

namespace VRCFaceTracking.Lite;

/// <summary>
/// There is no UI thread here, so actions run inline. Core uses this to marshal collection
/// updates onto the UI; in the console build the caller's thread is as good as it gets.
/// </summary>
public class ImmediateDispatcherService : IDispatcherService
{
    public void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // A failed UI update must never take the tracking loop down
        }
    }
}

/// <summary>Plain JSON file storage, replacing the WinUI build's Windows.Storage version.</summary>
public class PlainFileService : IFileService
{
    public T Read<T>(string folderPath, string fileName)
    {
        var path = Path.Combine(folderPath, fileName);
        if (!File.Exists(path))
        {
            return default!;
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(File.ReadAllText(path))!;
        }
        catch (Exception)
        {
            return default!;
        }
    }

    public Task Save<T>(string folderPath, string fileName, T content)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            File.WriteAllText(Path.Combine(folderPath, fileName), JsonConvert.SerializeObject(content));
        }
        catch (Exception)
        {
        }
        return Task.CompletedTask;
    }

    public void Delete(string folderPath, string fileName)
    {
        try
        {
            var path = Path.Combine(folderPath, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// Settings store pointed at the same LocalSettings.json the full app uses, in the same
/// key -> serialized-value shape. That's deliberate: curve profiles, mutations and OSC ports
/// tuned in the full app apply to this build without any syncing step.
/// </summary>
public class SharedSettingsService : ILocalSettingsService
{
    // Mirrors LocalSettingsOptions in the WinUI build's appsettings.json
    private static readonly string Folder =
        Path.Combine(Utils.PersistentDataDirectory, "VRCFaceTracking", "ApplicationData");
    private const string FileName = "LocalSettings.json";

    private readonly IFileService _fileService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, object>? _settings;

    public SharedSettingsService(IFileService fileService)
    {
        _fileService = fileService;
    }

    private void EnsureLoaded()
    {
        _settings ??= _fileService.Read<Dictionary<string, object>>(Folder, FileName)
                      ?? new Dictionary<string, object>();
    }

    public Task<T> ReadSettingAsync<T>(string key, T? defaultValue = default, bool forceLocal = false)
    {
        EnsureLoaded();

        if (_settings!.TryGetValue(key, out var raw) && raw is string json)
        {
            try
            {
                var value = JsonConvert.DeserializeObject<T>(json);
                if (value != null)
                {
                    return Task.FromResult(value);
                }
            }
            catch (Exception)
            {
                // Fall through to the default rather than crashing on one bad setting
            }
        }

        return Task.FromResult(defaultValue!);
    }

    public async Task SaveSettingAsync<T>(string key, T value, bool forceLocal = false)
    {
        EnsureLoaded();
        _settings![key] = JsonConvert.SerializeObject(value);

        await _gate.WaitAsync();
        try
        {
            await _fileService.Save(Folder, FileName, _settings);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task Load(object target)
    {
        foreach (var (property, attribute) in SavedProperties(target))
        {
            var setting = await ReadSettingAsync(attribute.GetName(), attribute.Default(), attribute.ForceLocal());
            try
            {
                property.SetValue(target, Convert.ChangeType(setting, property.PropertyType));
            }
            catch (Exception)
            {
                property.SetValue(target, attribute.Default());
            }
        }
    }

    public async Task Save(object target)
    {
        foreach (var (property, attribute) in SavedProperties(target))
        {
            await SaveSettingAsync(attribute.GetName(), property.GetValue(target), attribute.ForceLocal());
        }
    }

    private static IEnumerable<(PropertyInfo, SavedSettingAttribute)> SavedProperties(object target)
    {
        foreach (var property in target.GetType().GetProperties())
        {
            var attribute = property.GetCustomAttributes(typeof(SavedSettingAttribute), false)
                .OfType<SavedSettingAttribute>()
                .FirstOrDefault();
            if (attribute != null)
            {
                yield return (property, attribute);
            }
        }
    }
}
