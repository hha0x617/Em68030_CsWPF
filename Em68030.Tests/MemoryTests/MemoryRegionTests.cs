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

using Em68030.Core;
using Xunit;

namespace Em68030.Tests.MemoryTests;

/// <summary>
/// メモリリージョン管理テスト。
/// byte/word/long の読み書き、未マッピングアドレス、ROM 動作を検証する。
/// </summary>
public class MemoryRegionTests
{
    [Fact]
    public void ReadWrite_Byte()
    {
        var mem = new Memory(1024);

        mem.WriteByte(0x00, 0xAB);
        byte val = mem.ReadByte(0x00);

        Assert.Equal(0xAB, val);
    }

    [Fact]
    public void ReadWrite_Word()
    {
        var mem = new Memory(1024);

        mem.WriteWord(0x00, 0xBEEF);
        ushort val = mem.ReadWord(0x00);

        Assert.Equal(0xBEEF, val);
    }

    [Fact]
    public void ReadWrite_Long()
    {
        var mem = new Memory(1024);

        mem.WriteLong(0x00, 0xDEADBEEF);
        uint val = mem.ReadLong(0x00);

        Assert.Equal(0xDEADBEEFu, val);
    }

    [Fact]
    public void ReadWrite_BigEndian()
    {
        var mem = new Memory(1024);

        // Write a long and verify byte order (big-endian)
        mem.WriteLong(0x10, 0x01020304);

        Assert.Equal(0x01, mem.ReadByte(0x10));
        Assert.Equal(0x02, mem.ReadByte(0x11));
        Assert.Equal(0x03, mem.ReadByte(0x12));
        Assert.Equal(0x04, mem.ReadByte(0x13));

        // Word reads should also be big-endian
        Assert.Equal(0x0102, (int)mem.ReadWord(0x10));
        Assert.Equal(0x0304, (int)mem.ReadWord(0x12));
    }

    [Fact]
    public void UnmappedAddress_ThrowsBusError()
    {
        var mem = new Memory(); // Empty memory, no regions

        Assert.Throws<BusErrorException>(() => mem.ReadByte(0x00000000));
        Assert.Throws<BusErrorException>(() => mem.ReadWord(0x00000000));
        Assert.Throws<BusErrorException>(() => mem.ReadLong(0x00000000));
        Assert.Throws<BusErrorException>(() => mem.WriteByte(0x00000000, 0));
        Assert.Throws<BusErrorException>(() => mem.WriteWord(0x00000000, 0));
        Assert.Throws<BusErrorException>(() => mem.WriteLong(0x00000000, 0));
    }

    [Fact]
    public void Rom_WriteIgnored()
    {
        var mem = new Memory();
        mem.AddRegion(0xFF800000, 0x80000, RegionType.Rom); // 512KB ROM

        // Pre-fill ROM via Poke (debugger/loader path)
        mem.PokeByte(0xFF800000, 0xAA);

        // Read should return the poked value
        Assert.Equal(0xAA, mem.ReadByte(0xFF800000));

        // Write via normal path should be ignored (ROM)
        mem.WriteByte(0xFF800000, 0x55);

        // Value should still be original
        Assert.Equal(0xAA, mem.ReadByte(0xFF800000));
    }

    [Fact]
    public void MultipleRegions_IndependentAccess()
    {
        var mem = new Memory();
        mem.AddRegion(0x00000000, 0x100000, RegionType.Ram);  // 1MB RAM at 0
        mem.AddRegion(0xFF800000, 0x80000, RegionType.Rom);    // 512KB ROM at FF800000

        // Write to RAM
        mem.WriteLong(0x00000100, 0x12345678);

        // Write to ROM (via Poke, since normal Write is ignored for ROM)
        mem.PokeLong(0xFF800000, 0xAABBCCDD);

        // Read back independently
        Assert.Equal(0x12345678u, mem.ReadLong(0x00000100));
        Assert.Equal(0xAABBCCDDu, mem.ReadLong(0xFF800000));

        // Ensure they don't interfere
        mem.WriteLong(0x00000100, 0x00000000);
        Assert.Equal(0x00000000u, mem.ReadLong(0x00000100));
        Assert.Equal(0xAABBCCDDu, mem.ReadLong(0xFF800000)); // ROM unchanged
    }

    [Fact]
    public void Peek_UnmappedAddress_ReturnsDefault()
    {
        var mem = new Memory(); // Empty

        // Peek should return default values, not throw
        Assert.Equal(0xFF, mem.PeekByte(0x00000000));
        Assert.Equal(0xFFFF, (int)mem.PeekWord(0x00000000));
        Assert.Equal(0xFFFFFFFFu, mem.PeekLong(0x00000000));
    }

    [Fact]
    public void LoadData_And_GetRange()
    {
        var mem = new Memory(1024);

        byte[] data = { 0x01, 0x02, 0x03, 0x04, 0x05 };
        mem.LoadData(0x00000100, data);

        byte[] result = mem.GetRange(0x00000100, 5);
        Assert.Equal(data, result);
    }
}
