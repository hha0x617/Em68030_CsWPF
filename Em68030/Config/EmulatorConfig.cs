// Copyright 2026 hha0x617
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

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

    // Kernel image paths for auto-load on startup (per target OS)
    public string NetBsdKernelImagePath { get; set; } = "";
    public string LinuxKernelImagePath { get; set; } = "";

    // Boot partition: 0='a', 1='b', etc. Used by boot stub to tell kernel which partition is root.
    public int Mvme147BootPartition { get; set; } = 0;

    // Target OS: "NetBSD" or "Linux"
    public string TargetOS { get; set; } = "NetBSD";

    // Linux kernel command line (used when TargetOS == "Linux")
    public string LinuxCommandLine { get; set; } = "root=/dev/sda1 console=ttyS0";

    // Network mode: "Virtual" (internal echo server) or "NAT" (host network via user-mode NAT)
    public string NetworkMode { get; set; } = "Virtual";

    // TAP adapter GUID (used when NetworkMode == "TAP (Bridge)")
    public string TapAdapterGuid { get; set; } = "";

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

    // Framebuffer (for X Window System)
    // VRAM is placed at the top of RAM (auto-calculated: MemorySize - VramSize, 1MB aligned).
    // The kernel is told RAM ends at the VRAM base, so it never touches VRAM.
    public bool FramebufferEnabled { get; set; } = false;
    public int FramebufferWidth { get; set; } = 640;
    public int FramebufferHeight { get; set; } = 480;
    public int FramebufferBpp { get; set; } = 16; // 8, 16, or 32

    /// <summary>Compute VRAM base address (top of RAM, 1MB aligned).</summary>
    public uint ComputeVramBase()
    {
        uint vramSize = (uint)(FramebufferWidth * FramebufferHeight * FramebufferBpp / 8);
        return ((uint)MemorySize - vramSize) & ~0xFFFFFu;
    }

    // JIT compiler: experimental feature, disabled by default
    public bool JitEnabled { get; set; } = false;
    public int JitMinBlockLength { get; set; } = 3;
    public int JitCompileThreshold { get; set; } = 32;

    // Call stack inspection mode.
    // "ShadowStack" : track BSR/JSR/RTS at runtime (accurate, OS-aware, default).
    // "A6Chain"     : walk the A6 frame pointer chain + scan stack heuristically
    //                 (works for code that uses LINK A6/UNLK A6, e.g. bare-metal
    //                 programs without an OS).
    public string CallStackMode { get; set; } = "ShadowStack";

    // Debug
    public bool EnableTraceButton { get; set; } = false;

    private static readonly string ConfigPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// User-writable data directory (%LOCALAPPDATA%\Em68030_CsWPF\).
    /// Falls back to exe directory if LOCALAPPDATA is unavailable.
    /// </summary>
    public static string DataDirectory { get; }

    static EmulatorConfig()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            DataDirectory = Path.Combine(localAppData, "Em68030_CsWPF");
            Directory.CreateDirectory(DataDirectory);
        }
        else
        {
            DataDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }

        // Migrate legacy files from exe directory
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.Equals(Path.GetFullPath(exeDir), Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            foreach (string name in new[] { "appsettings.json", "nvram.bin" })
            {
                string src = Path.Combine(exeDir, name);
                string dst = Path.Combine(DataDirectory, name);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    try { File.Copy(src, dst); } catch { }
                }
            }
        }

        ConfigPath = Path.Combine(DataDirectory, "appsettings.json");
    }

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

                // Migration: old single kernel image path
                if (doc.RootElement.TryGetProperty("Mvme147KernelImagePath", out var kernelProp) &&
                    string.IsNullOrEmpty(config.NetBsdKernelImagePath) &&
                    string.IsNullOrEmpty(config.LinuxKernelImagePath))
                {
                    string old = kernelProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(old))
                    {
                        if (config.TargetOS == "Linux") config.LinuxKernelImagePath = old;
                        else config.NetBsdKernelImagePath = old;
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
