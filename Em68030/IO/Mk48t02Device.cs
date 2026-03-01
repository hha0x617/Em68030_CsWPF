namespace Em68030.IO;

using Em68030.Core;

/// <summary>
/// MK48T02 NVRAM/RTC for MVME147.
/// 2KB SRAM with clock registers at the last 8 bytes ($7F8-$7FF).
/// Mapped at $FFFE0000, 2048 bytes.
///
/// Clock registers (offset from base):
///   $7F8 = Control (bit 7: Write, bit 6: Read)
///   $7F9 = Seconds (BCD, 0-59)
///   $7FA = Minutes (BCD, 0-59)
///   $7FB = Hours   (BCD, 0-23)
///   $7FC = Day of week (1-7)
///   $7FD = Date    (BCD, 1-31)
///   $7FE = Month   (BCD, 1-12)
///   $7FF = Year    (BCD, 0-99)
/// </summary>
public class Mk48t02Device : IMemoryMappedDevice
{
    private const uint BaseAddress = 0xFFFE0000;
    private readonly byte[] _nvram = new byte[2048];

    public byte ReadByte(uint address)
    {
        uint offset = address - BaseAddress;
        if (offset >= 2048) return 0xFF;

        if (offset >= 0x7F8)
            return ReadClockRegister(offset - 0x7F8);

        return _nvram[offset];
    }

    public ushort ReadWord(uint address)
    {
        return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
    }

    public uint ReadLong(uint address)
    {
        return (uint)((ReadWord(address) << 16) | ReadWord(address + 2));
    }

    public void WriteByte(uint address, byte value)
    {
        uint offset = address - BaseAddress;
        if (offset >= 2048) return;

        _nvram[offset] = value;
    }

    public void WriteWord(uint address, ushort value)
    {
        WriteByte(address, (byte)(value >> 8));
        WriteByte(address + 1, (byte)(value & 0xFF));
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)(value & 0xFFFF));
    }

    private byte ReadClockRegister(uint reg)
    {
        var now = DateTime.UtcNow;
        return reg switch
        {
            0 => _nvram[0x7F8], // Control register
            1 => ToBcd(now.Second),
            2 => ToBcd(now.Minute),
            3 => ToBcd(now.Hour),
            4 => (byte)((int)now.DayOfWeek + 1), // Sunday=1
            5 => ToBcd(now.Day),
            6 => ToBcd(now.Month),
            7 => ToBcd((now.Year - 1968) % 100), // YEAR0=1968 (Sun/NetBSD convention)
            _ => 0
        };
    }

    private static byte ToBcd(int val) => (byte)(((val / 10) << 4) | (val % 10));

    /// <summary>
    /// Pre-populate NVRAM with MVME147 hardware configuration values.
    /// These are normally set by 147Bug firmware during POST.
    /// </summary>
    public void SetMvme147Config(uint onboardRamEnd, byte[] ethernetAddr)
    {
        // Offset $0774: End+1 of onboard memory (32-bit, big-endian)
        _nvram[0x0774] = (byte)(onboardRamEnd >> 24);
        _nvram[0x0775] = (byte)(onboardRamEnd >> 16);
        _nvram[0x0776] = (byte)(onboardRamEnd >> 8);
        _nvram[0x0777] = (byte)(onboardRamEnd);

        // Offset $0764: Start of offboard RAM (0 = none)
        _nvram[0x0764] = 0;
        _nvram[0x0765] = 0;
        _nvram[0x0766] = 0;
        _nvram[0x0767] = 0;

        // Offset $0768: End of offboard RAM (0 = none)
        _nvram[0x0768] = 0;
        _nvram[0x0769] = 0;
        _nvram[0x076A] = 0;
        _nvram[0x076B] = 0;

        // Offset $0778: Ethernet address bytes (used by Linit147)
        // Format: 3 bytes at $0778-$077A, masked/combined with fixed prefix 08:00:3E
        if (ethernetAddr.Length >= 3)
        {
            _nvram[0x0778] = ethernetAddr[0];
            _nvram[0x0779] = ethernetAddr[1];
            _nvram[0x077A] = ethernetAddr[2];
        }
    }
}
