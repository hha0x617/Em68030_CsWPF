using Em68030.Core;
using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.MmuTests;

/// <summary>
/// ページテーブルウォークの基本動作テスト。
/// </summary>
public class PageTableWalkTests
{
    private MmuTestFixture CreateFixture()
    {
        return new MmuTestFixture();
    }

    [Fact]
    public void Walk_ValidPage_ReturnsPhysicalAddress()
    {
        var f = CreateFixture();
        f.SetupPageTableEntry(0x10000000, 0x01000000);
        f.FlushAtc();

        uint pa = f.Mmu.Translate(0x10000000, true, false, 5);

        // PA should be the physical base + page offset (offset=0 here)
        Assert.Equal(0x01000000u, pa);
    }

    [Fact]
    public void Walk_ValidPage_PreservesPageOffset()
    {
        var f = CreateFixture();
        f.SetupPageTableEntry(0x10000000, 0x01000000);
        f.FlushAtc();

        // Access with page offset 0x123
        uint pa = f.Mmu.Translate(0x10000123, true, false, 5);

        Assert.Equal(0x01000123u, pa);
    }

    [Fact]
    public void Walk_InvalidPage_ThrowsBusError()
    {
        var f = CreateFixture();
        f.SetupInvalidPage(0x20000000);
        f.FlushAtc();

        var ex = Assert.Throws<BusErrorException>(() =>
            f.Mmu.Translate(0x20000000, true, false, 5));

        Assert.Equal(0x20000000u, ex.FaultAddress);
    }

    [Fact]
    public void Walk_WriteProtectedPage_ReadSucceeds()
    {
        var f = CreateFixture();
        f.SetupWriteProtectedPage(0x30000000, 0x03000000);
        f.FlushAtc();

        // Read should succeed on write-protected page
        uint pa = f.Mmu.Translate(0x30000000, true, false, 5);

        Assert.Equal(0x03000000u, pa);
    }

    [Fact]
    public void Walk_WriteProtectedPage_WriteThrowsBusError()
    {
        var f = CreateFixture();
        f.SetupWriteProtectedPage(0x30000000, 0x03000000);
        f.FlushAtc();

        var ex = Assert.Throws<BusErrorException>(() =>
            f.Mmu.Translate(0x30000000, true, true, 5));

        Assert.True(ex.IsWrite);
        Assert.Equal(0x30000000u, ex.FaultAddress);
    }

    [Fact]
    public void Walk_SetsModifiedBit_OnWrite()
    {
        var f = CreateFixture();
        // Setup page without Modified bit
        f.SetupPageTableEntry(0x10000000, 0x01000000, wp: false, modified: false);
        f.FlushAtc();

        // Write access should set Modified bit in the page descriptor
        f.Mmu.Translate(0x10000000, true, true, 5);

        // Verify via PTest that Modified bit is now set
        f.FlushAtc();
        f.Mmu.PTest(0x10000000, true, true, 5, 7);

        // M bit (0x0200) should be set in MMUSR
        Assert.NotEqual(0, f.Mmu.MMUSR & 0x0200);
    }

    [Fact]
    public void Walk_SetsUsedBit()
    {
        var f = CreateFixture();
        f.SetupPageTableEntry(0x10000000, 0x01000000);
        f.FlushAtc();

        // Read the page descriptor before access
        // Level A index = 1, Level B index = 0
        // Level B table addr = 0x00101000 + (1 * 64) = 0x00101040
        // Level B entry addr = 0x00101040 + (0 * 4) = 0x00101040
        uint levelBEntryAddr = 0x00101000 + (1 * 64) + (0 * 4);
        uint descBefore = f.Memory.ReadLong(levelBEntryAddr);
        Assert.Equal(0, (int)(descBefore & 0x08)); // U bit should be 0 initially

        // Access the page
        f.Mmu.Translate(0x10000000, true, false, 5);

        // Used bit should now be set
        uint descAfter = f.Memory.ReadLong(levelBEntryAddr);
        Assert.NotEqual(0, (int)(descAfter & 0x08)); // U bit (0x08) should be set
    }

    [Fact]
    public void Walk_MultiplePages_IndependentMapping()
    {
        var f = CreateFixture();
        f.SetupPageTableEntry(0x10000000, 0x01000000);
        f.SetupPageTableEntry(0x20000000, 0x02000000);
        f.SetupPageTableEntry(0x30000000, 0x03000000);
        f.FlushAtc();

        uint pa1 = f.Mmu.Translate(0x10000000, true, false, 5);
        uint pa2 = f.Mmu.Translate(0x20000000, true, false, 5);
        uint pa3 = f.Mmu.Translate(0x30000000, true, false, 5);

        Assert.Equal(0x01000000u, pa1);
        Assert.Equal(0x02000000u, pa2);
        Assert.Equal(0x03000000u, pa3);
    }
}
