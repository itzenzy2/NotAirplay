using System.IO;
using System.Text.Json;

namespace NotAirplay;

internal sealed class AppSettings
{
    public string CustomName { get; set; } = "";
    public int TargetFps { get; set; } = 60;
    public string? AudioDeviceId { get; set; }

    public static int NormalizeFps(int fps) => fps is 30 or 120 ? fps : 60;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotAirplay", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    loaded.CustomName = loaded.CustomName?.Trim() ?? "";
                    loaded.TargetFps = NormalizeFps(loaded.TargetFps);
                    loaded.AudioDeviceId = string.IsNullOrWhiteSpace(loaded.AudioDeviceId) ? null : loaded.AudioDeviceId.Trim();
                    return loaded;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new AppSettings
            {
                CustomName = CustomName?.Trim() ?? "",
                TargetFps = NormalizeFps(TargetFps),
                AudioDeviceId = string.IsNullOrWhiteSpace(AudioDeviceId) ? null : AudioDeviceId.Trim(),
            }));
        }
        catch { }
    }
}
