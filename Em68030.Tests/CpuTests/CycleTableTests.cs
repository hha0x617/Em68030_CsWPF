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
using Em68030.Core.Jit;
using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.CpuTests;

public class CycleTableTests : IClassFixture<CpuTestFixture>
{
    private readonly CpuTestFixture _fixture;

    public CycleTableTests(CpuTestFixture fixture) => _fixture = fixture;

    private MC68030 Cpu => _fixture.Cpu;
    private Memory Mem => _fixture.Memory;

    private void ResetCpu(uint pc = 0x1000)
    {
        for (int i = 0; i < 8; i++) { Cpu.D[i] = 0; Cpu.A[i] = 0; }
        Cpu.PC = pc;
        Cpu.SR = 0x2700;
        Cpu.A[7] = 0x00800000;
        Cpu.SSP = 0x00800000;
        Cpu.CycleCount = 0;
        Cpu.InstructionCount = 0;
    }

    // ================================================================
    // Basic cycle table lookups
    // ================================================================

    [Fact]
    public void Moveq_2Cycles()
    {
        // MOVEQ #0, D0 = 0x7000
        Assert.Equal(2, InstructionDecoder.GetCycles(0x7000));
    }

    [Fact]
    public void MoveLDnDm_2Cycles()
    {
        // MOVE.L D0, D1 = 0x2200
        Assert.Equal(2, InstructionDecoder.GetCycles(0x2200));
    }

    [Fact]
    public void MoveLMemSrc_HasEaCost()
    {
        // MOVE.L (A0), D0 = 0x2010 — 2 + 4 = 6
        Assert.Equal(6, InstructionDecoder.GetCycles(0x2010));
    }

    [Fact]
    public void MoveLMemDst_HasEaCost()
    {
        // MOVE.L D0, (A0) = 0x2080 — 2 + 0 + 4 = 6
        Assert.Equal(6, InstructionDecoder.GetCycles(0x2080));
    }

    [Fact]
    public void AddLDnDm_2Cycles()
    {
        // ADD.L D0, D1 = 0xD280
        Assert.Equal(2, InstructionDecoder.GetCycles(0xD280));
    }

    [Fact]
    public void AddLMemSrc_HasEaCost()
    {
        // ADD.L (A0), D1 = 0xD290 — 2 + 4 = 6
        Assert.Equal(6, InstructionDecoder.GetCycles(0xD290));
    }

    [Fact]
    public void Rts_10Cycles()
    {
        Assert.Equal(10, InstructionDecoder.GetCycles(0x4E75));
    }

    [Fact]
    public void BccB_6Cycles()
    {
        // BEQ.B +2 = 0x6702
        Assert.Equal(6, InstructionDecoder.GetCycles(0x6702));
    }

    [Fact]
    public void Nop_2Cycles()
    {
        Assert.Equal(2, InstructionDecoder.GetCycles(0x4E71));
    }

    [Fact]
    public void DivuW_38Cycles()
    {
        // DIVU.W D0, D1 = 0x82C0
        Assert.Equal(38, InstructionDecoder.GetCycles(0x82C0));
    }

    [Fact]
    public void MuluW_28Cycles()
    {
        // MULU.W D0, D1 = 0xC2C0
        Assert.Equal(28, InstructionDecoder.GetCycles(0xC2C0));
    }

    [Fact]
    public void FpuOp_40Cycles()
    {
        Assert.Equal(40, InstructionDecoder.GetCycles(0xF200));
    }

    [Fact]
    public void LineA_34Cycles()
    {
        Assert.Equal(34, InstructionDecoder.GetCycles(0xA000));
    }

    // ================================================================
    // JIT block TotalCycles
    // ================================================================

    [Fact]
    public void JitBlock_TotalCycles()
    {
        ResetCpu();
        // Block: MOVEQ #1,D0 (2) + MOVEQ #2,D1 (2) + ADD.L D0,D1 (2) = 6
        Mem.WriteWord(0x1000, 0x7001); // MOVEQ #1, D0
        Mem.WriteWord(0x1002, 0x7201); // MOVEQ #1, D1
        Mem.WriteWord(0x1004, 0xD280); // ADD.L D0, D1
        Mem.WriteWord(0x1006, 0x4AFC); // ILLEGAL (terminates block)

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        Assert.Equal(6, block.TotalCycles); // 2+2+2

        int sum = InstructionDecoder.GetCycles(0x7001)
                + InstructionDecoder.GetCycles(0x7201)
                + InstructionDecoder.GetCycles(0xD280);
        Assert.Equal(sum, block.TotalCycles);
    }

    // ================================================================
    // Integration: CycleCount and InstructionCount increments
    // ================================================================

    [Fact]
    public void CycleCountIncrement()
    {
        ResetCpu();
        ushort opcode = 0x7000 | 42; // MOVEQ #42, D0
        Mem.WriteWord(0x1000, opcode);

        Cpu.ExecuteNextFast();

        Assert.Equal(InstructionDecoder.GetCycles(opcode), Cpu.CycleCount);
    }

    [Fact]
    public void InstructionCountIncrement()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x4E71); // NOP

        Cpu.ExecuteNextFast();

        Assert.Equal(1, Cpu.InstructionCount);
        Assert.Equal(2, Cpu.CycleCount); // NOP = 2 cycles
    }
}
