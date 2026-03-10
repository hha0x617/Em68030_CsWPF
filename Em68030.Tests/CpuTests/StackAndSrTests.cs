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
/// SR/スタック切り替えテスト。
/// 例外処理時の SR 保存、スーパーバイザモード遷移、VBR 参照を検証する。
/// </summary>
public class StackAndSrTests
{
    private CpuTestFixture CreateFixture()
    {
        return new CpuTestFixture();
    }

    [Fact]
    public void ExceptionProcessing_SavesSR()
    {
        var f = CreateFixture();
        uint originalSP = f.Cpu.A[7];
        ushort originalSR = 0x2700;
        f.Cpu.SR = originalSR;
        f.Cpu.PC = 0x00001000;

        // Place a handler (NOP + RTE) at the vector address
        uint handlerAddr = 0x00002000;
        f.Memory.WriteWord(handlerAddr, 0x4E71); // NOP
        f.Memory.WriteWord(handlerAddr + 2, 0x4E73); // RTE

        // Setup vector table: vector 32 (TRAP #0) at VBR + 32*4 = VBR + 0x80
        f.Cpu.VBR = 0x00000000;
        f.Memory.WriteLong(0x00000080, handlerAddr);

        // Raise exception (vector 32 = TRAP #0)
        f.Cpu.RaiseException(32);

        // SR should be saved on the stack (at the top of the frame)
        // Format 0 frame: SR(2) + PC(4) + Format/Vector(2) = 8 bytes
        uint frameSP = f.Cpu.A[7];
        ushort savedSR = f.Memory.ReadWord(frameSP);

        Assert.Equal(originalSR, savedSR);
    }

    [Fact]
    public void ExceptionProcessing_EntersSupervisorMode()
    {
        var f = CreateFixture();
        // Start in user mode
        f.Cpu.USP = 0x00700000;
        f.Cpu.SR = 0x0000; // User mode, no flags
        f.Cpu.A[7] = 0x00700000; // USP

        // Setup handler
        uint handlerAddr = 0x00002000;
        f.Memory.WriteWord(handlerAddr, 0x4E71); // NOP

        f.Cpu.VBR = 0x00000000;
        f.Memory.WriteLong(0x00000080, handlerAddr); // TRAP #0 vector

        // Raise exception
        f.Cpu.RaiseException(32);

        // CPU should now be in supervisor mode
        Assert.True(f.Cpu.SupervisorMode);
        // S bit (bit 13) should be set
        Assert.NotEqual(0, f.Cpu.SR & 0x2000);
    }

    [Fact]
    public void ExceptionProcessing_UsesVBR()
    {
        var f = CreateFixture();
        f.Cpu.PC = 0x00001000;

        // Set VBR to a non-zero base
        uint vbr = 0x00010000;
        f.Cpu.VBR = vbr;

        // Place handler address in vector table at VBR + vector*4
        uint handlerAddr = 0x00003000;
        f.Memory.WriteWord(handlerAddr, 0x4E71); // NOP

        // Vector 4 = Illegal Instruction at VBR + 4*4 = VBR + 16
        f.Memory.WriteLong(vbr + 16, handlerAddr);

        f.Cpu.RaiseException(4);

        // PC should be the handler address read from VBR-relative vector table
        Assert.Equal(handlerAddr, f.Cpu.PC);
    }

    [Fact]
    public void SetSR_UserToSupervisor_SwapsStack()
    {
        var f = CreateFixture();

        // Start in supervisor mode with known SSP
        f.Cpu.SR = 0x2700;
        f.Cpu.A[7] = 0x00800000; // SSP
        f.Cpu.SSP = 0x00800000;
        f.Cpu.USP = 0x00600000;

        // Switch to user mode
        f.Cpu.SetSR(0x0000);
        Assert.Equal(0x00600000u, f.Cpu.A[7]); // A7 should be USP now

        // Switch back to supervisor mode
        f.Cpu.SetSR(0x2700);
        Assert.Equal(0x00800000u, f.Cpu.A[7]); // A7 should be SSP again
    }
}
