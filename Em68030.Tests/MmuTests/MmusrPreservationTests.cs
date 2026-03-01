using Em68030.Core;
using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.MmuTests;

/// <summary>
/// MMUSR 保存テスト — TableWalk() が MMUSR を上書きするバグの回帰テスト。
/// MC68030 PRM §9.5.3: 通常のアドレス変換では MMUSR を変更してはならない。
/// MMUSR を変更するのは PTEST 命令のみ。
/// </summary>
public class MmusrPreservationTests
{
    private MmuTestFixture CreateFixture()
    {
        var f = new MmuTestFixture();
        // Setup a valid page mapping: VA 0x10000000 → PA 0x01000000
        f.SetupPageTableEntry(0x10000000, 0x01000000);
        return f;
    }

    [Fact]
    public void Translate_DoesNot_ModifyMmusr()
    {
        var f = CreateFixture();

        // Set a known MMUSR value (simulating a prior PTEST result)
        f.Mmu.MMUSR = 0x1234;

        // Translate should NOT modify MMUSR
        f.Mmu.Translate(0x10000000, true, false, 5);

        Assert.Equal(0x1234, f.Mmu.MMUSR);
    }

    [Fact]
    public void Translate_AtcMiss_PreservesMmusr()
    {
        var f = CreateFixture();

        // Ensure ATC miss by flushing
        f.FlushAtc();
        f.Mmu.MMUSR = 0xABCD;

        // This will trigger a full TableWalk (ATC miss)
        f.Mmu.Translate(0x10000000, true, false, 5);

        Assert.Equal(0xABCD, f.Mmu.MMUSR);
    }

    [Fact]
    public void Translate_WriteProtect_PreservesMmusr()
    {
        var f = CreateFixture();

        // Setup write-protected page
        f.SetupWriteProtectedPage(0x20000000, 0x02000000);
        f.FlushAtc();
        f.Mmu.MMUSR = 0x5678;

        // Write to WP page should throw BusErrorException but preserve MMUSR
        var ex = Assert.Throws<BusErrorException>(() =>
            f.Mmu.Translate(0x20000000, true, true, 5));

        Assert.Equal(0x5678, f.Mmu.MMUSR);
        Assert.True(ex.IsWrite);
    }

    [Fact]
    public void Translate_ModifiedBitReWalk_PreservesMmusr()
    {
        var f = CreateFixture();

        // First: read access to populate ATC (without Modified bit)
        f.FlushAtc();
        f.Mmu.Translate(0x10000000, true, false, 5);

        // Set known MMUSR
        f.Mmu.MMUSR = 0x9999;

        // Write access: ATC hit but Modified=false → triggers re-walk to set M bit
        f.Mmu.Translate(0x10000000, true, true, 5);

        Assert.Equal(0x9999, f.Mmu.MMUSR);
    }

    [Fact]
    public void PTest_Does_ModifyMmusr()
    {
        var f = CreateFixture();
        f.Mmu.MMUSR = 0x0000;

        // PTest SHOULD modify MMUSR
        f.Mmu.PTest(0x10000000, true, false, 5, 7);

        // After PTest on a valid page, MMUSR should reflect the walk result
        // N (levels searched) should be non-zero for a valid page
        Assert.NotEqual((ushort)0, f.Mmu.MMUSR);
        // I bit (0x0400) should NOT be set for a valid page
        Assert.Equal(0, f.Mmu.MMUSR & 0x0400);
    }

    [Fact]
    public void PLoad_DoesNot_ModifyMmusr()
    {
        var f = CreateFixture();
        f.Mmu.MMUSR = 0x4321;

        // PLoad should NOT modify MMUSR (per MC68030 UM)
        f.Mmu.PLoad(0x10000000, true, false, 5);

        Assert.Equal(0x4321, f.Mmu.MMUSR);
    }

    [Fact]
    public void PTest_Then_Translate_PreservesPTestResult()
    {
        var f = CreateFixture();

        // Setup another page for translation
        f.SetupPageTableEntry(0x30000000, 0x03000000);
        f.FlushAtc();

        // PTest sets MMUSR with valid result
        f.Mmu.PTest(0x10000000, true, false, 5, 7);
        ushort ptestResult = f.Mmu.MMUSR;

        // Translate should NOT disturb the PTEST result
        f.Mmu.Translate(0x30000000, true, false, 5);

        Assert.Equal(ptestResult, f.Mmu.MMUSR);
    }
}
