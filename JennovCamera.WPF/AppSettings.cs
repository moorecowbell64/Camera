using System.IO;
using System.Text.Json;

namespace JennovCamera.WPF;

public class AppSettings
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JennovCamera");

    private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

    public string RecordingFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public double ClickTravelMultiplier { get; set; } = 1700;
    public string CameraIp { get; set; } = "192.168.50.224";
    public string Username { get; set; } = "admin";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    Console.WriteLine($"Settings loaded from: {SettingsFile}");
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load settings: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(SettingsFolder))
            {
                Directory.CreateDirectory(SettingsFolder);
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SettingsFile, json);
            Console.WriteLine($"Settings saved to: {SettingsFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }
}
