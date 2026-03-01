namespace Em68030.IO;

using System.IO;
using Em68030.Core;

public class HddDevice : IMemoryMappedDevice
{
    private uint _baseAddress;
    private FileStream? _imageStream;
    private string? _imagePath;
    private Memory? _memory;

    public const int SectorSize = 512;

    // Register offsets
    private const int RegCommand = 0x00;   // Long R/W - Command
    private const int RegLBA = 0x04;       // Long R/W - LBA sector number
    private const int RegStatus = 0x08;    // Long R   - Status
    private const int RegDmaAddr = 0x0C;   // Long R/W - DMA transfer address

    // Commands
    private const uint CmdNop = 0;
    private const uint CmdRead = 1;
    private const uint CmdWrite = 2;
    private const uint CmdStatus = 3;

    // Status bits
    private const uint StatusReady = 0x01;
    private const uint StatusError = 0x02;

    // Internal registers
    private uint _command;
    private uint _lba;
    private uint _status = StatusReady;
    private uint _dmaAddr;

    public uint BaseAddress
    {
        get => _baseAddress;
        set => _baseAddress = value;
    }

    public bool IsImageLoaded => _imageStream != null;
    public string? ImagePath => _imagePath;

    public HddDevice(uint baseAddress = 0x00FF1000)
    {
        _baseAddress = baseAddress;
    }

    public void AttachMemory(Memory memory)
    {
        _memory = memory;
    }

    public void MountImage(string path)
    {
        _imageStream?.Close();
        _imagePath = path;
        _imageStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _status = StatusReady;
    }

    public void UnmountImage()
    {
        _imageStream?.Close();
        _imageStream = null;
        _imagePath = null;
        _status = StatusError;
    }

    public static void CreateImage(string path, long sizeBytes)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(sizeBytes);
    }

    public byte ReadByte(uint address)
    {
        uint offset = address - _baseAddress;
        uint regValue = offset switch
        {
            0x00 or 0x01 or 0x02 or 0x03 => _command,
            0x04 or 0x05 or 0x06 or 0x07 => _lba,
            0x08 or 0x09 or 0x0A or 0x0B => _status,
            0x0C or 0x0D or 0x0E or 0x0F => _dmaAddr,
            _ => 0
        };
        int byteOffset = (int)(3 - (offset & 3));
        return (byte)(regValue >> (byteOffset * 8));
    }

    public ushort ReadWord(uint address)
    {
        return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
    }

    public uint ReadLong(uint address)
    {
        uint offset = address - _baseAddress;
        return offset switch
        {
            0x00 => _command,
            0x04 => _lba,
            0x08 => _status,
            0x0C => _dmaAddr,
            _ => 0
        };
    }

    public void WriteByte(uint address, byte value)
    {
        // Byte writes to registers - accumulate
        uint offset = address - _baseAddress;
        int byteOffset = (int)(3 - (offset & 3));
        uint aligned = offset & 0xFFFC;
        uint mask = ~(0xFFu << (byteOffset * 8));
        uint val = (uint)value << (byteOffset * 8);

        switch (aligned)
        {
            case 0x00: _command = (_command & mask) | val; break;
            case 0x04: _lba = (_lba & mask) | val; break;
            case 0x0C: _dmaAddr = (_dmaAddr & mask) | val; break;
        }

        // Execute on command register byte 3 write (last byte)
        if (offset == 0x03)
            ExecuteCommand();
    }

    public void WriteWord(uint address, ushort value)
    {
        WriteByte(address, (byte)(value >> 8));
        WriteByte(address + 1, (byte)(value & 0xFF));
    }

    public void WriteLong(uint address, uint value)
    {
        uint offset = address - _baseAddress;
        switch (offset)
        {
            case 0x00:
                _command = value;
                ExecuteCommand();
                break;
            case 0x04: _lba = value; break;
            case 0x0C: _dmaAddr = value; break;
        }
    }

    private void ExecuteCommand()
    {
        if (_memory == null || _imageStream == null)
        {
            _status = StatusError;
            return;
        }

        switch (_command)
        {
            case CmdNop:
                break;

            case CmdRead:
                ReadSector();
                break;

            case CmdWrite:
                WriteSector();
                break;

            case CmdStatus:
                // Status already set
                break;
        }

        _command = CmdNop;
    }

    private void ReadSector()
    {
        if (_imageStream == null || _memory == null)
        {
            _status = StatusError;
            return;
        }

        long offset = (long)_lba * SectorSize;
        if (offset + SectorSize > _imageStream.Length)
        {
            _status = StatusError;
            return;
        }

        byte[] buffer = new byte[SectorSize];
        _imageStream.Seek(offset, SeekOrigin.Begin);
        int bytesRead = _imageStream.Read(buffer, 0, SectorSize);

        // DMA transfer to memory
        for (int i = 0; i < bytesRead; i++)
        {
            _memory.PokeByte(_dmaAddr + (uint)i, buffer[i]);
        }

        _status = StatusReady;
    }

    private void WriteSector()
    {
        if (_imageStream == null || _memory == null)
        {
            _status = StatusError;
            return;
        }

        long offset = (long)_lba * SectorSize;

        // Extend file if needed
        if (offset + SectorSize > _imageStream.Length)
        {
            _imageStream.SetLength(offset + SectorSize);
        }

        byte[] buffer = new byte[SectorSize];
        for (int i = 0; i < SectorSize; i++)
        {
            buffer[i] = _memory.PeekByte(_dmaAddr + (uint)i);
        }

        _imageStream.Seek(offset, SeekOrigin.Begin);
        _imageStream.Write(buffer, 0, SectorSize);
        _imageStream.Flush();

        _status = StatusReady;
    }

    public void Dispose()
    {
        _imageStream?.Close();
    }
}
