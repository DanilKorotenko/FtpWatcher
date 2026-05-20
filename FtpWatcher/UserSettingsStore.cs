using System;
using System.IO;
using System.Text.Json;

namespace FtpWatcher;

public static class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FtpWatcher",
            "settings.json");

    public static UserSettings Load()
    {
        try
        {
            var path = SettingsFilePath;
            if (!File.Exists(path))
            {
                return new UserSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var path = SettingsFilePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(path, json);
    }
}
