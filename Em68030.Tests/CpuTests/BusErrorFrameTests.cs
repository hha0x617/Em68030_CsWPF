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
using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.CpuTests;

/// <summary>
/// バスエラーフレーム生成テスト。
/// Format $A (short bus cycle fault) フレームの構造を検証する。
/// MMU 無効状態（TC=0）でテストし、フレームレイアウトの検証に集中する。
/// </summary>
public class BusErrorFrameTests
{
    private CpuTestFixture CreateFixture()
    {
        var f = new CpuTestFixture();

        // MMU disabled (TC=0) for frame layout tests — address translation is identity
        // Setup bus error vector handler (just an RTE at some address)
        uint handlerAddr = 0x00002000;
        f.Memory.WriteWord(handlerAddr, 0x4E73); // RTE

        // Vector table: bus error vector (vector 2) at VBR + 8
        f.Cpu.VBR = 0x00000000;
        f.Memory.WriteLong(0x00000008, handlerAddr);

        return f;
    }

    [Fact]
    public void BusError_PushesFormatA_Frame()
    {
        var f = CreateFixture();
        uint originalSP = f.Cpu.A[7];
        uint originalPC = 0x00001000;
        ushort originalSR = f.Cpu.SR;
        f.Cpu.PC = originalPC;

        // Trigger bus error (MMU disabled, so PTest returns 0 and frame push uses identity)
        f.Cpu.RaiseBusError(0xDEADBEEF, false, 5, 0x0145);

        Assert.False(f.Cpu.Halted, "CPU should not be halted");

        // Format $A frame is 32 bytes (16 words)
        uint expectedSP = originalSP - 32;
        Assert.Equal(expectedSP, f.Cpu.A[7]);

        // Read frame from stack (physical memory, identity mapped since MMU disabled)
        // +$00: SR (2 bytes)
        ushort frameSR = f.Memory.ReadWord(expectedSP);
        Assert.Equal(originalSR, frameSR);

        // +$02: PC (4 bytes)
        uint framePC = f.Memory.ReadLong(expectedSP + 2);
        Assert.Equal(originalPC, framePC);

        // +$06: Format/Vector word
        ushort formatVector = f.Memory.ReadWord(expectedSP + 6);
        int format = (formatVector >> 12) & 0xF;
        Assert.Equal(0xA, format); // Format $A
        int vectorOffset = formatVector & 0x0FFF;
        Assert.Equal(8, vectorOffset); // Vector 2 * 4 = 8
    }

    [Fact]
    public void BusError_Frame_ContainsFaultAddress()
    {
        var f = CreateFixture();
        f.Cpu.PC = 0x00001000;

        f.Cpu.RaiseBusError(0xDEADBEEF, false, 5, 0x0145);

        Assert.False(f.Cpu.Halted, "CPU should not be halted");

        // Fault address is at offset +$10 in the frame
        uint sp = f.Cpu.A[7];
        uint faultAddr = f.Memory.ReadLong(sp + 0x10);
        Assert.Equal(0xDEADBEEFu, faultAddr);
    }

    [Fact]
    public void BusError_Frame_ContainsSSW()
    {
        var f = CreateFixture();
        f.Cpu.PC = 0x00001000;

        ushort testSSW = 0x0145;
        f.Cpu.RaiseBusError(0xDEADBEEF, false, 5, testSSW);

        Assert.False(f.Cpu.Halted, "CPU should not be halted");

        // SSW is at offset +$0A in the frame
        uint sp = f.Cpu.A[7];
        ushort frameSSW = f.Memory.ReadWord(sp + 0x0A);
        Assert.Equal(testSSW, frameSSW);
    }

    [Fact]
    public void BusError_DoubleBusError_Halts()
    {
        var mem = new Memory();
        // Only map a small region that does NOT include the stack area
        mem.AddRegion(0x00000000, 0x10000, RegionType.Ram);
        // Write reset vectors
        mem.WriteLong(0x00000000, 0x00800000); // SSP
        mem.WriteLong(0x00000004, 0x00001000); // PC

        var cpu = new MC68030(mem);
        cpu.SR = 0x2700;
        cpu.A[7] = 0x00800000; // SSP in unmapped area
        cpu.SSP = 0x00800000;
        cpu.VBR = 0x00000000;

        // Bus error vector handler
        mem.WriteLong(0x00000008, 0x00002000);
        mem.WriteWord(0x00002000, 0x4E73); // RTE

        cpu.PC = 0x00001000;

        // Stack is at 0x00800000 which is unmapped → push will fail → double fault
        cpu.RaiseBusError(0xDEADBEEF, false, 5, 0x0145);

        Assert.True(cpu.Halted);
    }
}
