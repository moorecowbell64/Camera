using JennovCamera;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("==============================================");
        Console.WriteLine("  Jennov PTZ Camera Control Demo");
        Console.WriteLine("==============================================\n");

        // Configuration - Update these values for your camera
        string cameraIp = "192.168.50.224";
        int port = 80;  // ONVIF uses port 80
        string username = "admin";
        string password = "hydroLob99";

        // Allow command line override
        if (args.Length >= 1) cameraIp = args[0];
        if (args.Length >= 2) username = args[1];
        if (args.Length >= 3) password = args[2];

        Console.WriteLine($"Connecting to camera at {cameraIp}:{port}");
        Console.WriteLine($"Username: {username}\n");

        using var client = new CameraClient(cameraIp, port);

        // Login
        if (!await client.LoginAsync(username, password))
        {
            Console.WriteLine("Failed to login. Check credentials and camera IP.");
            return;
        }

        // Create managers
        var ptz = new PTZController(client);
        var presets = new PresetManager(client);
        var recording = new RecordingManager(client);
        using var deviceMgr = new DeviceManager(cameraIp, username, password, port);

        // Main menu loop
        while (true)
        {
            Console.WriteLine("\n==============================================");
            Console.WriteLine("Main Menu:");
            Console.WriteLine("==============================================");
            Console.WriteLine("  1. PTZ Control");
            Console.WriteLine("  2. Preset Management");
            Console.WriteLine("  3. Recording");
            Console.WriteLine("  4. Device Management");
            Console.WriteLine("  5. Exit");
            Console.Write("\nSelect option: ");

            var choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    await PTZControlMenu(ptz);
                    break;
                case "2":
                    await PresetMenu(presets, ptz, client);
                    break;
                case "3":
                    await RecordingMenu(recording);
                    break;
                case "4":
                    await DeviceManagementMenu(deviceMgr);
                    break;
                case "5":
                    Console.WriteLine("\nLogging out...");
                    return;
                default:
                    Console.WriteLine("\nInvalid option.");
                    break;
            }
        }
    }

    static async Task PTZControlMenu(PTZController ptz)
    {
        while (true)
        {
            Console.WriteLine("\n--- PTZ Control ---");
            Console.WriteLine("  1. Pan Left");
            Console.WriteLine("  2. Pan Right");
            Console.WriteLine("  3. Tilt Up");
            Console.WriteLine("  4. Tilt Down");
            Console.WriteLine("  5. Move to Coordinates");
            Console.WriteLine("  6. Stop Movement");
            Console.WriteLine("  7. Get Status");
            Console.WriteLine("  8. Custom Movement");
            Console.WriteLine("  9. Back");
            Console.Write("\nSelect option: ");

            var choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    Console.Write("Speed (0.1-1.0): ");
                    float speed = float.TryParse(Console.ReadLine(), out var s) ? s : 0.5f;
                    Console.Write("Duration (ms): ");
                    int duration = int.TryParse(Console.ReadLine(), out var d) ? d : 2000;
                    await ptz.PanLeftAsync(speed, duration);
                    Console.WriteLine($"Panning left at speed {speed} for {duration}ms");
                    break;

                case "2":
                    Console.Write("Speed (0.1-1.0): ");
                    speed = float.TryParse(Console.ReadLine(), out s) ? s : 0.5f;
                    Console.Write("Duration (ms): ");
                    duration = int.TryParse(Console.ReadLine(), out d) ? d : 2000;
                    await ptz.PanRightAsync(speed, duration);
                    Console.WriteLine($"Panning right at speed {speed} for {duration}ms");
                    break;

                case "3":
                    Console.Write("Speed (0.1-1.0): ");
                    speed = float.TryParse(Console.ReadLine(), out s) ? s : 0.5f;
                    Console.Write("Duration (ms): ");
                    duration = int.TryParse(Console.ReadLine(), out d) ? d : 2000;
                    await ptz.TiltUpAsync(speed, duration);
                    Console.WriteLine($"Tilting up at speed {speed} for {duration}ms");
                    break;

                case "4":
                    Console.Write("Speed (0.1-1.0): ");
                    speed = float.TryParse(Console.ReadLine(), out s) ? s : 0.5f;
                    Console.Write("Duration (ms): ");
                    duration = int.TryParse(Console.ReadLine(), out d) ? d : 2000;
                    await ptz.TiltDownAsync(speed, duration);
                    Console.WriteLine($"Tilting down at speed {speed} for {duration}ms");
                    break;

                case "5":
                    Console.WriteLine("Note: Direct positioning not implemented in ONVIF continuous mode");
                    Console.WriteLine("Use presets instead for positioning");
                    break;

                case "6":
                    await ptz.StopMoveAsync();
                    Console.WriteLine("Movement stopped");
                    break;

                case "7":
                    Console.WriteLine("PTZ Status: Connected via ONVIF");
                    break;

                case "8":
                    Console.Write("Horizontal speed (-1.0 to 1.0): ");
                    float hSpeed = float.TryParse(Console.ReadLine(), out var h) ? h : 0;
                    Console.Write("Vertical speed (-1.0 to 1.0): ");
                    float vSpeed = float.TryParse(Console.ReadLine(), out var v) ? v : 0;
                    Console.Write("Zoom speed (-1.0 to 1.0): ");
                    float zSpeed = float.TryParse(Console.ReadLine(), out var z) ? z : 0;
                    Console.Write("Duration (ms): ");
                    duration = int.TryParse(Console.ReadLine(), out d) ? d : 2000;
                    await ptz.MoveContinuouslyAsync(hSpeed, vSpeed, zSpeed);
                    await Task.Delay(duration);
                    await ptz.StopMoveAsync();
                    Console.WriteLine($"Moved: H={hSpeed}, V={vSpeed}, Z={zSpeed} for {duration}ms");
                    break;

                case "9":
                    return;

                default:
                    Console.WriteLine("Invalid option.");
                    break;
            }
        }
    }

    static async Task PresetMenu(PresetManager presets, PTZController ptz, CameraClient client)
    {
        while (true)
        {
            Console.WriteLine("\n--- Preset Management ---");
            Console.WriteLine("  1. List Presets");
            Console.WriteLine("  2. Go to Preset");
            Console.WriteLine("  3. Set/Save Preset");
            Console.WriteLine("  4. Remove Preset");
            Console.WriteLine("  5. Back");
            Console.Write("\nSelect option: ");

            var choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    await presets.ListPresetsAsync();
                    break;

                case "2":
                    Console.Write("Preset token (e.g., '1'): ");
                    string? presetToken = Console.ReadLine();
                    if (!string.IsNullOrEmpty(presetToken))
                    {
                        var success = await client.Onvif.GotoPresetAsync(presetToken, 1.0f);
                        Console.WriteLine(success ? $"Moving to preset {presetToken}" : "Failed to go to preset");
                    }
                    break;

                case "3":
                    Console.WriteLine("Note: Setting presets requires ONVIF SetPreset command");
                    Console.WriteLine("This feature will be implemented in a future update");
                    break;

                case "4":
                    Console.WriteLine("Note: Removing presets requires ONVIF RemovePreset command");
                    Console.WriteLine("This feature will be implemented in a future update");
                    break;

                case "5":
                    return;

                default:
                    Console.WriteLine("Invalid option.");
                    break;
            }
        }
    }

    static async Task RecordingMenu(RecordingManager recording)
    {
        while (true)
        {
            Console.WriteLine("\n--- Recording & Streaming ---");
            Console.WriteLine($"  Stream Status: {(recording.IsStreaming ? "STREAMING" : "Stopped")}");
            Console.WriteLine($"  Recording Status: {(recording.IsRecording ? "RECORDING" : "Stopped")}");
            Console.WriteLine();
            Console.WriteLine("  1. Take Snapshot (ONVIF)");
            Console.WriteLine("  2. Start Streaming (RTSP)");
            Console.WriteLine("  3. Stop Streaming");
            Console.WriteLine("  4. Start Recording");
            Console.WriteLine("  5. Stop Recording");
            Console.WriteLine("  6. Capture Frame (from stream)");
            Console.WriteLine("  7. Back");
            Console.Write("\nSelect option: ");

            var choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    Console.Write("Filename (Enter for auto): ");
                    var snapFilename = Console.ReadLine();
                    var snapResult = await recording.TakeSnapshotAsync(
                        string.IsNullOrWhiteSpace(snapFilename) ? null : snapFilename);
                    Console.WriteLine(snapResult ? "Snapshot captured successfully!" : "Failed to capture snapshot");
                    break;

                case "2":
                    if (recording.IsStreaming)
                    {
                        Console.WriteLine("Already streaming.");
                    }
                    else
                    {
                        var streamResult = recording.StartStreaming();
                        Console.WriteLine(streamResult ? "Streaming started!" : "Failed to start streaming");
                    }
                    break;

                case "3":
                    recording.StopStreaming();
                    break;

                case "4":
                    Console.Write("Filename (Enter for auto): ");
                    var recFilename = Console.ReadLine();
                    var recResult = recording.StartRecording(
                        string.IsNullOrWhiteSpace(recFilename) ? null : recFilename);
                    Console.WriteLine(recResult ? "Recording started!" : "Failed to start recording");
                    break;

                case "5":
                    var savedFile = recording.StopRecording();
                    if (savedFile != null)
                    {
                        Console.WriteLine($"Recording saved: {savedFile}");
                    }
                    else
                    {
                        Console.WriteLine("No active recording to stop.");
                    }
                    break;

                case "6":
                    if (!recording.IsStreaming)
                    {
                        Console.WriteLine("Start streaming first to capture frames.");
                    }
                    else
                    {
                        using var frame = recording.CaptureFrame();
                        if (frame != null)
                        {
                            Console.Write("Filename (Enter for auto): ");
                            var frameFilename = Console.ReadLine();
                            recording.SaveFrame(frame, string.IsNullOrWhiteSpace(frameFilename) ? null : frameFilename);
                        }
                        else
                        {
                            Console.WriteLine("Failed to capture frame.");
                        }
                    }
                    break;

                case "7":
                    return;

                default:
                    Console.WriteLine("Invalid option.");
                    break;
            }
        }
    }

    static async Task DeviceManagementMenu(DeviceManager deviceMgr)
    {
        while (true)
        {
            Console.WriteLine("\n--- Device Management ---");
            Console.WriteLine("  1. Show Device Information");
            Console.WriteLine("  2. Show Network Info");
            Console.WriteLine("  3. Show Video Encoder Settings");
            Console.WriteLine("  4. Get/Set Hostname");
            Console.WriteLine("  5. Get/Set NTP");
            Console.WriteLine("  6. Show System Time");
            Console.WriteLine("  7. Get/Set OSD (Camera Title)");
            Console.WriteLine("  8. Modify Video Encoder");
            Console.WriteLine("  9. Reboot Camera");
            Console.WriteLine("  R. Factory Reset (Soft)");
            Console.WriteLine("  H. Factory Reset (Hard) [DANGEROUS]");
            Console.WriteLine("  0. Back");
            Console.Write("\nSelect option: ");

            var choice = Console.ReadLine();

            try
            {
                switch (choice)
                {
                    case "1":
                        var info = await deviceMgr.GetDeviceInformationAsync();
                        Console.WriteLine($"\n  Manufacturer: {info.Manufacturer}");
                        Console.WriteLine($"  Model: {info.Model}");
                        Console.WriteLine($"  Firmware: {info.FirmwareVersion}");
                        Console.WriteLine($"  Serial: {info.SerialNumber}");
                        Console.WriteLine($"  Hardware ID: {info.HardwareId}");
                        break;

                    case "2":
                        var network = await deviceMgr.GetNetworkInfoAsync();
                        Console.WriteLine($"\n  IP Address: {network.IpAddress}");
                        Console.WriteLine($"  MAC Address: {network.MacAddress}");
                        break;

                    case "3":
                        var encoders = await deviceMgr.GetVideoEncoderConfigsAsync();
                        Console.WriteLine("\n  Video Encoder Configurations:");
                        foreach (var enc in encoders)
                        {
                            Console.WriteLine($"    {enc}");
                        }
                        break;

                    case "4":
                        var hostname = await deviceMgr.GetHostnameAsync();
                        Console.WriteLine($"\n  Current Hostname: {hostname}");
                        Console.Write("  New hostname (Enter to skip): ");
                        var newHostname = Console.ReadLine();
                        if (!string.IsNullOrWhiteSpace(newHostname))
                        {
                            var success = await deviceMgr.SetHostnameAsync(newHostname);
                            Console.WriteLine(success ? "  Hostname updated!" : "  Failed to update hostname");
                        }
                        break;

                    case "5":
                        var ntp = await deviceMgr.GetNtpAsync();
                        Console.WriteLine($"\n  NTP Server: {ntp.NtpServer}");
                        Console.WriteLine($"  From DHCP: {ntp.FromDHCP}");
                        Console.Write("  New NTP server (Enter to skip): ");
                        var newNtp = Console.ReadLine();
                        if (!string.IsNullOrWhiteSpace(newNtp))
                        {
                            var success = await deviceMgr.SetNtpAsync(newNtp);
                            Console.WriteLine(success ? "  NTP updated!" : "  Failed to update NTP");
                        }
                        break;

                    case "6":
                        var sysTime = await deviceMgr.GetSystemDateTimeAsync();
                        Console.WriteLine($"\n  Type: {sysTime.DateTimeType}");
                        Console.WriteLine($"  Timezone: {sysTime.TimeZone}");
                        Console.WriteLine($"  UTC Time: {sysTime.UtcTime:yyyy-MM-dd HH:mm:ss}");
                        break;

                    case "7":
                        var osds = await deviceMgr.GetOSDsAsync();
                        Console.WriteLine("\n  OSD Configurations:");
                        foreach (var osd in osds)
                        {
                            Console.WriteLine($"    {osd}");
                        }
                        var titleOsd = osds.FirstOrDefault(o => o.Token == "osd_title");
                        if (titleOsd != null)
                        {
                            Console.WriteLine($"\n  Current Title: {titleOsd.PlainText}");
                            Console.Write("  New title (Enter to skip): ");
                            var newTitle = Console.ReadLine();
                            if (!string.IsNullOrWhiteSpace(newTitle))
                            {
                                var success = await deviceMgr.SetOSDAsync("osd_title", newTitle);
                                Console.WriteLine(success ? "  OSD title updated!" : "  Failed to update OSD");
                            }
                        }
                        break;

                    case "8":
                        var encConfigs = await deviceMgr.GetVideoEncoderConfigsAsync();
                        Console.WriteLine("\n  Video Encoder Configurations:");
                        for (int i = 0; i < encConfigs.Count; i++)
                        {
                            Console.WriteLine($"    {i + 1}. {encConfigs[i]}");
                        }
                        Console.Write("\n  Select encoder to modify (Enter to skip): ");
                        var encChoice = Console.ReadLine();
                        if (int.TryParse(encChoice, out int encIdx) && encIdx >= 1 && encIdx <= encConfigs.Count)
                        {
                            var enc = encConfigs[encIdx - 1];
                            Console.WriteLine($"\n  Modifying: {enc.Name}");
                            Console.WriteLine($"  Current: {enc.Width}x{enc.Height} @{enc.FrameRateLimit}fps, {enc.BitrateLimit}kbps");

                            Console.Write($"  New Width [{enc.Width}]: ");
                            var widthStr = Console.ReadLine();
                            if (int.TryParse(widthStr, out int w)) enc.Width = w;

                            Console.Write($"  New Height [{enc.Height}]: ");
                            var heightStr = Console.ReadLine();
                            if (int.TryParse(heightStr, out int h)) enc.Height = h;

                            Console.Write($"  New FrameRate [{enc.FrameRateLimit}]: ");
                            var frStr = Console.ReadLine();
                            if (int.TryParse(frStr, out int fr)) enc.FrameRateLimit = fr;

                            Console.Write($"  New Bitrate [{enc.BitrateLimit}]: ");
                            var brStr = Console.ReadLine();
                            if (int.TryParse(brStr, out int br)) enc.BitrateLimit = br;

                            var success = await deviceMgr.SetVideoEncoderConfigAsync(enc);
                            Console.WriteLine(success ? "  Encoder config updated!" : "  Failed to update encoder");
                        }
                        break;

                    case "9":
                        Console.Write("\n  Are you sure you want to reboot the camera? (yes/no): ");
                        if (Console.ReadLine()?.ToLower() == "yes")
                        {
                            var msg = await deviceMgr.RebootAsync();
                            Console.WriteLine($"  {msg}");
                            Console.WriteLine("  Camera is rebooting. Connection will be lost.");
                            return;
                        }
                        Console.WriteLine("  Reboot cancelled.");
                        break;

                    case "R":
                    case "r":
                        Console.WriteLine("\n  SOFT RESET: Resets settings but preserves network configuration.");
                        Console.Write("  Are you sure? (yes/no): ");
                        if (Console.ReadLine()?.ToLower() == "yes")
                        {
                            var success = await deviceMgr.FactoryResetAsync(hardReset: false);
                            Console.WriteLine(success ? "  Factory reset initiated!" : "  Failed to initiate reset");
                            if (success)
                            {
                                Console.WriteLine("  Camera will reboot with default settings.");
                                return;
                            }
                        }
                        Console.WriteLine("  Reset cancelled.");
                        break;

                    case "H":
                    case "h":
                        Console.WriteLine("\n  WARNING: HARD RESET will erase ALL settings including network!");
                        Console.WriteLine("  You may need to physically access the camera to reconfigure.");
                        Console.Write("  Type 'FACTORY RESET' to confirm: ");
                        if (Console.ReadLine() == "FACTORY RESET")
                        {
                            var success = await deviceMgr.FactoryResetAsync(hardReset: true);
                            Console.WriteLine(success ? "  Factory reset initiated!" : "  Failed to initiate reset");
                            if (success)
                            {
                                Console.WriteLine("  Camera will reboot with factory defaults.");
                                return;
                            }
                        }
                        Console.WriteLine("  Reset cancelled.");
                        break;

                    case "0":
                        return;

                    default:
                        Console.WriteLine("Invalid option.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error: {ex.Message}");
            }
        }
    }
}
