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
    public string Password { get; set; } = "";  // Saved on successful login
    public string FFmpegPath { get; set; } = "";  // Empty = auto-detect

    // Enhanced calibration settings
    public CalibrationProfile Calibration { get; set; } = new();

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

/// <summary>
/// Enhanced calibration profile with separate pan/tilt multipliers
/// and distance-based correction curves
/// </summary>
public class CalibrationProfile
{
    // Base multipliers (ms per unit distance at speed 1.0)
    public double PanMultiplier { get; set; } = 1700;    // Horizontal movement
    public double TiltMultiplier { get; set; } = 1700;   // Vertical movement

    // Distance correction factors (multiply base by these at different distances)
    // Near movements need less time, far movements need more (non-linear camera response)
    public double NearFactor { get; set; } = 0.85;   // For distance < 0.35
    public double MidFactor { get; set; } = 1.0;     // For distance 0.35-0.65
    public double FarFactor { get; set; } = 1.15;    // For distance > 0.65

    // Response time compensation (ms) - accounts for camera acceleration/deceleration
    public double ResponseDelay { get; set; } = 80;

    // Deceleration profile (0-1, higher = more aggressive slowdown near target)
    public double DecelerationStrength { get; set; } = 0.7;

    // When was calibration last performed
    public DateTime LastCalibrated { get; set; } = DateTime.MinValue;
    public int SampleCount { get; set; } = 0;
    public double AverageError { get; set; } = 0;

    /// <summary>
    /// Calculate movement duration for a given offset
    /// </summary>
    public (int panDurationMs, int tiltDurationMs) CalculateMoveDuration(double normalizedX, double normalizedY)
    {
        var absX = Math.Abs(normalizedX);
        var absY = Math.Abs(normalizedY);
        var distance = Math.Sqrt(normalizedX * normalizedX + normalizedY * normalizedY);

        // Get distance correction factor
        var distanceFactor = distance switch
        {
            < 0.35 => NearFactor,
            > 0.65 => FarFactor,
            _ => MidFactor
        };

        // Calculate individual axis durations
        var panDuration = (int)(absX * PanMultiplier * distanceFactor);
        var tiltDuration = (int)(absY * TiltMultiplier * distanceFactor);

        return (panDuration, tiltDuration);
    }

    /// <summary>
    /// Get calibration quality description
    /// </summary>
    public string GetQualityDescription()
    {
        if (SampleCount == 0)
            return "Not calibrated";
        if (AverageError < 0.05)
            return $"Excellent ({SampleCount} samples, {AverageError * 100:F1}% error)";
        if (AverageError < 0.10)
            return $"Good ({SampleCount} samples, {AverageError * 100:F1}% error)";
        if (AverageError < 0.20)
            return $"Fair ({SampleCount} samples, {AverageError * 100:F1}% error)";
        return $"Poor ({SampleCount} samples, {AverageError * 100:F1}% error)";
    }
}
