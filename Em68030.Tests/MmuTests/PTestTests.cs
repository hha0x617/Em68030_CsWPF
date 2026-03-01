using Em68030.Core;
using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.MmuTests;

/// <summary>
/// PTEST 命令テスト。
/// PTEST は非破壊的なテーブルウォークを行い、MMUSR に結果をセットする。
/// </summary>
public class PTestTests
{
    private MmuTestFixture CreateFixture()
    {
        return new MmuTestFixture();
    }

    [Fact]
    public void PTest_ValidPage_SetsLevelCount()
    {
        var f = CreateFixture();
        f.SetupPageTableEntry(0x10000000, 0x01000000);

        f.Mmu.PTest(0x10000000, true, false, 5, 7);

        // N (bits 2-0) should be non-zero (levels searched)
        int levelsSearched = f.Mmu.MMUSR & 7;
        Assert.True(levelsSearched > 0, $"Expected levelsSearched > 0, got {levelsSearched}");
        // With 2-level page table (TIA=4, TIB=4), expect 2 levels
        Assert.Equal(2, levelsSearched);
    }

    [Fact]
    public void PTest_InvalidPage_SetsInvalidBit()
    {
        var f = CreateFixture();
        f.SetupInvalidPage(0x20000000);

        f.Mmu.PTest(0x20000000, true, false, 5, 7);

        // I bit (0x0400) should be set
        Assert.NotEqual(0, f.Mmu.MMUSR & 0x0400);
    }

    [Fact]
    public void PTest_WriteProtectedPage_SetsWPBit()
    {
        var f = CreateFixture();
        f.SetupWriteProtectedPage(0x30000000, 0x03000000);

        f.Mmu.PTest(0x30000000, true, true, 5, 7);

        // W bit (0x0800) should be set
        Assert.NotEqual(0, f.Mmu.MMUSR & 0x0800);
    }

    [Fact]
    public void PTest_ModifiedPage_SetsModifiedBit()
    {
        var f = CreateFixture();
        f.SetupPageTableEntry(0x10000000, 0x01000000, wp: false, modified: true);

        f.Mmu.PTest(0x10000000, true, false, 5, 7);

        // M bit (0x0200) should be set
        Assert.NotEqual(0, f.Mmu.MMUSR & 0x0200);
    }

    [Fact]
    public void PTest_MmuDisabled_ReturnsZero()
    {
        var f = CreateFixture();

        // Disable MMU
        f.Mmu.TC = 0x00000000;

        f.Mmu.PTest(0x10000000, true, false, 5, 7);

        // MMUSR should be 0 when MMU is disabled
        Assert.Equal(0, (int)f.Mmu.MMUSR);
    }

    [Fact]
    public void PTest_Level0_Transparent_SetsTBit()
    {
        var f = CreateFixture();

        // Setup TT0 to match address range 0x10xxxxxx
        // TT0 format: Base=0x10, Mask=0x00, E=1, RWM=1, FC any
        // Bits: Base(0x10) Mask(0x00) E(1) reserved CI RW RWM reserved FCBase FCMask
        //       0x10       0x00       1    0000     0  0  1   0        000    111
        // = 0x1000_8107
        f.Mmu.TT0 = 0x10008107; // Base=0x10, Mask=0x00, E=1, RWM=1, FC mask=7 (any FC)

        // PTest at level 0 checks TT registers
        f.Mmu.PTest(0x10000000, true, false, 5, 0);

        // T bit (0x0040) should be set
        Assert.NotEqual(0, f.Mmu.MMUSR & 0x0040);
    }

    [Fact]
    public void PTest_SetsLastDescriptorAddress()
    {
        var f = CreateFixture();
        f.SetupPageTableEntry(0x10000000, 0x01000000);

        f.Mmu.PTest(0x10000000, true, false, 5, 7);

        // LastDescriptorAddress should point to the page descriptor
        Assert.NotEqual(0u, f.Mmu.LastDescriptorAddress);
    }

    [Fact]
    public void PTest_MaxLevel_LimitsSearch()
    {
        var f = CreateFixture();
        f.SetupPageTableEntry(0x10000000, 0x01000000);

        // With maxLevel=1, should only search 1 level (Level A table descriptor)
        f.Mmu.PTest(0x10000000, true, false, 5, 1);

        // N (bits 2-0) should be 1 (stopped at level A)
        int levelsSearched = f.Mmu.MMUSR & 7;
        Assert.Equal(1, levelsSearched);
    }
}
