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

namespace Em68030.IO;

using Em68030.Core;

/// <summary>
/// Framebuffer control register device for Em68030.
/// Mapped at $FFFE8000, 64 bytes.
///
/// VRAM itself is in the main RAM array (fast path), not routed through this device.
/// This device provides identification and palette registers only.
///
/// Register map (offset from base $FFFE8000):
///   $00-$03  R    MAGIC      0x454D4642 ("EMFB")
///   $04-$05  R    WIDTH      Horizontal resolution
///   $06-$07  R    HEIGHT     Vertical resolution
///   $08      R    BPP        Bits per pixel (8/16/32)
///   $0A-$0B  R    STRIDE     Bytes per row
///   $0C-$0F  R    VRAM_BASE  VRAM physical address
///   $10-$13  R    VRAM_SIZE  VRAM size in bytes
///   $14      RW   ENABLE     Display enable (1) / disable (0)
///   $20      W    PAL_INDEX  Palette index (0-255, for 8bpp mode)
///   $21      W    PAL_R      Palette Red
///   $22      W    PAL_G      Palette Green
///   $23      W    PAL_B      Palette Blue (auto-increments index)
/// </summary>
public class FramebufferDevice : IMemoryMappedDevice
{
    public const uint BaseAddress = 0xFFFE8000;
    public const uint DeviceSize = 64;
    public const uint Magic = 0x454D4642; // "EMFB"

    private readonly int _width;
    private readonly int _height;
    private readonly int _bpp;
    private readonly int _stride;
    private readonly uint _vramBase;
    private readonly uint _vramSize;
    private byte _enable;

    // 256-entry palette (for 8bpp mode): each entry is [R, G, B]
    private readonly byte[,] _palette = new byte[256, 3];
    private byte _paletteIndex;

    public FramebufferDevice(int width, int height, int bpp, uint vramBase)
    {
        _width = width;
        _height = height;
        _bpp = bpp;
        _stride = width * bpp / 8;
        _vramBase = vramBase;
        _vramSize = (uint)(_stride * height);
        _enable = 1;

        // Initialize default grayscale palette
        for (int i = 0; i < 256; i++)
        {
            _palette[i, 0] = (byte)i;
            _palette[i, 1] = (byte)i;
            _palette[i, 2] = (byte)i;
        }
    }

    public bool Enabled => _enable != 0;
    public int Width => _width;
    public int Height => _height;
    public int Bpp => _bpp;
    public int Stride => _stride;
    public uint VramBase => _vramBase;
    public uint VramSize => _vramSize;

    /// <summary>Get palette entry. Returns (R, G, B).</summary>
    public (byte R, byte G, byte B) GetPaletteEntry(int index)
    {
        if (index < 0 || index > 255) return (0, 0, 0);
        return (_palette[index, 0], _palette[index, 1], _palette[index, 2]);
    }

    public byte ReadByte(uint address)
    {
        uint offset = address - BaseAddress;
        return offset switch
        {
            0x00 => unchecked((byte)(Magic >> 24)),
            0x01 => unchecked((byte)(Magic >> 16)),
            0x02 => unchecked((byte)(Magic >> 8)),
            0x03 => unchecked((byte)(Magic)),
            0x04 => (byte)(_width >> 8),
            0x05 => (byte)(_width),
            0x06 => (byte)(_height >> 8),
            0x07 => (byte)(_height),
            0x08 => (byte)_bpp,
            0x0A => (byte)(_stride >> 8),
            0x0B => (byte)(_stride),
            0x0C => (byte)(_vramBase >> 24),
            0x0D => (byte)(_vramBase >> 16),
            0x0E => (byte)(_vramBase >> 8),
            0x0F => (byte)(_vramBase),
            0x10 => (byte)(_vramSize >> 24),
            0x11 => (byte)(_vramSize >> 16),
            0x12 => (byte)(_vramSize >> 8),
            0x13 => (byte)(_vramSize),
            0x14 => _enable,
            _ => 0
        };
    }

    public void WriteByte(uint address, byte value)
    {
        uint offset = address - BaseAddress;
        switch (offset)
        {
            case 0x14:
                _enable = (byte)(value & 1);
                break;
            case 0x20:
                _paletteIndex = value;
                break;
            case 0x21:
                _palette[_paletteIndex, 0] = value; // R
                break;
            case 0x22:
                _palette[_paletteIndex, 1] = value; // G
                break;
            case 0x23:
                _palette[_paletteIndex, 2] = value; // B
                _paletteIndex++; // auto-increment after B write
                break;
        }
    }

    public ushort ReadWord(uint address)
    {
        return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
    }

    public uint ReadLong(uint address)
    {
        return (uint)((ReadWord(address) << 16) | ReadWord(address + 2));
    }

    public void WriteWord(uint address, ushort value)
    {
        WriteByte(address, (byte)(value >> 8));
        WriteByte(address + 1, (byte)(value & 0xFF));
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)(value & 0xFFFF));
    }
}
