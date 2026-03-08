namespace Em68030.IO;

using System.IO;

/// <summary>
/// SCSI CD-ROM target device. Processes SCSI CDBs against an ISO 9660 image file.
/// Sector size is fixed at 2048 bytes (ISO 9660 standard).
/// Read-only device — no write commands are supported.
/// </summary>
public class ScsiCdrom : IScsiTarget
{
    private FileStream? _imageStream;
    private long _totalSectors;
    private const int SectorSize = 2048;
    private byte[] _senseData = new byte[18];
    private bool _mediaChanged; // Set on mount/unmount to trigger UNIT ATTENTION

    public bool IsReady => _imageStream != null;

    public void MountImage(string path)
    {
        bool wasOpen = _imageStream != null;
        UnmountImage();
        _imageStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _totalSectors = _imageStream.Length / SectorSize;
        if (_totalSectors == 0 && _imageStream.Length > 0)
            _totalSectors = 1;
        // Only report UNIT ATTENTION for media changes after initial mount.
        // At power-on, media is already present — no UNIT ATTENTION needed.
        _mediaChanged = wasOpen;
        ClearSense();
    }

    public void UnmountImage()
    {
        if (_imageStream != null)
            _mediaChanged = true;
        _imageStream?.Dispose();
        _imageStream = null;
        _totalSectors = 0;
    }

    public ScsiResult ProcessCommand(byte[] cdb, int cdbLength, int lun = 0)
    {
        byte opcode = cdb[0];

        // INQUIRY and REQUEST SENSE must always work regardless of media state
        // (SCSI standard: these commands never return CHECK CONDITION for
        // UNIT ATTENTION or NOT READY).
        if (lun != 0)
        {
            if (opcode == 0x12) return CmdInquiryNoDevice(cdb);
            if (opcode == 0x03) return CmdRequestSense(cdb);
            return MakeCheckCondition(0x05, 0x25, 0x00); // LUN NOT SUPPORTED
        }
        if (opcode == 0x12) return CmdInquiry(cdb);
        if (opcode == 0x03) return CmdRequestSense(cdb);

        // Report media change via UNIT ATTENTION
        if (_mediaChanged)
        {
            _mediaChanged = false;
            return MakeCheckCondition(0x06, 0x28, 0x00); // UNIT ATTENTION, MEDIUM MAY HAVE CHANGED
        }

        if (!IsReady)
            return MakeCheckCondition(0x02, 0x3A, 0x00); // NOT READY, MEDIUM NOT PRESENT

        byte op = cdb[0];
        return op switch
        {
            0x00 => CmdTestUnitReady(),
            0x03 => CmdRequestSense(cdb),
            0x08 => CmdRead6(cdb),
            0x12 => CmdInquiry(cdb),
            0x1A => CmdModeSense6(cdb),
            0x1B => CmdStartStopUnit(),
            0x1E => CmdPreventAllowRemoval(),
            0x25 => CmdReadCapacity(),
            0x28 => CmdRead10(cdb),
            0x43 => CmdReadToc(cdb),
            0x5A => CmdModeSense10(cdb),
            _ => MakeCheckCondition(0x05, 0x20, 0x00) // INVALID COMMAND
        };
    }

    public void CompleteWrite(uint lba, byte[] data, int length)
    {
        // CD-ROM is read-only — no-op
    }

    private ScsiResult CmdTestUnitReady()
    {
        ClearSense();
        return new ScsiResult { StatusByte = 0x00 };
    }

    private ScsiResult CmdRequestSense(byte[] cdb)
    {
        int allocLen = cdb[4];
        if (allocLen == 0) allocLen = 18;
        int len = Math.Min(allocLen, 18);
        byte[] data = new byte[len];
        Array.Copy(_senseData, data, len);
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = data, DataInLength = len, HasDataIn = true };
    }

    private ScsiResult CmdInquiryNoDevice(byte[] cdb)
    {
        int allocLen = cdb[4];
        if (allocLen == 0) allocLen = 36;
        byte[] data = new byte[36];
        data[0] = 0x7F; // No device at this LUN
        data[4] = 0x1F;
        int len = Math.Min(allocLen, 36);
        return new ScsiResult { StatusByte = 0x00, DataIn = TrimData(data, len), DataInLength = len, HasDataIn = true };
    }

    private ScsiResult CmdInquiry(byte[] cdb)
    {
        int allocLen = cdb[4];
        if (allocLen == 0) allocLen = 36;
        byte[] data = new byte[36];
        data[0] = 0x05; // CD-ROM device
        data[1] = 0x80; // Removable media
        data[2] = 0x02; // SCSI-2
        data[3] = 0x02; // Response format 2
        data[4] = 0x1F; // Additional length = 31

        SetString(data, 8, 8, "EMULATED");
        SetString(data, 16, 16, "SCSI CD-ROM");
        SetString(data, 32, 4, "1.0 ");

        int len = Math.Min(allocLen, 36);
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = TrimData(data, len), DataInLength = len, HasDataIn = true };
    }

    private ScsiResult CmdModeSense6(byte[] cdb)
    {
        int allocLen = cdb[4];
        if (allocLen == 0) allocLen = 4;

        // Mode parameter header (4 bytes) + CD-ROM capabilities page (optional)
        byte[] data = new byte[4];
        data[0] = 0x03; // Mode data length
        data[1] = 0x01; // Medium type: 120mm CD-ROM data only
        data[2] = 0x80; // Device-specific: write-protected (read-only)
        data[3] = 0x00; // Block descriptor length

        int len = Math.Min(allocLen, 4);
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = TrimData(data, len), DataInLength = len, HasDataIn = true };
    }

    private ScsiResult CmdModeSense10(byte[] cdb)
    {
        int allocLen = cdb[7] << 8 | cdb[8];
        if (allocLen == 0) allocLen = 8;

        // Mode parameter header (8 bytes for MODE SENSE(10))
        byte[] data = new byte[8];
        data[0] = 0x00; // Mode data length MSB
        data[1] = 0x06; // Mode data length LSB (6 bytes follow)
        data[2] = 0x01; // Medium type: 120mm CD-ROM data only
        data[3] = 0x80; // Device-specific: write-protected
        data[4] = 0x00; // Reserved
        data[5] = 0x00; // Reserved
        data[6] = 0x00; // Block descriptor length MSB
        data[7] = 0x00; // Block descriptor length LSB

        int len = Math.Min(allocLen, 8);
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = TrimData(data, len), DataInLength = len, HasDataIn = true };
    }

    private ScsiResult CmdStartStopUnit()
    {
        ClearSense();
        return new ScsiResult { StatusByte = 0x00 };
    }

    private ScsiResult CmdPreventAllowRemoval()
    {
        ClearSense();
        return new ScsiResult { StatusByte = 0x00 };
    }

    private ScsiResult CmdReadCapacity()
    {
        byte[] data = new byte[8];
        uint lastLba = _totalSectors > 0 ? (uint)(_totalSectors - 1) : 0;
        // Last LBA (big-endian)
        data[0] = (byte)(lastLba >> 24);
        data[1] = (byte)(lastLba >> 16);
        data[2] = (byte)(lastLba >> 8);
        data[3] = (byte)lastLba;
        // Block size = 2048 (big-endian)
        data[4] = 0x00;
        data[5] = 0x00;
        data[6] = 0x08;
        data[7] = 0x00;
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = data, DataInLength = 8, HasDataIn = true };
    }

    private ScsiResult CmdRead6(byte[] cdb)
    {
        // READ(6) CDB: [0]=0x08 [1]=LBA(MSB, 5 bits) [2]=LBA [3]=LBA(LSB) [4]=count (0=256)
        uint lba = (uint)((cdb[1] & 0x1F) << 16 | cdb[2] << 8 | cdb[3]);
        int count = cdb[4] == 0 ? 256 : cdb[4];
        return DoRead(lba, count);
    }

    private ScsiResult CmdRead10(byte[] cdb)
    {
        uint lba = (uint)(cdb[2] << 24 | cdb[3] << 16 | cdb[4] << 8 | cdb[5]);
        int count = cdb[7] << 8 | cdb[8];
        if (count == 0)
        {
            ClearSense();
            return new ScsiResult { StatusByte = 0x00 };
        }
        return DoRead(lba, count);
    }

    /// <summary>
    /// READ TOC — returns a minimal single-track data CD table of contents.
    /// Format 0 (TOC): returns track descriptors + lead-out.
    /// NetBSD's cd driver issues this to discover the disc layout.
    /// </summary>
    private ScsiResult CmdReadToc(byte[] cdb)
    {
        int allocLen = cdb[7] << 8 | cdb[8];
        if (allocLen == 0) allocLen = 12;
        bool msf = (cdb[1] & 0x02) != 0;
        int format = cdb[2] & 0x0F; // Some drives use cdb[9] bits 6-7 for format
        int startTrack = cdb[6];

        // We support format 0 (standard TOC) only
        // Track 1 = data track starting at LBA 0
        // Track 0xAA = lead-out at end of disc

        // TOC header (4 bytes) + track 1 descriptor (8 bytes) + lead-out (8 bytes) = 20 bytes
        byte[] toc = new byte[20];

        // TOC Data Length (excludes first 2 bytes of header)
        int tocDataLen = 18;
        toc[0] = (byte)(tocDataLen >> 8);
        toc[1] = (byte)(tocDataLen & 0xFF);
        toc[2] = 0x01; // First track number
        toc[3] = 0x01; // Last track number

        // Track 1 descriptor
        toc[4] = 0x00; // Reserved
        toc[5] = 0x14; // ADR=1 (sub-channel Q), CONTROL=4 (data track, digital copy permitted)
        toc[6] = 0x01; // Track number
        toc[7] = 0x00; // Reserved

        if (msf)
        {
            // Track 1 start: MSF 00:02:00 (LBA 0 = 00:02:00 in absolute MSF)
            toc[8] = 0x00;  // Reserved
            toc[9] = 0x00;  // Minutes
            toc[10] = 0x02; // Seconds
            toc[11] = 0x00; // Frames
        }
        else
        {
            // Track 1 start: LBA 0
            toc[8] = 0x00;
            toc[9] = 0x00;
            toc[10] = 0x00;
            toc[11] = 0x00;
        }

        // Lead-out (track 0xAA)
        toc[12] = 0x00; // Reserved
        toc[13] = 0x14; // ADR=1, CONTROL=4 (data)
        toc[14] = 0xAA; // Lead-out track number
        toc[15] = 0x00; // Reserved

        uint leadOutLba = (uint)_totalSectors;
        if (msf)
        {
            LbaToMsf(leadOutLba, out byte m, out byte s, out byte f);
            toc[16] = 0x00;
            toc[17] = m;
            toc[18] = s;
            toc[19] = f;
        }
        else
        {
            toc[16] = (byte)(leadOutLba >> 24);
            toc[17] = (byte)(leadOutLba >> 16);
            toc[18] = (byte)(leadOutLba >> 8);
            toc[19] = (byte)leadOutLba;
        }

        int len = Math.Min(allocLen, 20);
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = TrimData(toc, len), DataInLength = len, HasDataIn = true };
    }

    private ScsiResult DoRead(uint lba, int sectorCount)
    {
        if (lba + sectorCount > _totalSectors)
            return MakeCheckCondition(0x05, 0x21, 0x00); // LBA OUT OF RANGE

        int byteCount = sectorCount * SectorSize;
        byte[] data = new byte[byteCount];
        _imageStream!.Seek((long)lba * SectorSize, SeekOrigin.Begin);
        int read = 0;
        while (read < byteCount)
        {
            int n = _imageStream.Read(data, read, byteCount - read);
            if (n == 0) break;
            read += n;
        }
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = data, DataInLength = byteCount, HasDataIn = true };
    }

    private ScsiResult MakeCheckCondition(byte senseKey, byte asc, byte ascq)
    {
        _senseData = new byte[18];
        _senseData[0] = 0x70;
        _senseData[2] = senseKey;
        _senseData[7] = 0x0A;
        _senseData[12] = asc;
        _senseData[13] = ascq;
        return new ScsiResult { StatusByte = 0x02 };
    }

    private void ClearSense()
    {
        Array.Clear(_senseData);
        _senseData[0] = 0x70;
        _senseData[7] = 0x0A;
    }

    private static void SetString(byte[] buf, int offset, int maxLen, string s)
    {
        for (int i = 0; i < maxLen; i++)
            buf[offset + i] = i < s.Length ? (byte)s[i] : (byte)' ';
    }

    private static byte[] TrimData(byte[] data, int len)
    {
        if (len >= data.Length) return data;
        byte[] trimmed = new byte[len];
        Array.Copy(data, trimmed, len);
        return trimmed;
    }

    private static void LbaToMsf(uint lba, out byte m, out byte s, out byte f)
    {
        // CD-ROM MSF: LBA 0 corresponds to absolute MSF 00:02:00 (2-second offset)
        uint adjusted = lba + 150; // 150 frames = 2 seconds
        f = (byte)(adjusted % 75);
        uint seconds = adjusted / 75;
        s = (byte)(seconds % 60);
        m = (byte)(seconds / 60);
    }
}
