using Em68030.IO;
using Xunit;

namespace Em68030.Tests.IoTests;

// ============================================================================
// Mk48t02Device Tests
// ============================================================================

public class Mk48t02DeviceTests
{
    private const uint Base = 0xFFFE0000;
    private readonly Mk48t02Device _rtc = new();

    // ============================================================================
    // NVRAM read/write
    // ============================================================================

    [Fact]
    public void NVRAM_WriteAndReadByte()
    {
        _rtc.WriteByte(Base + 0x0000, 0x42);
        Assert.Equal(0x42, _rtc.ReadByte(Base + 0x0000));
    }

    [Fact]
    public void NVRAM_WriteAndReadMultipleLocations()
    {
        _rtc.WriteByte(Base + 0x0100, 0xAA);
        _rtc.WriteByte(Base + 0x0200, 0x55);
        Assert.Equal(0xAA, _rtc.ReadByte(Base + 0x0100));
        Assert.Equal(0x55, _rtc.ReadByte(Base + 0x0200));
    }

    [Fact]
    public void NVRAM_DefaultIsZero()
    {
        Assert.Equal(0, _rtc.ReadByte(Base + 0x0000));
        Assert.Equal(0, _rtc.ReadByte(Base + 0x0100));
        Assert.Equal(0, _rtc.ReadByte(Base + 0x07F7));
    }

    [Fact]
    public void NVRAM_OutOfRange_ReturnsFF()
    {
        Assert.Equal(0xFF, _rtc.ReadByte(Base + 2048));
    }

    [Fact]
    public void NVRAM_OutOfRange_WriteIgnored()
    {
        _rtc.WriteByte(Base + 2048, 0x42);
        Assert.Equal(0xFF, _rtc.ReadByte(Base + 2048));
    }

    // ============================================================================
    // Word / Long access
    // ============================================================================

    [Fact]
    public void NVRAM_ReadWord()
    {
        _rtc.WriteByte(Base + 0x10, 0xAB);
        _rtc.WriteByte(Base + 0x11, 0xCD);
        Assert.Equal((ushort)0xABCD, _rtc.ReadWord(Base + 0x10));
    }

    [Fact]
    public void NVRAM_WriteWord()
    {
        _rtc.WriteWord(Base + 0x20, 0x1234);
        Assert.Equal(0x12, _rtc.ReadByte(Base + 0x20));
        Assert.Equal(0x34, _rtc.ReadByte(Base + 0x21));
    }

    [Fact]
    public void NVRAM_ReadLong()
    {
        _rtc.WriteByte(Base + 0x30, 0xDE);
        _rtc.WriteByte(Base + 0x31, 0xAD);
        _rtc.WriteByte(Base + 0x32, 0xBE);
        _rtc.WriteByte(Base + 0x33, 0xEF);
        Assert.Equal(0xDEADBEEFu, _rtc.ReadLong(Base + 0x30));
    }

    [Fact]
    public void NVRAM_WriteLong()
    {
        _rtc.WriteLong(Base + 0x40, 0xCAFEBABE);
        Assert.Equal(0xCA, _rtc.ReadByte(Base + 0x40));
        Assert.Equal(0xFE, _rtc.ReadByte(Base + 0x41));
        Assert.Equal(0xBA, _rtc.ReadByte(Base + 0x42));
        Assert.Equal(0xBE, _rtc.ReadByte(Base + 0x43));
    }

    // ============================================================================
    // Clock registers — basic sanity (values are BCD, from real time)
    // ============================================================================

    [Fact]
    public void Clock_SecondsInBcdRange()
    {
        byte sec = _rtc.ReadByte(Base + 0x7F9);
        Assert.True((sec >> 4) <= 5);
        Assert.True((sec & 0x0F) <= 9);
    }

    [Fact]
    public void Clock_MinutesInBcdRange()
    {
        byte min = _rtc.ReadByte(Base + 0x7FA);
        Assert.True((min >> 4) <= 5);
        Assert.True((min & 0x0F) <= 9);
    }

    [Fact]
    public void Clock_HoursInBcdRange()
    {
        byte hour = _rtc.ReadByte(Base + 0x7FB);
        Assert.True((hour >> 4) <= 2);
        Assert.True((hour & 0x0F) <= 9);
        int h = (hour >> 4) * 10 + (hour & 0x0F);
        Assert.True(h <= 23);
    }

    [Fact]
    public void Clock_DayOfWeekInRange()
    {
        byte dow = _rtc.ReadByte(Base + 0x7FC);
        Assert.InRange(dow, 1, 7);
    }

    [Fact]
    public void Clock_DateInBcdRange()
    {
        byte date = _rtc.ReadByte(Base + 0x7FD);
        int d = (date >> 4) * 10 + (date & 0x0F);
        Assert.InRange(d, 1, 31);
    }

    [Fact]
    public void Clock_MonthInBcdRange()
    {
        byte mon = _rtc.ReadByte(Base + 0x7FE);
        int m = (mon >> 4) * 10 + (mon & 0x0F);
        Assert.InRange(m, 1, 12);
    }

    [Fact]
    public void Clock_YearInBcdRange()
    {
        byte year = _rtc.ReadByte(Base + 0x7FF);
        Assert.True((year >> 4) <= 9);
        Assert.True((year & 0x0F) <= 9);
    }

    // ============================================================================
    // Control register
    // ============================================================================

    [Fact]
    public void Clock_ControlRegister_Writable()
    {
        _rtc.WriteByte(Base + 0x7F8, 0x80);
        Assert.Equal(0x80, _rtc.ReadByte(Base + 0x7F8));
    }

    [Fact]
    public void Clock_ControlRegister_DefaultZero()
    {
        Assert.Equal(0x00, _rtc.ReadByte(Base + 0x7F8));
    }

    // ============================================================================
    // Year offset (NetBSD vs Linux)
    // ============================================================================

    [Fact]
    public void YearOffset_Default_LinuxMode()
    {
        byte year = _rtc.ReadByte(Base + 0x7FF);
        int y = (year >> 4) * 10 + (year & 0x0F);
        Assert.InRange(y, 0, 99);
    }

    [Fact]
    public void YearBase_NetBSD_DifferentFromDefault()
    {
        byte yearLinux = _rtc.ReadByte(Base + 0x7FF);
        _rtc.YearBase = 1968; // NetBSD
        byte yearNetBSD = _rtc.ReadByte(Base + 0x7FF);
        int yLinux = (yearLinux >> 4) * 10 + (yearLinux & 0x0F);
        int yNetBSD = (yearNetBSD >> 4) * 10 + (yearNetBSD & 0x0F);
        Assert.NotEqual(yLinux, yNetBSD);
    }

    // ============================================================================
    // SetMvme147Config
    // ============================================================================

    [Fact]
    public void SetMvme147Config_RamEnd()
    {
        _rtc.SetMvme147Config(0x02000000, new byte[] { 0x08, 0x00, 0x3E });
        Assert.Equal(0x02000000u, _rtc.ReadLong(Base + 0x0774));
    }

    [Fact]
    public void SetMvme147Config_OffboardRamZero()
    {
        _rtc.SetMvme147Config(0x02000000, new byte[] { 0x08, 0x00, 0x3E });
        Assert.Equal(0u, _rtc.ReadLong(Base + 0x0764));
        Assert.Equal(0u, _rtc.ReadLong(Base + 0x0768));
    }

    [Fact]
    public void SetMvme147Config_EthernetAddr()
    {
        _rtc.SetMvme147Config(0x01000000, new byte[] { 0xAA, 0xBB, 0xCC });
        Assert.Equal(0xAA, _rtc.ReadByte(Base + 0x0778));
        Assert.Equal(0xBB, _rtc.ReadByte(Base + 0x0779));
        Assert.Equal(0xCC, _rtc.ReadByte(Base + 0x077A));
    }

    [Fact]
    public void SetMvme147Config_ShortEthernet_NoWrite()
    {
        _rtc.WriteByte(Base + 0x0778, 0xFF);
        _rtc.SetMvme147Config(0x01000000, new byte[] { 0x01, 0x02 });
        Assert.Equal(0xFF, _rtc.ReadByte(Base + 0x0778)); // unchanged
    }
}
