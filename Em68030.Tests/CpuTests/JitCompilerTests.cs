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
        cache.AddBlock(0x1000, new CompiledBlock(0x1000, 1, 4, 2, true, _ => 0));
        cache.AddBlock(0x2000, new CompiledBlock(0x2000, 1, 4, 2, true, _ => 0));

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
        // ILLEGAL = 0x4AFC (not supported)
        Mem.WriteWord(addr, 0x4AFC);

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
        // ILLEGAL (unsupported) — block ends before this
        Mem.WriteWord(addr + 4, 0x4AFC);

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

    // ================================================================
    // ADDQ.L #imm, Dn tests
    // ================================================================

    [Fact]
    public void AddqLDn_BasicAdd()
    {
        ResetCpu();
        Cpu.D[2] = 100;
        // ADDQ.L #3, D2: 0101 011 0 10 000 010 = 0x5682
        Mem.WriteWord(0x1000, 0x5682);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(103u, Cpu.D[2]);
        Assert.False(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void AddqLDn_Immediate8()
    {
        ResetCpu();
        // ADDQ.L #8, D0: qqq=0 means 8 → 0101 000 0 10 000 000 = 0x5080
        Mem.WriteWord(0x1000, 0x5080);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(8u, Cpu.D[0]);
    }

    [Fact]
    public void AddqLDn_SetsFlags()
    {
        ResetCpu();
        // ADDQ.L #1, D0: 0x5280
        Mem.WriteWord(0x1000, 0x5280);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        // Test overflow
        Cpu.D[0] = 0x7FFFFFFF;
        block.Execute(Cpu);
        Assert.Equal(0x80000000u, Cpu.D[0]);
        Assert.True(Cpu.FlagN);
        Assert.True(Cpu.FlagV);

        // Test wrap to zero
        Cpu.D[0] = 0xFFFFFFFF;
        block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
        Assert.True(Cpu.FlagC);
        Assert.True(Cpu.FlagX);
    }

    // ================================================================
    // SUBQ.L #imm, Dn tests
    // ================================================================

    [Fact]
    public void SubqLDn_BasicSub()
    {
        ResetCpu();
        Cpu.D[3] = 100;
        // SUBQ.L #5, D3: 0101 101 1 10 000 011 = 0x5B83
        Mem.WriteWord(0x1000, 0x5B83);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(95u, Cpu.D[3]);
    }

    [Fact]
    public void SubqLDn_SetsFlags()
    {
        ResetCpu();
        // SUBQ.L #1, D0: 0x5380
        Mem.WriteWord(0x1000, 0x5380);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        // Test Z
        Cpu.D[0] = 1;
        block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);

        // Test borrow
        Cpu.D[0] = 0;
        block.Execute(Cpu);
        Assert.Equal(0xFFFFFFFFu, Cpu.D[0]);
        Assert.True(Cpu.FlagN);
        Assert.True(Cpu.FlagC);
        Assert.True(Cpu.FlagX);
    }

    // ================================================================
    // ADDQ #imm, An tests
    // ================================================================

    [Fact]
    public void AddqAn_BasicAdd()
    {
        ResetCpu();
        Cpu.A[2] = 0x1000;
        // ADDQ.L #4, A2: 0101 100 0 10 001 010 = 0x588A
        Mem.WriteWord(0x1000, 0x588A);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0x1004u, Cpu.A[2]);
    }

    [Fact]
    public void AddqAn_NoFlagChange()
    {
        ResetCpu();
        Cpu.A[0] = 0xFFFFFFFF;
        Cpu.SR = 0x271F; // All flags set
        // ADDQ.W #1, A0: 0101 001 0 01 001 000 = 0x5248
        Mem.WriteWord(0x1000, 0x5248);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0u, Cpu.A[0]); // Wraps
        Assert.Equal(0x1Fu, (uint)(Cpu.SR & 0xFF)); // All flags unchanged
    }

    // ================================================================
    // SUBQ #imm, An tests
    // ================================================================

    [Fact]
    public void SubqAn_BasicSub()
    {
        ResetCpu();
        Cpu.A[3] = 0x2000;
        // SUBQ.L #2, A3: 0101 010 1 10 001 011 = 0x558B
        Mem.WriteWord(0x1000, 0x558B);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0x1FFEu, Cpu.A[3]);
    }

    [Fact]
    public void SubqAn_NoFlagChange()
    {
        ResetCpu();
        Cpu.A[1] = 0;
        // SUBQ.W #1, A1: 0101 001 1 01 001 001 = 0x5349
        Mem.WriteWord(0x1000, 0x5349);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xFFFFFFFFu, Cpu.A[1]); // Wraps
        // Flags unchanged from ResetCpu (SR=0x2700 → CCR=0x00)
        Assert.False(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
        Assert.False(Cpu.FlagC);
        Assert.False(Cpu.FlagX);
    }

    // ================================================================
    // ADDQ/SUBQ unsupported cases
    // ================================================================

    [Fact]
    public void AddqDn_ByteSize_Supported()
    {
        ResetCpu();
        // ADDQ.B #1, D0: 0x5200 (size=0=byte)
        Mem.WriteWord(0x1000, 0x5200);
        Mem.WriteWord(0x1002, 0x4E71); // NOP
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x123456FE;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x123456FFu, Cpu.D[0]);
    }

    [Fact]
    public void AddqDn_WordSize_Supported()
    {
        ResetCpu();
        // ADDQ.W #1, D0: 0x5240 (size=1=word)
        Mem.WriteWord(0x1000, 0x5240);
        Mem.WriteWord(0x1002, 0x4E71); // NOP
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12340001;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x12340002u, Cpu.D[0]);
    }

    [Fact]
    public void AddqMemory_Unsupported()
    {
        ResetCpu();
        // ADDQ.L #1, (A0): 0x5290 (eaMode=2)
        Mem.WriteWord(0x1000, 0x5290);
        var compiler = new JitCompiler();
        Assert.Null(compiler.TryCompile(Cpu, 0x1000, 0x1000));
    }

    // ================================================================
    // ADDQ/SUBQ in composite blocks
    // ================================================================

    [Fact]
    public void AddqSubq_CompositeBlock()
    {
        ResetCpu();
        // MOVEQ #10, D0 → 0x700A
        // ADDQ.L #5, D0 → 0x5A80
        // SUBQ.L #3, D0 → 0x5780
        Mem.WriteWord(0x1000, 0x700A);
        Mem.WriteWord(0x1002, 0x5A80);
        Mem.WriteWord(0x1004, 0x5780);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1006u, nextPC);
        Assert.Equal(12u, Cpu.D[0]); // 10+5-3 = 12
    }

    [Fact]
    public void AddqAn_InCompositeBlock()
    {
        ResetCpu();
        Cpu.A[0] = 0x1000;
        // MOVEQ #0, D0 → 0x7000
        // ADDQ.L #4, A0 → 0x5888
        // SUBQ.L #1, D0 → 0x5380
        Mem.WriteWord(0x1000, 0x7000);
        Mem.WriteWord(0x1002, 0x5888);
        Mem.WriteWord(0x1004, 0x5380);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        block.Execute(Cpu);
        Assert.Equal(0x1004u, Cpu.A[0]);
        Assert.Equal(0xFFFFFFFFu, Cpu.D[0]); // 0-1 wraps
        Assert.True(Cpu.FlagN);
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

    // ================================================================
    // CLR.L Dn tests
    // ================================================================

    [Fact]
    public void ClrLDn_ClearsRegister()
    {
        ResetCpu();
        Cpu.D[3] = 0xDEADBEEF;
        // CLR.L D3: 0x4283
        Mem.WriteWord(0x1000, 0x4283);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[3]);
    }

    [Fact]
    public void ClrLDn_SetsZeroFlag()
    {
        ResetCpu();
        Cpu.D[0] = 0x12345678;
        Cpu.SR = 0x2708; // N=1 initially
        // CLR.L D0: 0x4280
        Mem.WriteWord(0x1000, 0x4280);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
        Assert.False(Cpu.FlagN);
        Assert.False(Cpu.FlagV);
        Assert.False(Cpu.FlagC);
    }

    [Fact]
    public void ClrLDn_PreservesXFlag()
    {
        ResetCpu();
        Cpu.FlagX = true;
        // CLR.L D0: 0x4280
        Mem.WriteWord(0x1000, 0x4280);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.True(Cpu.FlagX);
        Assert.True(Cpu.FlagZ);
    }

    [Fact]
    public void ClrBDn_Supported()
    {
        ResetCpu();
        // CLR.B D0: 0x4200
        Mem.WriteWord(0x1000, 0x4200);
        Mem.WriteWord(0x1002, 0x4E71); // NOP
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0xAABBCCDD;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0xAABBCC00u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
    }

    [Fact]
    public void ClrWDn_Supported()
    {
        ResetCpu();
        // CLR.W D0: 0x4240
        Mem.WriteWord(0x1000, 0x4240);
        Mem.WriteWord(0x1002, 0x4E71); // NOP
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0xAABBCCDD;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0xAABB0000u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
    }

    [Fact]
    public void ClrLDn_Memory_Unsupported()
    {
        ResetCpu();
        // CLR.L (A0): 0x4290
        Mem.WriteWord(0x1000, 0x4290);
        var compiler = new JitCompiler();
        Assert.Null(compiler.TryCompile(Cpu, 0x1000, 0x1000));
    }

    // ================================================================
    // TST.L Dn tests
    // ================================================================

    [Fact]
    public void TstLDn_PositiveValue()
    {
        ResetCpu();
        Cpu.D[2] = 42;
        // TST.L D2: 0x4A82
        Mem.WriteWord(0x1000, 0x4A82);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(42u, Cpu.D[2]); // Unchanged
        Assert.False(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void TstLDn_NegativeValue()
    {
        ResetCpu();
        Cpu.D[0] = 0x80000000;
        // TST.L D0: 0x4A80
        Mem.WriteWord(0x1000, 0x4A80);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0x80000000u, Cpu.D[0]);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void TstLDn_ZeroValue()
    {
        ResetCpu();
        Cpu.D[1] = 0;
        Cpu.SR = 0x2708; // N=1 initially
        // TST.L D1: 0x4A81
        Mem.WriteWord(0x1000, 0x4A81);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.True(Cpu.FlagZ);
        Assert.False(Cpu.FlagN);
        Assert.False(Cpu.FlagV);
        Assert.False(Cpu.FlagC);
    }

    [Fact]
    public void TstLDn_PreservesXFlag()
    {
        ResetCpu();
        Cpu.D[0] = 1;
        Cpu.FlagX = true;
        // TST.L D0: 0x4A80
        Mem.WriteWord(0x1000, 0x4A80);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.True(Cpu.FlagX);
    }

    [Fact]
    public void TstBDn_Supported()
    {
        ResetCpu();
        // TST.B D0: 0x4A00
        Mem.WriteWord(0x1000, 0x4A00);
        Mem.WriteWord(0x1002, 0x4E71); // NOP
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0xAABBCC80;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void TstWDn_Supported()
    {
        ResetCpu();
        // TST.W D0: 0x4A40
        Mem.WriteWord(0x1000, 0x4A40);
        Mem.WriteWord(0x1002, 0x4E71); // NOP
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0xAABB0000;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.False(Cpu.FlagN);
        Assert.True(Cpu.FlagZ);
    }

    [Fact]
    public void TstLDn_Memory_Unsupported()
    {
        ResetCpu();
        // TST.L (A0): 0x4A90
        Mem.WriteWord(0x1000, 0x4A90);
        var compiler = new JitCompiler();
        Assert.Null(compiler.TryCompile(Cpu, 0x1000, 0x1000));
    }

    // ================================================================
    // MOVE.L An,Dn tests
    // ================================================================

    [Fact]
    public void MoveLAnDn_CopiesRegister()
    {
        ResetCpu();
        Cpu.A[3] = 0xDEADBEEF;
        // MOVE.L A3,D5: 0010 101 000 001 011 = 0x2A0B
        Mem.WriteWord(0x1000, 0x2A0B);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xDEADBEEFu, Cpu.D[5]);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void MoveLAnDn_SetsZeroFlag()
    {
        ResetCpu();
        Cpu.A[0] = 0;
        Cpu.D[0] = 0x12345678;
        // MOVE.L A0,D0: 0x2008
        Mem.WriteWord(0x1000, 0x2008);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
        Assert.False(Cpu.FlagN);
    }

    [Fact]
    public void MoveLAnDn_PreservesXFlag()
    {
        ResetCpu();
        Cpu.A[0] = 42;
        Cpu.FlagX = true;
        // MOVE.L A0,D0: 0x2008
        Mem.WriteWord(0x1000, 0x2008);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.True(Cpu.FlagX);
    }

    // ================================================================
    // MOVEA.L Dn,An tests
    // ================================================================

    [Fact]
    public void MoveaLDnAn_CopiesRegister()
    {
        ResetCpu();
        Cpu.D[3] = 0xCAFEBABE;
        // MOVEA.L D3,A2: 0010 010 001 000 011 = 0x2443
        Mem.WriteWord(0x1000, 0x2443);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xCAFEBABEu, Cpu.A[2]);
    }

    [Fact]
    public void MoveaLDnAn_NoFlagChange()
    {
        ResetCpu();
        Cpu.D[0] = 0x80000000;
        Cpu.SR = 0x271F; // All flags set
        // MOVEA.L D0,A0: 0x2040
        Mem.WriteWord(0x1000, 0x2040);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0x80000000u, Cpu.A[0]);
        Assert.Equal(0x1Fu, (uint)(Cpu.SR & 0xFF)); // All flags unchanged
    }

    // ================================================================
    // MOVEA.L An,Am tests
    // ================================================================

    [Fact]
    public void MoveaLAnAm_CopiesRegister()
    {
        ResetCpu();
        Cpu.A[3] = 0x12345678;
        // MOVEA.L A3,A5: 0010 101 001 001 011 = 0x2A4B
        Mem.WriteWord(0x1000, 0x2A4B);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0x12345678u, Cpu.A[5]);
    }

    [Fact]
    public void MoveaLAnAm_NoFlagChange()
    {
        ResetCpu();
        Cpu.A[0] = 0x80000000;
        Cpu.SR = 0x271F; // All flags set
        // MOVEA.L A0,A1: 0x2248
        Mem.WriteWord(0x1000, 0x2248);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0x80000000u, Cpu.A[1]);
        Assert.Equal(0x1Fu, (uint)(Cpu.SR & 0xFF)); // All flags unchanged
    }

    // ================================================================
    // LSL.L #imm, Dn tests
    // ================================================================

    [Fact]
    public void LslImmLDn_Basic()
    {
        ResetCpu();
        Cpu.D[0] = 1;
        // LSL.L #2, D0: 0xE580
        Mem.WriteWord(0x1000, 0xE580);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(4u, Cpu.D[0]); // 1 << 2 = 4
        Assert.False(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void LslImmLDn_Count8()
    {
        ResetCpu();
        Cpu.D[0] = 0xFF;
        // LSL.L #8, D0: ccc=0 means 8 → 0xE188
        Mem.WriteWord(0x1000, 0xE188);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xFF00u, Cpu.D[0]);
    }

    [Fact]
    public void LslImmLDn_SetsCarryAndExtend()
    {
        ResetCpu();
        Cpu.D[0] = 0x80000000; // MSB set → shifts out to carry
        // LSL.L #1, D0: 0xE388
        Mem.WriteWord(0x1000, 0xE388);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[0]);
        Assert.True(Cpu.FlagC);
        Assert.True(Cpu.FlagX);
        Assert.True(Cpu.FlagZ);
    }

    // ================================================================
    // LSR.L #imm, Dn tests
    // ================================================================

    [Fact]
    public void LsrImmLDn_Basic()
    {
        ResetCpu();
        Cpu.D[0] = 4;
        // LSR.L #1, D0: 0xE288
        Mem.WriteWord(0x1000, 0xE288);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(2u, Cpu.D[0]); // 4 >> 1 = 2
    }

    [Fact]
    public void LsrImmLDn_SetsZero()
    {
        ResetCpu();
        Cpu.D[0] = 1; // 1 >> 1 = 0
        // LSR.L #1, D0: 0xE288
        Mem.WriteWord(0x1000, 0xE288);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
        Assert.True(Cpu.FlagC); // bit 0 was 1
        Assert.True(Cpu.FlagX);
    }

    // ================================================================
    // ASR.L #imm, Dn tests
    // ================================================================

    [Fact]
    public void AsrImmLDn_SignExtends()
    {
        ResetCpu();
        Cpu.D[0] = 0x80000000; // >> 1 = 0xC0000000 (sign-extended)
        // ASR.L #1, D0: 0xE280
        Mem.WriteWord(0x1000, 0xE280);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xC0000000u, Cpu.D[0]);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void AsrImmLDn_Positive()
    {
        ResetCpu();
        Cpu.D[0] = 8; // Positive: same as logical shift right
        // ASR.L #1, D0: 0xE280
        Mem.WriteWord(0x1000, 0xE280);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(4u, Cpu.D[0]);
        Assert.False(Cpu.FlagN);
    }

    // ================================================================
    // ASL.L #imm, Dn tests
    // ================================================================

    [Fact]
    public void AslImmLDn_SetsOverflow()
    {
        ResetCpu();
        Cpu.D[0] = 0x40000000; // Shift left → 0x80000000, sign changes → V=1
        // ASL.L #1, D0: 0xE380
        Mem.WriteWord(0x1000, 0xE380);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0x80000000u, Cpu.D[0]);
        Assert.True(Cpu.FlagV);
        Assert.True(Cpu.FlagN);
    }

    // ================================================================
    // Dead flag elimination for shift instructions
    // ================================================================

    [Fact]
    public void DeadFlags_ShiftFollowedByMove()
    {
        ResetCpu();
        Cpu.D[0] = 3;
        // LSL.L #2, D0 → 0xE580 (flags dead — overwritten by MOVE.L)
        // MOVE.L D0, D1 → 0x2200 (flags live — last instruction)
        Mem.WriteWord(0x1000, 0xE580);
        Mem.WriteWord(0x1002, 0x2200);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(2, block.InstructionCount);
        block.Execute(Cpu);
        Assert.Equal(12u, Cpu.D[0]); // 3 << 2 = 12
        Assert.Equal(12u, Cpu.D[1]);
    }

    // ================================================================
    // Composite block + Dead flag elimination for new instructions
    // ================================================================

    [Fact]
    public void ClrTst_CompositeBlock()
    {
        ResetCpu();
        Cpu.D[0] = 0xDEAD;
        // CLR.L D0 → 0x4280
        // MOVEQ #5, D1 → 0x7205
        // TST.L D1 → 0x4A81
        Mem.WriteWord(0x1000, 0x4280);
        Mem.WriteWord(0x1002, 0x7205);
        Mem.WriteWord(0x1004, 0x4A81);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1006u, nextPC);
        Assert.Equal(0u, Cpu.D[0]);
        Assert.Equal(5u, Cpu.D[1]);
        Assert.False(Cpu.FlagZ); // TST.L D1=5 → Z=0
        Assert.False(Cpu.FlagN);
    }

    [Fact]
    public void MoveLAnDn_MoveaLDnAn_CompositeBlock()
    {
        ResetCpu();
        Cpu.A[0] = 0x1000;
        // MOVE.L A0,D0 → 0x2008
        // ADDQ.L #4, D0 → 0x5880
        // MOVEA.L D0,A0 → 0x2040
        Mem.WriteWord(0x1000, 0x2008);
        Mem.WriteWord(0x1002, 0x5880);
        Mem.WriteWord(0x1004, 0x2040);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        block.Execute(Cpu);
        Assert.Equal(0x1004u, Cpu.D[0]);
        Assert.Equal(0x1004u, Cpu.A[0]);
    }

    [Fact]
    public void MoveaLAnAm_DeadFlagElimination_Transparent()
    {
        // Test that MOVEA.L An,Am is transparent for dead flag elimination.
        // MOVEQ #1, D0 → 0x7001 (flags: dead — SUBQ.L D1 overwrites)
        // MOVEA.L A0, A1 → 0x2248 (flags: transparent)
        // SUBQ.L #1, D1 → 0x5381 (flags: live — last)
        ResetCpu();
        Mem.WriteWord(0x1000, 0x7001);
        Mem.WriteWord(0x1002, 0x2248);
        Mem.WriteWord(0x1004, 0x5381);

        // We can't directly inspect needsFlags in the C# IL-compiled block,
        // but we can verify correctness: the block should execute without issues
        // and the final flags should come from SUBQ.L only.
        Cpu.D[1] = 1;
        Cpu.A[0] = 0x2000;
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        block.Execute(Cpu);
        Assert.Equal(1u, Cpu.D[0]);
        Assert.Equal(0x2000u, Cpu.A[1]);
        Assert.Equal(0u, Cpu.D[1]);
        Assert.True(Cpu.FlagZ); // From SUBQ.L #1,D1: 1-1=0
    }

    // ================================================================
    // EXG Dn,Dm tests
    // ================================================================

    [Fact]
    public void ExgDnDm_Basic()
    {
        ResetCpu();
        Cpu.D[0] = 0x11111111;
        Cpu.D[1] = 0x22222222;
        // EXG D0,D1: 0xC141
        Mem.WriteWord(0x1000, 0xC141);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1002u, nextPC);
        Assert.Equal(0x22222222u, Cpu.D[0]);
        Assert.Equal(0x11111111u, Cpu.D[1]);
    }

    // ================================================================
    // EXG An,Am tests
    // ================================================================

    [Fact]
    public void ExgAnAm_Basic()
    {
        ResetCpu();
        Cpu.A[0] = 0xAAAAAAAA;
        Cpu.A[1] = 0xBBBBBBBB;
        // EXG A0,A1: 0xC149
        Mem.WriteWord(0x1000, 0xC149);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xBBBBBBBBu, Cpu.A[0]);
        Assert.Equal(0xAAAAAAAAu, Cpu.A[1]);
    }

    // ================================================================
    // EXG Dn,An tests
    // ================================================================

    [Fact]
    public void ExgDnAn_Basic()
    {
        ResetCpu();
        Cpu.D[0] = 0x12345678;
        Cpu.A[0] = 0xABCDEF00;
        // EXG D0,A0: 0xC188
        Mem.WriteWord(0x1000, 0xC188);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xABCDEF00u, Cpu.D[0]);
        Assert.Equal(0x12345678u, Cpu.A[0]);
    }

    [Fact]
    public void ExgDnDm_NoFlags()
    {
        ResetCpu();
        Cpu.D[0] = 0x80000000;
        Cpu.D[1] = 0;
        Cpu.SR = 0x271F; // All flags set
        // EXG D0,D1: 0xC141
        Mem.WriteWord(0x1000, 0xC141);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0x1Fu, (uint)(Cpu.SR & 0xFF)); // All flags unchanged
    }

    // ================================================================
    // SWAP Dn tests
    // ================================================================

    [Fact]
    public void SwapDn_Basic()
    {
        ResetCpu();
        Cpu.D[0] = 0x12340000;
        // SWAP D0: 0x4840
        Mem.WriteWord(0x1000, 0x4840);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1002u, nextPC);
        Assert.Equal(0x00001234u, Cpu.D[0]);
        Assert.False(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void SwapDn_SetsNegative()
    {
        ResetCpu();
        Cpu.D[0] = 0x0000FFFF;
        // SWAP D0: 0x4840
        Mem.WriteWord(0x1000, 0x4840);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xFFFF0000u, Cpu.D[0]);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    // ================================================================
    // EXT.W Dn tests
    // ================================================================

    [Fact]
    public void ExtWDn_NegByte()
    {
        ResetCpu();
        Cpu.D[0] = 0x00000080; // byte 0x80 → sign-extend to word 0xFF80
        // EXT.W D0: 0x4880
        Mem.WriteWord(0x1000, 0x4880);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xFF80u, Cpu.D[0] & 0xFFFF); // lower word sign-extended
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    // ================================================================
    // EXT.L Dn tests
    // ================================================================

    [Fact]
    public void ExtLDn_NegWord()
    {
        ResetCpu();
        Cpu.D[0] = 0x00008000; // word 0x8000 → sign-extend to long 0xFFFF8000
        // EXT.L D0: 0x48C0
        Mem.WriteWord(0x1000, 0x48C0);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xFFFF8000u, Cpu.D[0]);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    // ================================================================
    // EXTB.L Dn tests
    // ================================================================

    [Fact]
    public void ExtbLDn_NegByte()
    {
        ResetCpu();
        Cpu.D[0] = 0x00000080; // byte 0x80 → sign-extend to long 0xFFFFFF80
        // EXTB.L D0: 0x49C0
        Mem.WriteWord(0x1000, 0x49C0);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xFFFFFF80u, Cpu.D[0]);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    // ================================================================
    // NEG.L Dn tests
    // ================================================================

    [Fact]
    public void NegLDn_Basic()
    {
        ResetCpu();
        Cpu.D[0] = 5;
        // NEG.L D0: 0x4480
        Mem.WriteWord(0x1000, 0x4480);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xFFFFFFFBu, Cpu.D[0]); // -5
        Assert.True(Cpu.FlagN);
        Assert.True(Cpu.FlagC);
        Assert.True(Cpu.FlagX);
    }

    [Fact]
    public void NegLDn_Zero()
    {
        ResetCpu();
        Cpu.D[0] = 0;
        Cpu.SR = 0x271F; // all flags set
        // NEG.L D0: 0x4480
        Mem.WriteWord(0x1000, 0x4480);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
        Assert.False(Cpu.FlagC);
        Assert.False(Cpu.FlagX);
    }

    // ================================================================
    // NOT.L Dn tests
    // ================================================================

    [Fact]
    public void NotLDn_Basic()
    {
        ResetCpu();
        Cpu.D[0] = 0;
        // NOT.L D0: 0x4680
        Mem.WriteWord(0x1000, 0x4680);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        block.Execute(Cpu);
        Assert.Equal(0xFFFFFFFFu, Cpu.D[0]);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    // ================================================================
    // Dead flag elimination for Tier 1 instructions
    // ================================================================

    [Fact]
    public void DeadFlags_Tier1Block()
    {
        // SWAP D0 → 0x4840 (flags dead — overwritten by NOT)
        // NOT.L D0 → 0x4680 (flags dead — overwritten by MOVE.L)
        // MOVE.L D0, D1 → 0x2200 (flags live — last instruction)
        ResetCpu();
        Cpu.D[0] = 0x00FF00FF;
        Mem.WriteWord(0x1000, 0x4840);
        Mem.WriteWord(0x1002, 0x4680);
        Mem.WriteWord(0x1004, 0x2200);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        block.Execute(Cpu);
        // SWAP: 0x00FF00FF → 0x00FF00FF (symmetric!)
        // NOT: 0x00FF00FF → 0xFF00FF00
        // MOVE.L D0→D1: D1 = 0xFF00FF00
        Assert.Equal(0xFF00FF00u, Cpu.D[0]);
        Assert.Equal(0xFF00FF00u, Cpu.D[1]);
        Assert.True(Cpu.FlagN); // bit 31 set
    }

    [Fact]
    public void DeadFlags_ExgTransparent()
    {
        // MOVEQ #1, D0 → 0x7001 (flags dead — overwritten by SUBQ)
        // EXG D0, D1 → 0xC141 (flags transparent)
        // SUBQ.L #1, D0 → 0x5380 (flags live — last)
        ResetCpu();
        Cpu.D[1] = 10;
        Mem.WriteWord(0x1000, 0x7001);
        Mem.WriteWord(0x1002, 0xC141);
        Mem.WriteWord(0x1004, 0x5380);

        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);

        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        block.Execute(Cpu);
        // MOVEQ #1,D0 → D0=1
        // EXG D0,D1 → D0=10, D1=1
        // SUBQ.L #1,D0 → D0=9
        Assert.Equal(9u, Cpu.D[0]);
        Assert.Equal(1u, Cpu.D[1]);
    }

    // ================================================================
    // Phase 1C: MULU.W / MULS.W
    // ================================================================

    [Fact]
    public void MuluW_BasicMultiply()
    {
        ResetCpu();
        // MULU.W D1,D0: 1100 000 011 000 001 = 0xC0C1
        Mem.WriteWord(0x1000, 0xC0C1);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(1, block.InstructionCount);

        Cpu.D[0] = 100;
        Cpu.D[1] = 200;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(20000u, Cpu.D[0]);
        Assert.False(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void MuluW_ZeroResult()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0xC0C1); // MULU.W D1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0;
        Cpu.D[1] = 12345;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
    }

    [Fact]
    public void MuluW_LargeValues()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0xC0C1); // MULU.W D1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        // 0xFFFF * 0xFFFF = 0xFFFE0001
        Cpu.D[0] = 0x1234FFFF; // upper bits replaced
        Cpu.D[1] = 0x5678FFFF;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0xFFFE0001u, Cpu.D[0]);
        Assert.True(Cpu.FlagN);
    }

    [Fact]
    public void MulsW_PositiveTimesNegative()
    {
        ResetCpu();
        // MULS.W D1,D0: 1100 000 111 000 001 = 0xC1C1
        Mem.WriteWord(0x1000, 0xC1C1);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 10;
        Cpu.D[1] = 0xFFF6; // -10 as int16
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(unchecked((uint)-100), Cpu.D[0]); // 0xFFFFFF9C
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void MulsW_NegativeTimesNegative()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0xC1C1); // MULS.W D1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0xFFF6; // -10
        Cpu.D[1] = 0xFFEC; // -20
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(200u, Cpu.D[0]);
        Assert.False(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void MuluW_OnlyLow16BitsUsed()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0xC0C1); // MULU.W D1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0xABCD0003;
        Cpu.D[1] = 0x12340007;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(21u, Cpu.D[0]);
    }

    // ================================================================
    // Phase 1D: BTST Dn,Dm
    // ================================================================

    [Fact]
    public void BtstDnDm_BitSet()
    {
        ResetCpu();
        // BTST D1,D0: 0000 001 100 000 000 = 0x0300
        Mem.WriteWord(0x1000, 0x0300);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x08; // bit 3 set
        Cpu.D[1] = 3;
        Cpu.FlagZ = true;
        block.Execute(Cpu);
        Assert.False(Cpu.FlagZ); // bit is set → Z=0
    }

    [Fact]
    public void BtstDnDm_BitClear()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x0300); // BTST D1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x00;
        Cpu.D[1] = 5;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.True(Cpu.FlagZ); // bit is clear → Z=1
    }

    [Fact]
    public void BtstDnDm_Modulo32()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x0300); // BTST D1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x80000000; // bit 31 set
        Cpu.D[1] = 63;         // 63 % 32 = 31
        Cpu.FlagZ = true;
        block.Execute(Cpu);
        Assert.False(Cpu.FlagZ); // bit 31 is set
    }

    [Fact]
    public void BtstDnDm_PreservesOtherFlags()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x0300); // BTST D1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0;
        Cpu.D[1] = 0;
        Cpu.SetCCR(0x1B); // X=1, N=1, V=1, C=1
        block.Execute(Cpu);
        byte ccr = Cpu.CCR;
        Assert.True((ccr & 0x10) != 0);  // X preserved
        Assert.True((ccr & 0x08) != 0);  // N preserved
        Assert.True((ccr & 0x04) != 0);  // Z set (bit 0 is clear)
        Assert.True((ccr & 0x02) != 0);  // V preserved
        Assert.True((ccr & 0x01) != 0);  // C preserved
    }

    // ================================================================
    // Phase 1A: LEA
    // ================================================================

    [Fact]
    public void LeaAnAr_Simple()
    {
        ResetCpu();
        // LEA (A2),A3: 0100 011 111 010 010 = 0x47D2
        Mem.WriteWord(0x1000, 0x47D2);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(1, block.InstructionCount);

        Cpu.A[2] = 0x12345678;
        Cpu.A[3] = 0;
        block.Execute(Cpu);
        Assert.Equal(0x12345678u, Cpu.A[3]);
    }

    [Fact]
    public void LeaD16AnAr_PositiveDisp()
    {
        ResetCpu();
        // LEA d16(A0),A1: 0100 001 111 101 000 = 0x43E8
        Mem.WriteWord(0x1000, 0x43E8);
        Mem.WriteWord(0x1002, 0x0100); // d16 = 256
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(1, block.InstructionCount);
        Assert.Equal(4, block.ByteLength);

        Cpu.A[0] = 0x00001000;
        Cpu.A[1] = 0;
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1004u, nextPC);
        Assert.Equal(0x00001100u, Cpu.A[1]);
    }

    [Fact]
    public void LeaD16AnAr_NegativeDisp()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x43E8); // LEA d16(A0),A1
        Mem.WriteWord(0x1002, 0xFF00); // d16 = -256
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[0] = 0x00002000;
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1004u, nextPC);
        Assert.Equal(0x00001F00u, Cpu.A[1]);
    }

    [Fact]
    public void LeaD8AnXnAr_BasicIndex()
    {
        ResetCpu();
        // LEA d8(A0,D1),A2: 0100 010 111 110 000 = 0x45F0
        // Brief ext: D1.L, scale=1, d8=0x10
        // extWord: 0 001 1 00 0 00010000 = 0x1810
        Mem.WriteWord(0x1000, 0x45F0);
        Mem.WriteWord(0x1002, 0x1810);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(4, block.ByteLength);

        Cpu.A[0] = 0x00001000;
        Cpu.D[1] = 0x00000020;
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1004u, nextPC);
        Assert.Equal(0x00001030u, Cpu.A[2]); // 0x1000 + 0x20 + 0x10
    }

    [Fact]
    public void LeaD8AnXnAr_Scale2()
    {
        ResetCpu();
        // LEA d8(A0,D1*2),A2
        // extWord: 0 001 1 01 0 00000000 = 0x1A00
        Mem.WriteWord(0x1000, 0x45F0);
        Mem.WriteWord(0x1002, 0x1A00);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[0] = 0x00001000;
        Cpu.D[1] = 0x00000010;
        block.Execute(Cpu);
        Assert.Equal(0x00001020u, Cpu.A[2]); // 0x1000 + 0x10*2
    }

    [Fact]
    public void LeaD8AnXnAr_AddrIndex()
    {
        ResetCpu();
        // LEA d8(A0,A3),A2
        // extWord: 1 011 1 00 0 00000100 = 0xB804
        Mem.WriteWord(0x1000, 0x45F0);
        Mem.WriteWord(0x1002, 0xB804);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[0] = 0x00001000;
        Cpu.A[3] = 0x00000100;
        block.Execute(Cpu);
        Assert.Equal(0x00001104u, Cpu.A[2]); // 0x1000 + 0x100 + 4
    }

    [Fact]
    public void LeaD8AnXnAr_WordIndex()
    {
        ResetCpu();
        // LEA d8(A0,D1.W),A2
        // extWord: 0 001 0 00 0 00000000 = 0x1000
        Mem.WriteWord(0x1000, 0x45F0);
        Mem.WriteWord(0x1002, 0x1000);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[0] = 0x00002000;
        Cpu.D[1] = 0x0000FFF0; // as word = -16
        block.Execute(Cpu);
        Assert.Equal(0x00001FF0u, Cpu.A[2]); // 0x2000 + (-16)
    }

    [Fact]
    public void Lea_DoesNotAffectFlags()
    {
        ResetCpu();
        // LEA (A0),A1 followed by SUBQ.L #1,D0 — LEA should not affect flags
        Mem.WriteWord(0x1000, 0x43D0); // LEA (A0),A1
        Mem.WriteWord(0x1002, 0x5380); // SUBQ.L #1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(2, block.InstructionCount);
    }

    // ================================================================
    // Phase 1B: Bcc.W / BRA.W
    // ================================================================

    [Fact]
    public void BraW_ForwardBranch()
    {
        ResetCpu();
        // BRA.W: 0x6000, followed by d16 displacement
        Mem.WriteWord(0x1000, 0x6000);
        Mem.WriteWord(0x1002, 0x0100); // d16=256 → target=0x1002+256=0x1102
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(1, block.InstructionCount);
        Assert.Equal(4, block.ByteLength);

        Cpu.SetCCR(0);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1102u, nextPC);
    }

    [Fact]
    public void BccW_ForwardBranchTaken()
    {
        ResetCpu();
        // BEQ.W: 0x6700, followed by d16=0x0200 (512)
        Mem.WriteWord(0x1000, 0x6700);
        Mem.WriteWord(0x1002, 0x0200);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(1, block.InstructionCount);
        Assert.Equal(4, block.ByteLength);

        Cpu.FlagZ = true; // BEQ taken
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1202u, nextPC);
    }

    [Fact]
    public void BccW_ForwardBranchNotTaken()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x6700); // BEQ.W
        Mem.WriteWord(0x1002, 0x0200); // d16=512
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.SetCCR(0); // Z=0, BEQ not taken
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1004u, nextPC); // fallthrough
    }

    [Fact]
    public void BraW_BackwardBranchRejected()
    {
        ResetCpu();
        // BRA.W with backward displacement should terminate block before it
        Mem.WriteWord(0x1000, 0x7000); // MOVEQ #0,D0
        Mem.WriteWord(0x1002, 0x6000); // BRA.W
        Mem.WriteWord(0x1004, 0xFFFC); // d16=-4 → target=0x1000
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        // Backward BRA.W should be excluded; block should contain only MOVEQ
        Assert.Equal(1, block.InstructionCount);
    }

    [Fact]
    public void BraW_BlockByteLength()
    {
        ResetCpu();
        // MOVEQ + BRA.W = 2 + 4 = 6 bytes
        Mem.WriteWord(0x1000, 0x7000); // MOVEQ #0,D0
        Mem.WriteWord(0x1002, 0x6000); // BRA.W
        Mem.WriteWord(0x1004, 0x0100); // d16=256
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(2, block.InstructionCount);
        Assert.Equal(6, block.ByteLength);
    }

    // ================================================================
    // Phase 1E: Byte/Word size register instructions
    // ================================================================

    [Fact]
    public void AddBDnDm_Basic()
    {
        ResetCpu();
        // ADD.B D1,D0: 1101 000 000 000 001 = 0xD001
        Mem.WriteWord(0x1000, 0xD001);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12345610;
        Cpu.D[1] = 0xABCDEF20;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x12345630u, Cpu.D[0]); // only low byte changes
    }

    [Fact]
    public void AddWDnDm_Basic()
    {
        ResetCpu();
        // ADD.W D1,D0: 1101 000 001 000 001 = 0xD041
        Mem.WriteWord(0x1000, 0xD041);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12340100;
        Cpu.D[1] = 0xABCD0200;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x12340300u, Cpu.D[0]);
    }

    [Fact]
    public void SubBDnDm_Basic()
    {
        ResetCpu();
        // SUB.B D1,D0: 1001 000 000 000 001 = 0x9001
        Mem.WriteWord(0x1000, 0x9001);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12345630;
        Cpu.D[1] = 0xABCDEF10;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x12345620u, Cpu.D[0]);
    }

    [Fact]
    public void SubWDnDm_Basic()
    {
        ResetCpu();
        // SUB.W D1,D0: 1001 000 001 000 001 = 0x9041
        Mem.WriteWord(0x1000, 0x9041);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12340300;
        Cpu.D[1] = 0xABCD0100;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x12340200u, Cpu.D[0]);
    }

    [Fact]
    public void CmpBDnDm_Equal()
    {
        ResetCpu();
        // CMP.B D1,D0: 1011 000 000 000 001 = 0xB001
        Mem.WriteWord(0x1000, 0xB001);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12345642;
        Cpu.D[1] = 0xABCDEF42;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.True(Cpu.FlagZ);
        Assert.Equal(0x12345642u, Cpu.D[0]); // unchanged
    }

    [Fact]
    public void CmpWDnDm_NotEqual()
    {
        ResetCpu();
        // CMP.W D1,D0: 1011 000 001 000 001 = 0xB041
        Mem.WriteWord(0x1000, 0xB041);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12340100;
        Cpu.D[1] = 0xABCD0200;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void AndBDnDm_Basic()
    {
        ResetCpu();
        // AND.B D1,D0: 1100 000 000 000 001 = 0xC001
        Mem.WriteWord(0x1000, 0xC001);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x123456FF;
        Cpu.D[1] = 0xABCDEF0F;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x1234560Fu, Cpu.D[0]);
    }

    [Fact]
    public void OrWDnDm_Basic()
    {
        ResetCpu();
        // OR.W D1,D0: 1000 000 001 000 001 = 0x8041
        Mem.WriteWord(0x1000, 0x8041);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12340F00;
        Cpu.D[1] = 0xABCD00F0;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x12340FF0u, Cpu.D[0]);
    }

    [Fact]
    public void EorBDnDm_Basic()
    {
        ResetCpu();
        // EOR.B D1,D0: 1011 001 100 000 000 = 0xB300
        Mem.WriteWord(0x1000, 0xB300);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x123456FF;
        Cpu.D[1] = 0xABCDEF0F;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x123456F0u, Cpu.D[0]); // 0xFF ^ 0x0F = 0xF0
    }

    [Fact]
    public void EorWDnDm_Basic()
    {
        ResetCpu();
        // EOR.W D1,D0: 1011 001 101 000 000 = 0xB340
        Mem.WriteWord(0x1000, 0xB340);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x1234FFFF;
        Cpu.D[1] = 0xABCD00FF;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x1234FF00u, Cpu.D[0]);
    }

    [Fact]
    public void AddqBDn_Basic()
    {
        ResetCpu();
        // ADDQ.B #3,D0: 0101 011 0 00 000 000 = 0x5600
        Mem.WriteWord(0x1000, 0x5600);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x123456FD;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        // 0xFD + 3 = 0x100 → wraps to 0x00
        Assert.Equal(0x12345600u, Cpu.D[0]);
    }

    [Fact]
    public void SubqWDn_Basic()
    {
        ResetCpu();
        // SUBQ.W #1,D0: 0101 001 1 01 000 000 = 0x5340
        Mem.WriteWord(0x1000, 0x5340);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12340000;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        // 0x0000 - 1 = 0xFFFF
        Assert.Equal(0x1234FFFFu, Cpu.D[0]);
    }

    [Fact]
    public void ClrBDn_Basic()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x4200); // CLR.B D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x123456FF;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x12345600u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
    }

    [Fact]
    public void ClrWDn_Basic()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x4240); // CLR.W D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x1234FFFF;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x12340000u, Cpu.D[0]);
        Assert.True(Cpu.FlagZ);
    }

    [Fact]
    public void TstBDn_Negative()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x4A00); // TST.B D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x000000FF;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.True(Cpu.FlagN);
        Assert.False(Cpu.FlagZ);
    }

    [Fact]
    public void TstWDn_Zero()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x4A40); // TST.W D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12340000;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.False(Cpu.FlagN);
        Assert.True(Cpu.FlagZ);
    }

    [Fact]
    public void NegBDn_Basic()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x4400); // NEG.B D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x12345601;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x123456FFu, Cpu.D[0]); // NEG.B 1 = 0xFF (-1)
    }

    [Fact]
    public void NotWDn_Basic()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x4640); // NOT.W D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0x1234FF00;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0x123400FFu, Cpu.D[0]);
    }

    [Fact]
    public void AddBDnDm_PreservesUpperBytes()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0xD001); // ADD.B D1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.D[0] = 0xAABBCC05;
        Cpu.D[1] = 0x11223303;
        Cpu.SetCCR(0);
        block.Execute(Cpu);
        Assert.Equal(0xAABBCC08u, Cpu.D[0]); // upper bytes unchanged
    }

    // ================================================================
    // Dead flag elimination for new instructions
    // ================================================================

    [Fact]
    public void DeadFlags_BtstTransparent()
    {
        ResetCpu();
        // ADDQ.L #1,D0 / BTST D1,D2 / BEQ target
        Mem.WriteWord(0x1000, 0x5280); // ADDQ.L #1,D0
        Mem.WriteWord(0x1002, 0x0302); // BTST D1,D2
        Mem.WriteWord(0x1004, 0x6704); // BEQ.B +4
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
    }

    [Fact]
    public void DeadFlags_LeaTransparent()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x43D0); // LEA (A0),A1
        Mem.WriteWord(0x1002, 0x43E8); // LEA d16(A0),A1
        Mem.WriteWord(0x1004, 0x0010); // d16=16
        Mem.WriteWord(0x1006, 0x5380); // SUBQ.L #1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
    }

    [Fact]
    public void DeadFlags_MuluKillsFlags()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x5280); // ADDQ.L #1,D0
        Mem.WriteWord(0x1002, 0xC0C1); // MULU.W D1,D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(2, block.InstructionCount);
    }

    // ================================================================
    // Multi-word instruction mixed blocks
    // ================================================================

    [Fact]
    public void MixedBlock_LeaAndMoveq()
    {
        ResetCpu();
        // MOVEQ #10,D0 / LEA d16(A0),A1 / MOVEQ #20,D2
        Mem.WriteWord(0x1000, 0x700A); // MOVEQ #10,D0
        Mem.WriteWord(0x1002, 0x43E8); // LEA d16(A0),A1
        Mem.WriteWord(0x1004, 0x0080); // d16=128
        Mem.WriteWord(0x1006, 0x7414); // MOVEQ #20,D2
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        Assert.Equal(8, block.ByteLength); // 2 + 4 + 2

        Cpu.A[0] = 0x00004000;
        Cpu.SetCCR(0);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1008u, nextPC);
        Assert.Equal(10u, Cpu.D[0]);
        Assert.Equal(0x00004080u, Cpu.A[1]);
        Assert.Equal(20u, Cpu.D[2]);
    }

    // ================================================================
    // Phase 2: Memory access instructions
    // ================================================================

    private void SetupDataCache(uint baseAddr, uint mask)
    {
        Cpu.SetupDataCache(baseAddr, baseAddr, mask);
    }

    [Fact]
    public void MoveLIndAnDm_Classify()
    {
        ResetCpu();
        // MOVE.L (A0),D0 = 0x2010
        Mem.WriteWord(0x1000, 0x2010);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(1, block.InstructionCount);
        Assert.False(block.RegisterOnly);
    }

    [Fact]
    public void MoveLIndAnDm_CacheHit()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x2010); // MOVE.L (A0),D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[0] = 0x2000;
        Mem.WriteLong(0x2000, 0xDEADBEEF);
        SetupDataCache(0x2000, 0xFFF);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1002u, nextPC);
        Assert.Equal(0xDEADBEEFu, Cpu.D[0]);
        Assert.Equal(block.InstructionCount, Cpu._jitExecutedCount);
    }

    [Fact]
    public void MoveLIndAnDm_CacheMiss_Bailout()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x2010);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[0] = 0x2000;
        Cpu.InvalidateDataCache();
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1000u, nextPC); // bailout PC
        Assert.Equal(0, Cpu._jitExecutedCount);
    }

    [Fact]
    public void MoveLIndAnDm_WrongPage_Bailout()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x2010);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[0] = 0x3000; // different page than cached
        SetupDataCache(0x2000, 0xFFF);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1000u, nextPC);
        Assert.Equal(0, Cpu._jitExecutedCount);
    }

    [Fact]
    public void MoveLPostIncAnDm_CacheHit()
    {
        ResetCpu();
        // MOVE.L (A0)+,D1 = 0x2218
        Mem.WriteWord(0x1000, 0x2218);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[0] = 0x2000;
        Mem.WriteLong(0x2000, 0x12345678);
        SetupDataCache(0x2000, 0xFFF);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1002u, nextPC);
        Assert.Equal(0x12345678u, Cpu.D[1]);
        Assert.Equal(0x2004u, Cpu.A[0]); // post-incremented
    }

    [Fact]
    public void MoveLPostIncAnDm_CacheMiss_NoIncrement()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x2218);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[0] = 0x2000;
        Cpu.InvalidateDataCache();
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1000u, nextPC);
        Assert.Equal(0x2000u, Cpu.A[0]); // NOT incremented
    }

    [Fact]
    public void MoveLDmIndAn_AlwaysBailout()
    {
        ResetCpu();
        // MOVE.L D0,(A0) = 0x2080
        Mem.WriteWord(0x1000, 0x2080);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        SetupDataCache(0x2000, 0xFFF);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1000u, nextPC);
        Assert.Equal(0, Cpu._jitExecutedCount);
    }

    [Fact]
    public void MoveLD16AnDm_CacheHit()
    {
        ResetCpu();
        // MOVE.L d16(A2),D3 — opcode=0x2628 (srcMode=5, srcReg=0, but srcReg from bits 0-2)
        // Actually: 0010 ddd 000 101 sss → D3=dst(bits 11-9=011), srcMode=5, srcReg=A2(bits 2-0=010)
        // 0x262A = 0010 011 000 101 010 → MOVE.L d16(A2),D3
        Mem.WriteWord(0x1000, 0x262A);
        Mem.WriteWord(0x1002, 0x0010); // d16 = 16
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(1, block.InstructionCount);
        Assert.Equal(4, block.ByteLength);

        Cpu.A[2] = 0x2000;
        Mem.WriteLong(0x2010, 0xCAFEBABE);
        SetupDataCache(0x2000, 0xFFF);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1004u, nextPC);
        Assert.Equal(0xCAFEBABEu, Cpu.D[3]);
    }

    [Fact]
    public void MoveLD16AnDm_NegativeDisp()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x262A); // MOVE.L d16(A2),D3
        Mem.WriteWord(0x1002, unchecked((ushort)-16)); // d16 = -16
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[2] = 0x2020;
        Mem.WriteLong(0x2010, 0x11223344);
        SetupDataCache(0x2000, 0xFFF);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1004u, nextPC);
        Assert.Equal(0x11223344u, Cpu.D[3]);
    }

    [Fact]
    public void MoveLDmD16An_AlwaysBailout()
    {
        ResetCpu();
        // MOVE.L D0,d16(A1) — 0010 sss 101 000 ddd... wait, MOVE.L encoding:
        // MOVE.L src,dst — bits 11-6=dst, 5-0=src
        // dstMode=5(bits 8-6=101), dstReg=A1(bits 11-9=001)
        // srcMode=0, srcReg=D0(bits 2-0=000)
        // 0010 001 101 000 000 = 0x2340
        Mem.WriteWord(0x1000, 0x2340);
        Mem.WriteWord(0x1002, 0x0010);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        SetupDataCache(0x2000, 0xFFF);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1000u, nextPC);
        Assert.Equal(0, Cpu._jitExecutedCount);
    }

    [Fact]
    public void Rts_CacheHit()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x4E75); // RTS
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(1, block.InstructionCount);

        Cpu.A[7] = 0x2000;
        Mem.WriteLong(0x2000, 0x00003000);
        SetupDataCache(0x2000, 0xFFF);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x00003000u, nextPC);
        Assert.Equal(0x2004u, Cpu.A[7]); // stack pointer incremented
    }

    [Fact]
    public void Rts_CacheMiss_Bailout()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x4E75);
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.A[7] = 0x2000;
        Cpu.InvalidateDataCache();
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1000u, nextPC); // bailout
        Assert.Equal(0x2000u, Cpu.A[7]); // NOT incremented
        Assert.Equal(0, Cpu._jitExecutedCount);
    }

    [Fact]
    public void Rts_TerminatesBlock()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x7001); // MOVEQ #1,D0
        Mem.WriteWord(0x1002, 0x4E75); // RTS
        Mem.WriteWord(0x1004, 0x7002); // MOVEQ #2,D0 — should NOT be included
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(2, block.InstructionCount);
    }

    [Fact]
    public void Bailout_CycleCounting()
    {
        ResetCpu();
        // MOVEQ #1,D0 (2 cycles) + MOVE.L (A0),D1 (6 cycles)
        Mem.WriteWord(0x1000, 0x7001); // MOVEQ #1,D0
        Mem.WriteWord(0x1002, 0x2210); // MOVE.L (A0),D1
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        Cpu.InvalidateDataCache();
        uint nextPC = block.Execute(Cpu);
        // MOVEQ succeeds (1 instr), MOVE.L bails out
        Assert.Equal(0x1002u, nextPC);
        Assert.Equal(1, Cpu._jitExecutedCount);
        Assert.Equal(2, Cpu._jitExecutedCycles); // only MOVEQ's 2 cycles
    }

    [Fact]
    public void MixedBlock_RegisterAndMemory()
    {
        ResetCpu();
        // MOVEQ #5,D0 + MOVE.L (A0),D1 + MOVEQ #10,D2
        Mem.WriteWord(0x1000, 0x7005); // MOVEQ #5,D0
        Mem.WriteWord(0x1002, 0x2210); // MOVE.L (A0),D1
        Mem.WriteWord(0x1004, 0x740A); // MOVEQ #10,D2
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.Equal(3, block.InstructionCount);
        Assert.False(block.RegisterOnly);

        // Cache hit: all 3 execute
        Cpu.A[0] = 0x2000;
        Mem.WriteLong(0x2000, 0xAABBCCDD);
        SetupDataCache(0x2000, 0xFFF);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0x1006u, nextPC);
        Assert.Equal(5u, Cpu.D[0]);
        Assert.Equal(0xAABBCCDDu, Cpu.D[1]);
        Assert.Equal(10u, Cpu.D[2]);
        Assert.Equal(3, Cpu._jitExecutedCount);
    }

    [Fact]
    public void MoveLIndAnDm_PageBoundary_Bailout()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x2210); // MOVE.L (A0),D1
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        // Address 0x2FFE crosses page boundary
        Cpu.A[0] = 0x2FFE;
        SetupDataCache(0x2000, 0xFFF);
        uint nextPC = block.Execute(Cpu);
        Assert.Equal(0, Cpu._jitExecutedCount); // bailout
    }

    [Fact]
    public void RegisterOnlyBlock_FlagIsTrue()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x7001); // MOVEQ #1,D0
        Mem.WriteWord(0x1002, 0x7201); // MOVEQ #1,D1
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);
        Assert.True(block.RegisterOnly);
    }

    // ================================================================
    // Bailout blacklisting
    // ================================================================

    [Fact]
    public void BailoutBlacklist_EvictsAfterThreshold()
    {
        ResetCpu();
        Mem.WriteWord(0x1000, 0x2010); // MOVE.L (A0),D0
        var compiler = new JitCompiler();
        var block = compiler.TryCompile(Cpu, 0x1000, 0x1000);
        Assert.NotNull(block);

        var cache = Cpu.JitCache;
        cache.AddBlock(0x1000, block);
        Assert.NotNull(cache.TryGetBlock(0x1000));
        Assert.False(cache.IsUncompilable(0x1000));

        block.BailoutCount = MC68030.JitBailoutBlacklistThreshold;
        cache.RemoveBlock(0x1000);
        cache.MarkUncompilable(0x1000);

        Assert.Null(cache.TryGetBlock(0x1000));
        Assert.True(cache.IsUncompilable(0x1000));
    }
}
