namespace Em68030.Config;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

public class MemoryRegionConfig
{
    public uint BaseAddress { get; set; }
    public int Size { get; set; }
    public string Type { get; set; } = "Ram"; // "Ram" or "Rom"
}

public class ScsiDiskConfig
{
    public string Path { get; set; } = "";
    public int ScsiId { get; set; } = 0;
}

public class EmulatorConfig
{
    public int MemorySize { get; set; } = 48 * 1024 * 1024; // 48MB default
    public List<MemoryRegionConfig> MemoryRegions { get; set; } = new();
    public uint ConsoleBaseAddress { get; set; } = 0x00FF0000;
    public uint HddBaseAddress { get; set; } = 0x00FF1000;
    public bool ConsoleEnabled { get; set; } = true;
    public bool HddEnabled { get; set; } = true;
    public string HddImagePath { get; set; } = "";
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 14.0;
    public string LastOpenedFile { get; set; } = "";
    public uint LastLoadAddress { get; set; } = 0x1000;

    // Board type: "Generic" or "MVME147"
    public string BoardType { get; set; } = "Generic";

    public string Mvme147RomPath { get; set; } = "";
    public List<ScsiDiskConfig> Mvme147ScsiDisks { get; set; } = new();
    public string Mvme147ScsiCdromPath { get; set; } = "";
    public int Mvme147ScsiCdromId { get; set; } = 3;

    // Boot partition: 0='a', 1='b', etc. Used by boot stub to tell kernel which partition is root.
    public int Mvme147BootPartition { get; set; } = 0;

    // Target OS: "NetBSD" or "Linux"
    public string TargetOS { get; set; } = "NetBSD";

    // Linux kernel command line (used when TargetOS == "Linux")
    public string LinuxCommandLine { get; set; } = "root=/dev/sda1 console=ttyS0";

    // Network mode: "Virtual" (internal echo server) or "NAT" (host network via user-mode NAT)
    public string NetworkMode { get; set; } = "Virtual";

    // NAT gateway address (shared by SlirpNetworkHandler and VirtualNetworkHandler)
    public string NatGatewayIp { get; set; } = "10.0.2.2";
    public string NatGatewayMac { get; set; } = "52:54:00:12:34:56";

    // Legacy fields kept for JSON deserialization backward compatibility
    [JsonIgnore] public string Mvme147ScsiDiskPath { get; set; } = "";
    [JsonIgnore] public int Mvme147ScsiDiskId { get; set; } = 0;
    [JsonIgnore] public string Mvme147ScsiDisk2Path { get; set; } = "";
    [JsonIgnore] public int Mvme147ScsiDisk2Id { get; set; } = 1;

    // Console scrollback buffer size (lines). Range: 0..100000
    public int ConsoleScrollbackLines { get; set; } = 2000;

    // Console terminal size (columns x rows). Minimum: 80x24
    public int ConsoleColumns { get; set; } = 80;
    public int ConsoleRows { get; set; } = 24;

    // JIT compiler: experimental feature, disabled by default
    public bool JitEnabled { get; set; } = false;
    public int JitMinBlockLength { get; set; } = 3;
    public int JitCompileThreshold { get; set; } = 32;

    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static EmulatorConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<EmulatorConfig>(json, JsonOptions) ?? new EmulatorConfig();

                using var doc = JsonDocument.Parse(json);

                // Backward compat: old configs may have "Mvme147RamSize" instead of "MemorySize"
                if (doc.RootElement.TryGetProperty("Mvme147RamSize", out var ramProp) &&
                    !doc.RootElement.TryGetProperty("MemorySize", out _))
                {
                    config.MemorySize = ramProp.GetInt32();
                }

                // Migrate legacy per-disk fields to Mvme147ScsiDisks list
                if (config.Mvme147ScsiDisks.Count == 0 &&
                    !doc.RootElement.TryGetProperty("Mvme147ScsiDisks", out _))
                {
                    if (doc.RootElement.TryGetProperty("Mvme147ScsiDiskPath", out var dp))
                    {
                        string path = dp.GetString() ?? "";
                        int id = 0;
                        if (doc.RootElement.TryGetProperty("Mvme147ScsiDiskId", out var di))
                            id = di.GetInt32();
                        if (!string.IsNullOrEmpty(path))
                            config.Mvme147ScsiDisks.Add(new ScsiDiskConfig { Path = path, ScsiId = id });
                    }
                    if (doc.RootElement.TryGetProperty("Mvme147ScsiDisk2Path", out var dp2))
                    {
                        string path2 = dp2.GetString() ?? "";
                        int id2 = 1;
                        if (doc.RootElement.TryGetProperty("Mvme147ScsiDisk2Id", out var di2))
                            id2 = di2.GetInt32();
                        if (!string.IsNullOrEmpty(path2))
                            config.Mvme147ScsiDisks.Add(new ScsiDiskConfig { Path = path2, ScsiId = id2 });
                    }
                }

                config.ConsoleColumns = Math.Max(config.ConsoleColumns, 80);
                config.ConsoleRows = Math.Max(config.ConsoleRows, 24);
                return config;
            }
        }
        catch
        {
            // If load fails, return defaults
        }
        return new EmulatorConfig();
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Silently fail
        }
    }

    public EmulatorConfig Clone()
    {
        string json = JsonSerializer.Serialize(this, JsonOptions);
        return JsonSerializer.Deserialize<EmulatorConfig>(json, JsonOptions) ?? new EmulatorConfig();
    }
}
