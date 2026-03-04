using Em68030.Core;
using Em68030.Core.Jit;
using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.CpuTests;

/// <summary>
/// Tests for the JIT basic-block compiler.
/// Verifies that compiled blocks produce identical results to the interpreter.
/// </summary>
public class JitCompilerTests : IClassFixture<CpuTestFixture>
{
    private readonly CpuTestFixture _fixture;

    public JitCompilerTests(CpuTestFixture fixture) => _fixture = fixture;

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
        Cpu.JitCache.InvalidateAll();
        // Clear code area with unsupported opcodes to prevent cross-test interference
        for (uint a = 0x1000; a < 0x1040; a += 2)
            Mem.PokeWord(a, 0x4AFC); // ILLEGAL — not JIT-compilable
    }

    // ================================================================
    // MOVEQ tests
    // ================================================================

    [Fact]
    public void Moveq_PositiveImmediate()
    {
        ResetCpu();
        uint addr = 0x1000;
        // MOVEQ #42,D3 = 0x7600 | 42 = 0x762A
        Mem.WriteWord(addr, 0x762A);
        // NOP to terminate block
        Mem.WriteWord(addr + 2, 0x4E71);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(2, block.InstructionCount);
        Assert.Equal(4, block.ByteLength);

        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1004u, nextPC);
        Assert.Equal(42u, Cpu.D[3]);
        Assert.True(Cpu.FlagZ == false);
        Assert.True(Cpu.FlagN == false);
    }

    [Fact]
    public void Moveq_NegativeImmediate()
    {
        ResetCpu();
        uint addr = 0x1000;
        // MOVEQ #-1,D0 = 0x70FF
        Mem.WriteWord(addr, 0x70FF);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0xFFFFFFFFu, Cpu.D[0]);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void Moveq_Zero_SetsZFlag()
    {
        ResetCpu();
        uint addr = 0x1000;
        // MOVEQ #0,D5 = 0x7A00
        Mem.WriteWord(addr, 0x7A00);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[5]);
        Assert.True(Cpu.FlagZ);
        Assert.False(Cpu.FlagN);
    }

    // ================================================================
    // MOVE.L Dn,Dm tests
    // ================================================================

    [Fact]
    public void MoveL_Dn_Dm()
    {
        ResetCpu();
        Cpu.D[2] = 0xDEADBEEF;
        uint addr = 0x1000;
        // MOVE.L D2,D4 = 0x2802 (dst=4 in bits 11-9, dstMode=0, srcMode=0, src=2)
        Mem.WriteWord(addr, 0x2802);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0xDEADBEEFu, Cpu.D[4]);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    // ================================================================
    // ADD.L / SUB.L / CMP.L Dn,Dm tests
    // ================================================================

    [Fact]
    public void AddL_Dn_Dm()
    {
        ResetCpu();
        Cpu.D[0] = 100;
        Cpu.D[1] = 200;
        uint addr = 0x1000;
        // ADD.L D1,D0: opcode = 0xD081 (D0 += D1, opMode=010, eaMode=000, eaReg=1, Dn=0)
        // Actually: group=0xD, Dn=0(bits11-9), opMode=2(bits8-6=010), eaMode=0(bits5-3), eaReg=1(bits2-0)
        // = 0xD000 | (0<<9) | (2<<6) | (0<<3) | 1 = 0xD081
        Mem.WriteWord(addr, 0xD081);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(300u, Cpu.D[0]);
        Assert.False(Cpu.FlagZ);
        Assert.False(Cpu.FlagN);
        Assert.False(Cpu.FlagC);
    }

    [Fact]
    public void SubL_Dn_Dm()
    {
        ResetCpu();
        Cpu.D[3] = 500;
        Cpu.D[4] = 200;
        uint addr = 0x1000;
        // SUB.L D4,D3: group=0x9, Dn=3(bits11-9), opMode=2(bits8-6=010), eaMode=0(bits5-3), eaReg=4(bits2-0)
        // = 0x9000 | (3<<9) | (2<<6) | 4 = 0x9684
        Mem.WriteWord(addr, 0x9684);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(300u, Cpu.D[3]);
        Assert.False(Cpu.FlagZ);
        Assert.False(Cpu.FlagN);
    }

    [Fact]
    public void CmpL_Dn_Dm_Equal()
    {
        ResetCpu();
        Cpu.D[0] = 42;
        Cpu.D[1] = 42;
        uint addr = 0x1000;
        // CMP.L D1,D0: group=0xB, Dn=0(bits11-9), opMode=2(bits8-6=010), eaMode=0, eaReg=1
        // = 0xB000 | (0<<9) | (2<<6) | 1 = 0xB081
        Mem.WriteWord(addr, 0xB081);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Cpu.FlagX = true; // X should be preserved by CMP
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(42u, Cpu.D[0]); // CMP doesn't modify destination
        Assert.True(Cpu.FlagZ);
        Assert.True(Cpu.FlagX); // X preserved
    }

    // ================================================================
    // Logic ops: AND.L / OR.L / EOR.L
    // ================================================================

    [Fact]
    public void AndL_Dn_Dm()
    {
        ResetCpu();
        Cpu.D[0] = 0xFF00FF00;
        Cpu.D[1] = 0x0F0F0F0F;
        uint addr = 0x1000;
        // AND.L D1,D0: group=0xC, Dn=0(bits11-9), opMode=2(010), eaMode=0, eaReg=1
        // = 0xC000 | (0<<9) | (2<<6) | 1 = 0xC081
        Mem.WriteWord(addr, 0xC081);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x0F000F00u, Cpu.D[0]);
    }

    [Fact]
    public void OrL_Dn_Dm()
    {
        ResetCpu();
        Cpu.D[2] = 0x00FF0000;
        Cpu.D[3] = 0x000000FF;
        uint addr = 0x1000;
        // OR.L D3,D2: group=0x8, Dn=2(bits11-9), opMode=2(010), eaMode=0, eaReg=3
        // = 0x8000 | (2<<9) | (2<<6) | 3 = 0x8483
        Mem.WriteWord(addr, 0x8483);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x00FF00FFu, Cpu.D[2]);
    }

    [Fact]
    public void EorL_Dn_Dm()
    {
        ResetCpu();
        Cpu.D[0] = 0xFFFF0000;
        Cpu.D[1] = 0xFF00FF00;
        uint addr = 0x1000;
        // EOR.L D0,D1: group=0xB, Dn=0(bits11-9), opMode=6(110), eaMode=0, eaReg=1
        // = 0xB000 | (0<<9) | (6<<6) | 1 = 0xB181
        Mem.WriteWord(addr, 0xB181);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x00FFFF00u, Cpu.D[1]);
        Assert.Equal(0xFFFF0000u, Cpu.D[0]); // Source unchanged
    }

    // ================================================================
    // Branch tests
    // ================================================================

    [Fact]
    public void BRA_B_Forward()
    {
        ResetCpu();
        uint addr = 0x1000;
        // BRA.B +6: 0x6006 (displacement 6, target = PC+2+6 = 0x1008)
        Mem.WriteWord(addr, 0x6006);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(1, block.InstructionCount);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1008u, nextPC);
    }

    [Fact]
    public void Bcc_B_Taken()
    {
        ResetCpu();
        Cpu.FlagZ = true; // BEQ will be taken
        uint addr = 0x1000;
        // BEQ.B +4: cond=7(EQ), disp=4 → 0x6704
        Mem.WriteWord(addr, 0x6704);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1006u, nextPC); // PC+2+4 = 0x1006
    }

    [Fact]
    public void Bcc_B_NotTaken()
    {
        ResetCpu();
        Cpu.FlagZ = false; // BEQ will NOT be taken
        uint addr = 0x1000;
        // BEQ.B +4: 0x6704
        Mem.WriteWord(addr, 0x6704);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1002u, nextPC); // Fallthrough
    }

    // ================================================================
    // Multi-instruction block tests
    // ================================================================

    [Fact]
    public void MultiInstruction_MoveqAddBra_BackwardBranchExcluded()
    {
        // Backward branches are excluded from JIT blocks to prevent false
        // infinite-loop detection. The block compiles only the straight-line code.
        ResetCpu();
        uint addr = 0x1000;
        // MOVEQ #10,D0 = 0x7000 | 10 = 0x700A
        Mem.WriteWord(addr, 0x700A);
        // MOVEQ #20,D1 = 0x7200 | 20 = 0x7214
        Mem.WriteWord(addr + 2, 0x7214);
        // ADD.L D1,D0 = 0xD081
        Mem.WriteWord(addr + 4, 0xD081);
        // BRA.B -8: displacement = -8 (0xF8), target = (addr+8) + (-8) = addr
        // This backward branch is NOT included — block terminates before it.
        Mem.WriteWord(addr + 6, 0x60F8);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount); // MOVEQ, MOVEQ, ADD (no BRA)
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(30u, Cpu.D[0]);
        Assert.Equal(20u, Cpu.D[1]);
        Assert.Equal(0x1006u, nextPC); // Points to the BRA instruction (interpreter handles it)
    }

    [Fact]
    public void MultiInstruction_ForwardBranchIncluded()
    {
        // Forward branches ARE included in JIT blocks.
        ResetCpu();
        uint addr = 0x1000;
        // MOVEQ #5,D0
        Mem.WriteWord(addr, 0x7005);
        // BRA.B +4: target = 0x1002 + 2 + 4 = 0x1008 (forward)
        Mem.WriteWord(addr + 2, 0x6004);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(2, block.InstructionCount);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(5u, Cpu.D[0]);
        Assert.Equal(0x1008u, nextPC);
    }

    [Fact]
    public void MultiInstruction_DeadFlagElimination()
    {
        // When multiple MOVEQs follow each other, only the last one needs to set flags.
        // This test verifies the final flags match the last instruction.
        ResetCpu();
        uint addr = 0x1000;
        // MOVEQ #5,D0
        Mem.WriteWord(addr, 0x7005);
        // MOVEQ #0,D1 (sets Z)
        Mem.WriteWord(addr + 2, 0x7200);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(5u, Cpu.D[0]);
        Assert.Equal(0u, Cpu.D[1]);
        Assert.True(Cpu.FlagZ); // From MOVEQ #0
    }

    // ================================================================
    // JitCache tests
    // ================================================================

    [Fact]
    public void JitCache_InvalidateAll()
    {
        var cache = new JitCache();
        cache.AddBlock(0x1000, new CompiledBlock(0x1000, 1, 2, _ => 0));
        cache.AddBlock(0x2000, new CompiledBlock(0x2000, 1, 2, _ => 0));

        cache.InvalidateAll();
        Assert.Null(cache.TryGetBlock(0x1000));
        Assert.Null(cache.TryGetBlock(0x2000));
    }

    [Fact]
    public void JitCache_ExecutionCountThreshold()
    {
        var cache = new JitCache();
        for (byte i = 1; i < 16; i++)
            Assert.Equal(i, cache.IncrementAndGetCount(0x1000));
        Assert.Equal(16, cache.IncrementAndGetCount(0x1000));
    }

    // ================================================================
    // Unsupported instruction terminates block
    // ================================================================

    [Fact]
    public void UnsupportedInstruction_ReturnsNull()
    {
        ResetCpu();
        uint addr = 0x1000;
        // RTS = 0x4E75 (not supported — memory access via stack)
        Mem.WriteWord(addr, 0x4E75);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.Null(block); // First instruction unsupported → null
    }

    [Fact]
    public void UnsupportedInstruction_TerminatesBlock()
    {
        ResetCpu();
        uint addr = 0x1000;
        // MOVEQ #1,D0
        Mem.WriteWord(addr, 0x7001);
        // MOVEQ #2,D1
        Mem.WriteWord(addr + 2, 0x7202);
        // RTS (unsupported) — block ends before this
        Mem.WriteWord(addr + 4, 0x4E75);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(2, block.InstructionCount); // Only the two MOVEQs
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1004u, nextPC);
        Assert.Equal(1u, Cpu.D[0]);
        Assert.Equal(2u, Cpu.D[1]);
    }

    // ================================================================
    // Self-modifying code invalidation
    // ================================================================

    [Fact]
    public void InvalidateAll_ClearsBlocks()
    {
        ResetCpu();
        uint addr = 0x1000;
        Mem.WriteWord(addr, 0x7005); // MOVEQ #5,D0

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        // Add to cache
        Cpu.JitCache.AddBlock(0x1000, block);
        Assert.NotNull(Cpu.JitCache.TryGetBlock(0x1000));

        // InvalidateAll clears all blocks (used on MMU flush, privilege change)
        Cpu.JitCache.InvalidateAll();
        Assert.Null(Cpu.JitCache.TryGetBlock(0x1000));
    }

    // ================================================================
    // NOP handling
    // ================================================================

    [Fact]
    public void Nop_InBlock()
    {
        ResetCpu();
        uint addr = 0x1000;
        // MOVEQ #7,D0
        Mem.WriteWord(addr, 0x7007);
        // NOP
        Mem.WriteWord(addr + 2, 0x4E71);
        // MOVEQ #3,D1
        Mem.WriteWord(addr + 4, 0x7203);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1006u, nextPC);
        Assert.Equal(7u, Cpu.D[0]);
        Assert.Equal(3u, Cpu.D[1]);
    }

    // ================================================================
    // X flag preservation
    // ================================================================

    [Fact]
    public void Moveq_PreservesXFlag()
    {
        ResetCpu();
        Cpu.FlagX = true;
        uint addr = 0x1000;
        // MOVEQ #1,D0 — should preserve X flag
        Mem.WriteWord(addr, 0x7001);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.True(Cpu.FlagX); // X preserved by MOVEQ
    }

    [Fact]
    public void AddL_SetsXFlag()
    {
        ResetCpu();
        Cpu.D[0] = 0xFFFFFFFF;
        Cpu.D[1] = 1;
        uint addr = 0x1000;
        // ADD.L D1,D0 — should overflow and set X+C
        Mem.WriteWord(addr, 0xD081);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
        Assert.True(Cpu.FlagC);
        Assert.True(Cpu.FlagX); // ADD sets X = C
    }
}
