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

using Em68030.IO;
using Xunit;

namespace Em68030.Tests.IoTests;

public class FramebufferDeviceTests
{
    private readonly FramebufferDevice _device = new(640, 480, 16, 0x00800000);

    [Fact]
    public void ReadMagic_ReturnsEMFB()
    {
        uint magic = _device.ReadLong(FramebufferDevice.BaseAddress + 0x00);
        Assert.Equal(0x454D4642u, magic);
    }

    [Fact]
    public void ReadWidth_Returns640()
    {
        ushort w = _device.ReadWord(FramebufferDevice.BaseAddress + 0x04);
        Assert.Equal(640, w);
    }

    [Fact]
    public void ReadHeight_Returns480()
    {
        ushort h = _device.ReadWord(FramebufferDevice.BaseAddress + 0x06);
        Assert.Equal(480, h);
    }

    [Fact]
    public void ReadBpp_Returns16()
    {
        byte bpp = _device.ReadByte(FramebufferDevice.BaseAddress + 0x08);
        Assert.Equal(16, bpp);
    }

    [Fact]
    public void ReadStride_Returns1280()
    {
        ushort stride = _device.ReadWord(FramebufferDevice.BaseAddress + 0x0A);
        Assert.Equal(1280, stride); // 640 * 16 / 8
    }

    [Fact]
    public void ReadVramBase_Returns0x800000()
    {
        uint vramBase = _device.ReadLong(FramebufferDevice.BaseAddress + 0x0C);
        Assert.Equal(0x00800000u, vramBase);
    }

    [Fact]
    public void ReadVramSize_Returns614400()
    {
        uint vramSize = _device.ReadLong(FramebufferDevice.BaseAddress + 0x10);
        Assert.Equal((uint)(640 * 480 * 2), vramSize);
    }

    [Fact]
    public void ReadEnable_DefaultIsEnabled()
    {
        byte enable = _device.ReadByte(FramebufferDevice.BaseAddress + 0x14);
        Assert.Equal(1, enable);
    }

    [Fact]
    public void WriteEnable_DisablesDisplay()
    {
        _device.WriteByte(FramebufferDevice.BaseAddress + 0x14, 0);
        Assert.False(_device.Enabled);
    }

    [Fact]
    public void WriteEnable_ReenablesDisplay()
    {
        _device.WriteByte(FramebufferDevice.BaseAddress + 0x14, 0);
        _device.WriteByte(FramebufferDevice.BaseAddress + 0x14, 1);
        Assert.True(_device.Enabled);
    }

    [Fact]
    public void ReadUnknownOffset_ReturnsZero()
    {
        byte val = _device.ReadByte(FramebufferDevice.BaseAddress + 0x30);
        Assert.Equal(0, val);
    }

    [Fact]
    public void WriteUnknownOffset_DoesNotCrash()
    {
        _device.WriteByte(FramebufferDevice.BaseAddress + 0x30, 0xFF);
        // No exception expected
    }

    [Fact]
    public void PaletteWrite_SetsEntry()
    {
        uint b = FramebufferDevice.BaseAddress;
        _device.WriteByte(b + 0x20, 10);   // palette index = 10
        _device.WriteByte(b + 0x21, 0xFF); // R
        _device.WriteByte(b + 0x22, 0x80); // G
        _device.WriteByte(b + 0x23, 0x40); // B (auto-increments index)

        var (r, g, bl) = _device.GetPaletteEntry(10);
        Assert.Equal(0xFF, r);
        Assert.Equal(0x80, g);
        Assert.Equal(0x40, bl);
    }

    [Fact]
    public void PaletteWrite_AutoIncrements()
    {
        uint b = FramebufferDevice.BaseAddress;
        _device.WriteByte(b + 0x20, 5);    // start at index 5
        _device.WriteByte(b + 0x21, 0x10); // R for entry 5
        _device.WriteByte(b + 0x22, 0x20); // G for entry 5
        _device.WriteByte(b + 0x23, 0x30); // B for entry 5, index becomes 6

        _device.WriteByte(b + 0x21, 0xA0); // R for entry 6
        _device.WriteByte(b + 0x22, 0xB0); // G for entry 6
        _device.WriteByte(b + 0x23, 0xC0); // B for entry 6

        var (r5, g5, b5) = _device.GetPaletteEntry(5);
        Assert.Equal(0x10, r5);
        Assert.Equal(0x20, g5);
        Assert.Equal(0x30, b5);

        var (r6, g6, b6) = _device.GetPaletteEntry(6);
        Assert.Equal(0xA0, r6);
        Assert.Equal(0xB0, g6);
        Assert.Equal(0xC0, b6);
    }

    [Fact]
    public void DefaultPalette_IsGrayscale()
    {
        var (r, g, b) = _device.GetPaletteEntry(128);
        Assert.Equal(128, r);
        Assert.Equal(128, g);
        Assert.Equal(128, b);
    }

    [Fact]
    public void Properties_MatchConstructorArgs()
    {
        Assert.Equal(640, _device.Width);
        Assert.Equal(480, _device.Height);
        Assert.Equal(16, _device.Bpp);
        Assert.Equal(1280, _device.Stride);
        Assert.Equal(0x00800000u, _device.VramBase);
        Assert.Equal((uint)(640 * 480 * 2), _device.VramSize);
    }

    [Fact]
    public void Constructor_8bpp_CorrectStride()
    {
        var dev8 = new FramebufferDevice(800, 600, 8, 0x00800000);
        Assert.Equal(800, dev8.Stride);
        Assert.Equal((uint)(800 * 600), dev8.VramSize);
    }

    [Fact]
    public void Constructor_32bpp_CorrectStride()
    {
        var dev32 = new FramebufferDevice(640, 480, 32, 0x00800000);
        Assert.Equal(2560, dev32.Stride);
        Assert.Equal((uint)(640 * 480 * 4), dev32.VramSize);
    }

    [Fact]
    public void VramInFastRam_IsAccessible()
    {
        // Verify VRAM region falls in RAM fast path
        var memory = new Core.Memory(16 * 1024 * 1024); // 16MB
        memory.WriteByte(0x00800000, 0xAB);
        memory.WriteByte(0x00800001, 0xCD);
        Assert.Equal(0xAB, memory.ReadByte(0x00800000));
        Assert.Equal(0xCD, memory.ReadByte(0x00800001));
    }
}
