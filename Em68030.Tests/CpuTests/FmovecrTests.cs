using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.CpuTests;

/// <summary>
/// Tests for FMOVECR (Move Constant ROM) instruction.
/// FMOVECR loads MC68882 on-chip constants into FP registers.
/// Encoding: F200 5Cxx where xx encodes destination register and ROM offset.
/// cmdWord format: 010 111 ddd ooooooo (cmdType=2, srcFormat=7, dst=ddd, offset=ooo_oooo)
/// </summary>
public class FmovecrTests : IClassFixture<CpuTestFixture>
{
    private readonly CpuTestFixture _fixture;

    public FmovecrTests(CpuTestFixture fixture) => _fixture = fixture;

    private void ExecuteFmovecr(int dstReg, int romOffset)
    {
        var cpu = _fixture.Cpu;
        uint addr = 0x1000;

        // F-line word: F200 (cpID=1, type=0 general)
        _fixture.Memory.WriteWord(addr, 0xF200);
        // cmdWord: 010 111 ddd ooooooo
        ushort cmdWord = (ushort)(0x5C00 | ((dstReg & 7) << 7) | (romOffset & 0x7F));
        _fixture.Memory.WriteWord(addr + 2, cmdWord);
        // NOP after for clean stop
        _fixture.Memory.WriteWord(addr + 4, 0x4E71);

        cpu.PC = addr;
        cpu.ExecuteStep();
    }

    [Fact]
    public void Fmovecr_Pi_LoadsIntoFP0()
    {
        ExecuteFmovecr(0, 0x00);
        Assert.Equal(Math.PI, _fixture.Cpu.Fpu.FP[0], 10);
    }

    [Fact]
    public void Fmovecr_Zero_LoadsIntoFP1()
    {
        ExecuteFmovecr(1, 0x0F);
        Assert.Equal(0.0, _fixture.Cpu.Fpu.FP[1]);
    }

    [Fact]
    public void Fmovecr_One_LoadsIntoFP3()
    {
        // ROM offset 0x32 = 10^0 = 1.0
        ExecuteFmovecr(3, 0x32);
        Assert.Equal(1.0, _fixture.Cpu.Fpu.FP[3]);
    }

    [Fact]
    public void Fmovecr_Hundred_LoadsIntoFP0()
    {
        // ROM offset 0x34 = 10^2 = 100.0
        ExecuteFmovecr(0, 0x34);
        Assert.Equal(100.0, _fixture.Cpu.Fpu.FP[0]);
    }

    [Fact]
    public void Fmovecr_Ten_LoadsIntoFP2()
    {
        // ROM offset 0x33 = 10^1 = 10.0
        ExecuteFmovecr(2, 0x33);
        Assert.Equal(10.0, _fixture.Cpu.Fpu.FP[2]);
    }

    [Fact]
    public void Fmovecr_E_LoadsCorrectValue()
    {
        ExecuteFmovecr(4, 0x0C);
        Assert.Equal(Math.E, _fixture.Cpu.Fpu.FP[4], 10);
    }

    [Fact]
    public void Fmovecr_Ln2_LoadsCorrectValue()
    {
        // ROM offset 0x30 = ln(2)
        ExecuteFmovecr(5, 0x30);
        Assert.Equal(Math.Log(2.0), _fixture.Cpu.Fpu.FP[5], 10);
    }

    [Fact]
    public void Fmovecr_SetsConditionCodes_Zero()
    {
        ExecuteFmovecr(0, 0x0F); // Load 0.0
        // FPSR condition codes: Z flag should be set
        uint cc = (_fixture.Cpu.Fpu.FPSR >> 24) & 0xF;
        Assert.True((cc & 0x4) != 0, "Z flag should be set for zero constant");
    }

    [Fact]
    public void Fmovecr_SetsConditionCodes_Positive()
    {
        ExecuteFmovecr(0, 0x32); // Load 1.0
        uint cc = (_fixture.Cpu.Fpu.FPSR >> 24) & 0xF;
        Assert.True((cc & 0x4) == 0, "Z flag should be clear for non-zero");
        Assert.True((cc & 0x8) == 0, "N flag should be clear for positive");
    }
}
