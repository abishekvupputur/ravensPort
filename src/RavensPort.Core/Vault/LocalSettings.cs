using System;
using System.IO;
using System.Text.Json;

namespace RavensPort.Core.Vault;

public class LocalSettingsData
{
    public string OnePasswordAccountName { get; set; } = "";
}

public static class LocalSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RavensPort",
        "local_settings.json");

    private static LocalSettingsData? _current;

    public static LocalSettingsData Current
    {
        get
        {
            if (_current == null)
            {
                Load();
            }
            return _current!;
        }
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _current = JsonSerializer.Deserialize<LocalSettingsData>(json) ?? new LocalSettingsData();
            }
            else
            {
                _current = new LocalSettingsData();
            }
        }
        catch
        {
            _current = new LocalSettingsData();
        }
    }

    /// <summary>
    /// Cached, because Save runs on every settings change and a fresh JsonSerializerOptions makes
    /// the serializer rebuild its metadata cache each time.
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            var json = JsonSerializer.Serialize(_current, WriteOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore write failures for local non-critical UI settings
        }
    }
}
