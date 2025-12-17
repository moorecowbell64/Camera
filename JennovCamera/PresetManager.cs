namespace JennovCamera;

public class PresetManager
{
    private readonly CameraClient _client;

    public PresetManager(CameraClient client)
    {
        _client = client;
    }

    public async Task ListPresetsAsync()
    {
        Console.WriteLine("Preset listing not yet implemented for ONVIF");
        Console.WriteLine("You can still use GotoPreset with preset tokens");
        await Task.CompletedTask;
    }
}
