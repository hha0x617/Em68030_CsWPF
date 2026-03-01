using Em68030.Core;
using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.MmuTests;

/// <summary>
/// TT0/TT1 透過的変換テスト。
/// MC68030 TTx register format:
///   Bits 31-24: Logical Address Base
///   Bits 23-16: Logical Address Mask
///   Bit 15:     E (Enable)
///   Bit 10:     CI (Cache Inhibit)
///   Bit 9:      R/W (0=write transparent, 1=read transparent)
///   Bit 8:      RWM (0=use R/W, 1=both directions transparent)
///   Bits 6-4:   FC BASE (3 bits)
///   Bits 2-0:   FC MASK (3 bits)
/// </summary>
public class TransparentTranslationTests
{
    private MmuTestFixture CreateFixture()
    {
        return new MmuTestFixture();
    }

    /// <summary>TT0 でマッチするアドレスは物理アドレス = 論理アドレス（identity mapping）</summary>
    [Fact]
    public void TT_Enabled_MatchingAddress_ReturnsIdentity()
    {
        var f = CreateFixture();

        // TT0: Base=0xFF, Mask=0x00, E=1, RWM=1, FC mask=7 (any)
        // 0xFF00_8107
        f.Mmu.TT0 = 0xFF008107;

        // Address 0xFF000000 should match TT0 and return identity mapping
        uint pa = f.Mmu.Translate(0xFF000100, true, false, 5);

        Assert.Equal(0xFF000100u, pa);
    }

    /// <summary>E=0 では TT は無効でマッチしない</summary>
    [Fact]
    public void TT_Disabled_NoMatch()
    {
        var f = CreateFixture();

        // TT0: Base=0x10, Mask=0x00, E=0 (disabled)
        f.Mmu.TT0 = 0x10000107; // E bit (bit 15) is 0

        // Without TT match and no page table entry, should trigger table walk
        // Setup a page entry so we can verify TT didn't match
        f.SetupPageTableEntry(0x10000000, 0x01000000);
        f.FlushAtc();

        uint pa = f.Mmu.Translate(0x10000000, true, false, 5);

        // Should use page table mapping, not identity
        Assert.Equal(0x01000000u, pa);
    }

    /// <summary>アドレスマスクで範囲が拡大される</summary>
    [Fact]
    public void TT_AddressMask_Works()
    {
        var f = CreateFixture();

        // TT0: Base=0xF0, Mask=0x0F, E=1, RWM=1, FC mask=7
        // Mask=0x0F means bits 24-27 are don't-care → matches 0xF0-0xFF
        f.Mmu.TT0 = 0xF00F8107;

        // Both 0xF0000000 and 0xFF000000 should match (mask covers lower nibble)
        uint pa1 = f.Mmu.Translate(0xF0000000, true, false, 5);
        uint pa2 = f.Mmu.Translate(0xFF000000, true, false, 5);

        Assert.Equal(0xF0000000u, pa1);
        Assert.Equal(0xFF000000u, pa2);
    }

    /// <summary>FC ベースとマスクが機能する</summary>
    [Fact]
    public void TT_FCMatch_Works()
    {
        var f = CreateFixture();

        // TT0: Base=0xFF, Mask=0x00, E=1, RWM=1, FC Base=5, FC Mask=0 (exact match FC=5)
        // FC Base in bits 6-4 = 5 = 0b101
        // FC Mask in bits 2-0 = 0 = 0b000 (no bits masked, exact match)
        // 0xFF00_8150 → Base=0xFF, E=1, RWM=1, FCBase=5, FCMask=0
        f.Mmu.TT0 = 0xFF008150;

        // FC=5 (supervisor data) should match
        uint pa = f.Mmu.Translate(0xFF000000, true, false, 5);
        Assert.Equal(0xFF000000u, pa);

        // FC=1 (user data) should NOT match TT0 → needs page table
        f.SetupPageTableEntry(0xF0000000, 0x0F000000);
        f.FlushAtc();

        // FC=1 should go through page table walk, not TT
        var ex = Assert.Throws<BusErrorException>(() =>
            f.Mmu.Translate(0xFF000000, false, false, 1));
        // Should bus error because no page table entry for 0xFF000000 with FC=1
    }

    /// <summary>R/W=1, RWM=0 → read のみ透過（write は TT を通らない）</summary>
    [Fact]
    public void TT_RW_ReadOnly()
    {
        var f = CreateFixture();

        // TT0: Base=0xFF, E=1, RWM=0, R/W=1 (read transparent), FC mask=7
        // RWM=0: bit 8 = 0
        // R/W=1: bit 9 = 1 → read transparent
        // 0xFF00_8307 → but RWM is bit 8=0, R/W is bit 9=1
        // Actually: E=bit15=1, CI=bit10=0, RW=bit9=1, RWM=bit8=0
        // 0xFF00 | 0x8000(E) | 0x0200(RW=1) | 0x0007(FCMask=7)
        f.Mmu.TT0 = 0xFF008207;

        // Read should be transparent
        uint pa = f.Mmu.Translate(0xFF000000, true, false, 5);
        Assert.Equal(0xFF000000u, pa);

        // Write should NOT be transparent → bus error (no page table entry)
        Assert.Throws<BusErrorException>(() =>
            f.Mmu.Translate(0xFF000000, true, true, 5));
    }

    /// <summary>R/W=0, RWM=0 → write のみ透過（read は TT を通らない）</summary>
    [Fact]
    public void TT_RW_WriteOnly()
    {
        var f = CreateFixture();

        // TT0: Base=0xFF, E=1, RWM=0, R/W=0 (write transparent), FC mask=7
        // RW=0, RWM=0
        f.Mmu.TT0 = 0xFF008007;

        // Write should be transparent
        uint pa = f.Mmu.Translate(0xFF000000, true, true, 5);
        Assert.Equal(0xFF000000u, pa);

        // Read should NOT be transparent → bus error (no page table entry)
        Assert.Throws<BusErrorException>(() =>
            f.Mmu.Translate(0xFF000000, true, false, 5));
    }

    /// <summary>RWM=1 → read/write 両方透過</summary>
    [Fact]
    public void TT_RWM_BothDirections()
    {
        var f = CreateFixture();

        // TT0: Base=0xFF, E=1, RWM=1, FC mask=7
        f.Mmu.TT0 = 0xFF008107;

        // Both read and write should be transparent
        uint paRead = f.Mmu.Translate(0xFF000000, true, false, 5);
        uint paWrite = f.Mmu.Translate(0xFF000000, true, true, 5);

        Assert.Equal(0xFF000000u, paRead);
        Assert.Equal(0xFF000000u, paWrite);
    }
}
