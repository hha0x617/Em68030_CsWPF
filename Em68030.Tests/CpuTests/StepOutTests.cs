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
/// StepOut テスト。
/// SP ベースの StepOut ロジック（RTS + A7 >= stepOutSP で停止）を検証する。
/// -fomit-frame-pointer 環境（LINK/UNLK なし）でも正しく動作することを確認する。
/// </summary>
public class StepOutTests
{
    private readonly CpuTestFixture _f = new();
    private Memory Memory => _f.Memory;
    private MC68030 Cpu => _f.Cpu;

    /// <summary>
    /// StepOut ロジック: stepOutSP を記録し、RTS + A7 >= stepOutSP で停止するまで実行。
    /// </summary>
    private bool RunStepOut(uint stepOutSP, int maxSteps = 1000)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            if (Cpu.A[7] >= stepOutSP)
            {
                ushort nextOp = Memory.ReadWord(Cpu.PC);
                if (nextOp == 0x4E75) // RTS
                {
                    Cpu.ExecuteStep();
                    return true;
                }
            }
            Cpu.ExecuteStep();
        }
        return false;
    }

    private void WriteBsrW(uint fromAddr, uint toAddr)
    {
        short disp = (short)(toAddr - (fromAddr + 2));
        Memory.WriteWord(fromAddr, 0x6100);
        Memory.WriteWord(fromAddr + 2, (ushort)disp);
    }

    [Fact]
    public void SimpleSubroutine_RtsImmediately()
    {
        uint mainAddr = 0x1000;
        uint subAddr  = 0x2000;
        uint returnAddr = mainAddr + 4;

        WriteBsrW(mainAddr, subAddr);
        Memory.WriteWord(returnAddr, 0x4E71); // NOP
        Memory.WriteWord(subAddr, 0x4E75);    // RTS

        Cpu.PC = mainAddr;
        Cpu.ExecuteStep(); // BSR

        Assert.Equal(subAddr, Cpu.PC);
        uint spAtStepOut = Cpu.A[7];

        bool found = RunStepOut(spAtStepOut);

        Assert.True(found);
        Assert.Equal(returnAddr, Cpu.PC);
    }

    [Fact]
    public void SubroutineWithMovem_RestoresAndReturns()
    {
        uint mainAddr = 0x1000;
        uint subAddr  = 0x2000;
        uint returnAddr = mainAddr + 4;

        WriteBsrW(mainAddr, subAddr);
        Memory.WriteWord(returnAddr, 0x4E71);

        // Sub: MOVEM.L D0-D3,-(A7) → NOP → MOVEM.L (A7)+,D0-D3 → RTS
        uint addr = subAddr;
        Memory.WriteWord(addr, 0x48E7);       // MOVEM.L reg,-(A7)
        Memory.WriteWord(addr + 2, 0xF000);   // D0-D3
        addr += 4;
        Memory.WriteWord(addr, 0x4E71);       // NOP
        addr += 2;
        Memory.WriteWord(addr, 0x4CDF);       // MOVEM.L (A7)+,reg
        Memory.WriteWord(addr + 2, 0x000F);   // D0-D3
        addr += 4;
        Memory.WriteWord(addr, 0x4E75);       // RTS

        Cpu.PC = mainAddr;
        Cpu.ExecuteStep(); // BSR

        Assert.Equal(subAddr, Cpu.PC);
        uint spAtStepOut = Cpu.A[7];

        bool found = RunStepOut(spAtStepOut);

        Assert.True(found);
        Assert.Equal(returnAddr, Cpu.PC);
    }

    [Fact]
    public void SubroutineWithLinkUnlk_StepsOutCorrectly()
    {
        uint mainAddr = 0x1000;
        uint subAddr  = 0x2000;
        uint returnAddr = mainAddr + 4;

        WriteBsrW(mainAddr, subAddr);
        Memory.WriteWord(returnAddr, 0x4E71);

        // Sub: LINK A6,#-8 → NOP → UNLK A6 → RTS
        uint addr = subAddr;
        Memory.WriteWord(addr, 0x4E56);       // LINK A6
        Memory.WriteWord(addr + 2, 0xFFF8);   // #-8
        addr += 4;
        Memory.WriteWord(addr, 0x4E71);       // NOP
        addr += 2;
        Memory.WriteWord(addr, 0x4E5E);       // UNLK A6
        addr += 2;
        Memory.WriteWord(addr, 0x4E75);       // RTS

        Cpu.PC = mainAddr;
        Cpu.ExecuteStep(); // BSR

        Assert.Equal(subAddr, Cpu.PC);
        uint spAtStepOut = Cpu.A[7];

        bool found = RunStepOut(spAtStepOut);

        Assert.True(found);
        Assert.Equal(returnAddr, Cpu.PC);
    }

    [Fact]
    public void NestedSubroutines_StopsAtCorrectLevel()
    {
        uint mainAddr = 0x1000;
        uint sub1Addr = 0x2000;
        uint sub2Addr = 0x3000;
        uint returnAddr = mainAddr + 4;

        WriteBsrW(mainAddr, sub1Addr);
        Memory.WriteWord(returnAddr, 0x4E71);

        // Sub1: NOP → BSR.W sub2 → NOP → RTS
        uint addr = sub1Addr;
        Memory.WriteWord(addr, 0x4E71);       // NOP
        addr += 2;
        WriteBsrW(addr, sub2Addr);
        addr += 4;
        Memory.WriteWord(addr, 0x4E71);       // NOP
        addr += 2;
        Memory.WriteWord(addr, 0x4E75);       // RTS

        // Sub2: NOP → RTS
        Memory.WriteWord(sub2Addr, 0x4E71);
        Memory.WriteWord(sub2Addr + 2, 0x4E75);

        Cpu.PC = mainAddr;
        Cpu.ExecuteStep(); // BSR to sub1

        Assert.Equal(sub1Addr, Cpu.PC);
        uint spAtStepOut = Cpu.A[7];

        bool found = RunStepOut(spAtStepOut);

        Assert.True(found);
        Assert.Equal(returnAddr, Cpu.PC);
    }

    [Fact]
    public void SubroutineWithManualSpAdjust_NoFramePointer()
    {
        uint mainAddr = 0x1000;
        uint subAddr  = 0x2000;
        uint returnAddr = mainAddr + 4;

        WriteBsrW(mainAddr, subAddr);
        Memory.WriteWord(returnAddr, 0x4E71);

        // Sub: SUBQ.L #8,A7 → NOP → ADDQ.L #8,A7 → RTS
        uint addr = subAddr;
        Memory.WriteWord(addr, 0x518F);       // SUBQ.L #8,A7
        addr += 2;
        Memory.WriteWord(addr, 0x4E71);       // NOP
        addr += 2;
        Memory.WriteWord(addr, 0x508F);       // ADDQ.L #8,A7
        addr += 2;
        Memory.WriteWord(addr, 0x4E75);       // RTS

        Cpu.PC = mainAddr;
        Cpu.ExecuteStep(); // BSR

        Assert.Equal(subAddr, Cpu.PC);
        uint spAtStepOut = Cpu.A[7];

        bool found = RunStepOut(spAtStepOut);

        Assert.True(found);
        Assert.Equal(returnAddr, Cpu.PC);
    }

    [Fact]
    public void StepOutFromMiddleOfFunction_AfterStackAllocation()
    {
        uint mainAddr = 0x1000;
        uint subAddr  = 0x2000;
        uint returnAddr = mainAddr + 4;

        WriteBsrW(mainAddr, subAddr);
        Memory.WriteWord(returnAddr, 0x4E71);

        // Sub: SUBQ.L #8,A7 → NOP → NOP → ADDQ.L #8,A7 → RTS
        uint addr = subAddr;
        Memory.WriteWord(addr, 0x518F);       // SUBQ.L #8,A7
        addr += 2;
        Memory.WriteWord(addr, 0x4E71);       // NOP
        addr += 2;
        Memory.WriteWord(addr, 0x4E71);       // NOP
        addr += 2;
        Memory.WriteWord(addr, 0x508F);       // ADDQ.L #8,A7
        addr += 2;
        Memory.WriteWord(addr, 0x4E75);       // RTS

        Cpu.PC = mainAddr;
        Cpu.ExecuteStep(); // BSR
        Cpu.ExecuteStep(); // SUBQ.L #8,A7
        Cpu.ExecuteStep(); // first NOP (now in middle)

        uint spAtStepOut = Cpu.A[7];

        bool found = RunStepOut(spAtStepOut);

        Assert.True(found);
        Assert.Equal(returnAddr, Cpu.PC);
    }
}
