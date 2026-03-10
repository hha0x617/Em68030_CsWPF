// Copyright 2026 hha0x617
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Em68030.IO;

using System.IO;

/// <summary>
/// SCSI disk target device. Processes SCSI CDBs against a file-backed disk image.
/// Sector size is fixed at 512 bytes.
/// </summary>
public class ScsiDisk : IScsiTarget
{
    private FileStream? _imageStream;
    private long _totalSectors;
    private const int SectorSize = 512;
    private byte[] _senseData = new byte[18];
    public Action<string>? DiagLog;

    public bool IsReady => _imageStream != null;

    public void MountImage(string path)
    {
        UnmountImage();
        _imageStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        _totalSectors = _imageStream.Length / SectorSize;
        if (_totalSectors == 0 && _imageStream.Length > 0)
            _totalSectors = 1;
        ClearSense();
    }

    public void UnmountImage()
    {
        _imageStream?.Dispose();
        _imageStream = null;
        _totalSectors = 0;
    }

    public ScsiResult ProcessCommand(byte[] cdb, int cdbLength, int lun = 0)
    {
        if (!IsReady)
            return MakeCheckCondition(0x02, 0x3A, 0x00); // NOT READY, MEDIUM NOT PRESENT

        // Only LUN 0 is supported. For other LUNs:
        // - INQUIRY returns "no device at this LUN" (device type 0x7F)
        // - REQUEST SENSE works normally (returns sense data)
        // - All other commands return CHECK CONDITION
        if (lun != 0)
        {
            byte opcode = cdb[0];
            if (opcode == 0x12) // INQUIRY
                return CmdInquiryNoDevice(cdb);
            if (opcode == 0x03) // REQUEST SENSE
                return CmdRequestSense(cdb);
            return MakeCheckCondition(0x05, 0x25, 0x00); // ILLEGAL REQUEST, LUN NOT SUPPORTED
        }

        byte op = cdb[0];
        var result = op switch
        {
            0x00 => CmdTestUnitReady(),
            0x03 => CmdRequestSense(cdb),
            0x08 => CmdRead6(cdb),
            0x0A => CmdWrite6(cdb),
            0x12 => CmdInquiry(cdb),
            0x15 => CmdModeSelect6(cdb),
            0x1A => CmdModeSense6(cdb),
            0x1B => CmdStartStopUnit(),
            0x1E => CmdPreventAllowRemoval(),
            0x25 => CmdReadCapacity(),
            0x28 => CmdRead10(cdb),
            0x2A => CmdWrite10(cdb),
            0x35 => CmdSynchronizeCache(),
            0x55 => CmdModeSelect10(cdb),
            0x5A => CmdModeSense10(cdb),
            _ => MakeCheckCondition(0x05, 0x20, 0x00) // ILLEGAL REQUEST, INVALID COMMAND
        };
        if (DiagLog != null)
        {
            var cdbHex = string.Join(" ", Enumerable.Range(0, cdbLength).Select(i => cdb[i].ToString("X2")));
            DiagLog($"[SD] op=${op:X2} lun={lun} status=${result.StatusByte:X2} din={result.DataInLength} dout={result.DataOutLength} CDB=[{cdbHex}]");
        }
        return result;
    }

    private ScsiResult CmdTestUnitReady()
    {
        ClearSense();
        return new ScsiResult { StatusByte = 0x00 }; // GOOD
    }

    private ScsiResult CmdRequestSense(byte[] cdb)
    {
        int allocLen = cdb[4];
        if (allocLen == 0) allocLen = 18;
        // Return allocLen bytes (zero-padded beyond 18) to match the transfer count
        // the driver sets up. Returning fewer bytes leaves a non-zero residual TC,
        // which causes the NetBSD wdsc driver to report EINVAL (error 22).
        byte[] data = new byte[allocLen];
        int copyLen = Math.Min(allocLen, 18);
        Array.Copy(_senseData, data, copyLen);
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = data, DataInLength = allocLen, HasDataIn = true };
    }

    private ScsiResult CmdInquiryNoDevice(byte[] cdb)
    {
        // INQUIRY response for non-existent LUN: device type 0x7F = no device
        int allocLen = cdb[4];
        if (allocLen == 0) allocLen = 36;
        byte[] data = new byte[36];
        data[0] = 0x7F; // Peripheral qualifier 011 + device type 11111 = no device
        data[4] = 0x1F; // Additional length = 31
        int len = Math.Min(allocLen, 36);
        if (len < 36)
        {
            byte[] trimmed = new byte[len];
            Array.Copy(data, trimmed, len);
            data = trimmed;
        }
        return new ScsiResult { StatusByte = 0x00, DataIn = data, DataInLength = len, HasDataIn = true };
    }

    private ScsiResult CmdInquiry(byte[] cdb)
    {
        int allocLen = cdb[4];
        if (allocLen == 0) allocLen = 36;
        byte[] data = new byte[36];
        data[0] = 0x00; // Direct access device
        data[1] = 0x00; // Not removable
        data[2] = 0x02; // SCSI-2
        data[3] = 0x02; // Response format 2
        data[4] = 0x1F; // Additional length = 31

        // Vendor (bytes 8-15): "EMULATED"
        SetString(data, 8, 8, "EMULATED");
        // Product (bytes 16-31): "SCSI DISK"
        SetString(data, 16, 16, "SCSI DISK");
        // Revision (bytes 32-35): "1.0 "
        SetString(data, 32, 4, "1.0 ");

        int len = Math.Min(allocLen, 36);
        if (len < 36)
        {
            byte[] trimmed = new byte[len];
            Array.Copy(data, trimmed, len);
            data = trimmed;
        }
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = data, DataInLength = len, HasDataIn = true };
    }

    private ScsiResult CmdModeSelect6(byte[] cdb)
    {
        // Accept and ignore MODE SELECT data
        int paramLen = cdb[4];
        ClearSense();
        if (paramLen > 0)
            return new ScsiResult { StatusByte = 0x00, DataOut = new byte[paramLen], DataOutLength = paramLen, HasDataOut = true };
        return new ScsiResult { StatusByte = 0x00 };
    }

    private ScsiResult CmdModeSense6(byte[] cdb)
    {
        bool dbd = (cdb[1] & 0x08) != 0; // Disable Block Descriptors
        int pageCode = cdb[2] & 0x3F;
        int allocLen = cdb[4];
        if (allocLen == 0) allocLen = 4;

        // Build mode page data
        var pages = BuildModePages(pageCode);

        // Block descriptor (8 bytes) unless DBD is set
        int bdLen = dbd ? 0 : 8;
        int totalLen = 4 + bdLen + pages.Length;
        byte[] data = new byte[totalLen];
        data[0] = (byte)(totalLen - 1); // Mode data length (excludes this byte)
        data[1] = 0x00; // Medium type
        data[2] = 0x00; // Device-specific parameter
        data[3] = (byte)bdLen; // Block descriptor length

        if (!dbd)
        {
            // Block descriptor: density=0, numBlocks, blockSize=512
            uint numBlocks = (uint)_totalSectors;
            data[4] = 0x00; // Density code
            data[5] = (byte)(numBlocks >> 16);
            data[6] = (byte)(numBlocks >> 8);
            data[7] = (byte)numBlocks;
            data[8] = 0x00; // Reserved
            data[9] = 0x00; // Block length MSB (512 = 0x000200)
            data[10] = 0x02;
            data[11] = 0x00;
        }

        Array.Copy(pages, 0, data, 4 + bdLen, pages.Length);

        int len = Math.Min(allocLen, totalLen);
        if (len < totalLen)
        {
            byte[] trimmed = new byte[len];
            Array.Copy(data, trimmed, len);
            data = trimmed;
        }
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = data, DataInLength = len, HasDataIn = true };
    }

    private ScsiResult CmdModeSense10(byte[] cdb)
    {
        bool dbd = (cdb[1] & 0x08) != 0;
        int pageCode = cdb[2] & 0x3F;
        int allocLen = (cdb[7] << 8) | cdb[8];
        if (allocLen == 0) allocLen = 8;

        var pages = BuildModePages(pageCode);

        int bdLen = dbd ? 0 : 8;
        int totalLen = 8 + bdLen + pages.Length; // 8-byte header for MODE SENSE(10)
        byte[] data = new byte[totalLen];
        // Mode data length (2 bytes, excludes these 2 bytes)
        int mdl = totalLen - 2;
        data[0] = (byte)(mdl >> 8);
        data[1] = (byte)mdl;
        data[2] = 0x00; // Medium type
        data[3] = 0x00; // Device-specific parameter
        data[6] = (byte)(bdLen >> 8);
        data[7] = (byte)bdLen;

        if (!dbd)
        {
            uint numBlocks = (uint)_totalSectors;
            data[8] = 0x00;
            data[9] = (byte)(numBlocks >> 16);
            data[10] = (byte)(numBlocks >> 8);
            data[11] = (byte)numBlocks;
            data[12] = 0x00;
            data[13] = 0x00;
            data[14] = 0x02;
            data[15] = 0x00;
        }

        Array.Copy(pages, 0, data, 8 + bdLen, pages.Length);

        int len = Math.Min(allocLen, totalLen);
        if (len < totalLen)
        {
            byte[] trimmed = new byte[len];
            Array.Copy(data, trimmed, len);
            data = trimmed;
        }
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = data, DataInLength = len, HasDataIn = true };
    }

    private ScsiResult CmdModeSelect10(byte[] cdb)
    {
        int paramLen = (cdb[7] << 8) | cdb[8];
        ClearSense();
        if (paramLen > 0)
            return new ScsiResult { StatusByte = 0x00, DataOut = new byte[paramLen], DataOutLength = paramLen, HasDataOut = true };
        return new ScsiResult { StatusByte = 0x00 };
    }

    private ScsiResult CmdStartStopUnit()
    {
        // Accept START STOP UNIT — disk is always ready
        ClearSense();
        return new ScsiResult { StatusByte = 0x00 };
    }

    private ScsiResult CmdPreventAllowRemoval()
    {
        // Accept PREVENT/ALLOW MEDIUM REMOVAL — non-removable disk, always OK
        ClearSense();
        return new ScsiResult { StatusByte = 0x00 };
    }

    private ScsiResult CmdSynchronizeCache()
    {
        // Flush the file stream
        _imageStream?.Flush();
        ClearSense();
        return new ScsiResult { StatusByte = 0x00 };
    }

    /// <summary>
    /// Build mode page data for the requested page code.
    /// Page 0x3F = return all pages.
    /// </summary>
    private byte[] BuildModePages(int pageCode)
    {
        // Geometry matching WriteNetBsdDisklabel
        int nsectors = 32;
        int ntracks = 64;
        int ncylinders = (int)(_totalSectors / (nsectors * ntracks));

        using var ms = new MemoryStream();

        if (pageCode == 0x03 || pageCode == 0x3F)
        {
            // Page 3: Format Device Parameters (24 bytes)
            byte[] p3 = new byte[24];
            p3[0] = 0x03;       // Page code
            p3[1] = 22;         // Page length
            p3[10] = 0;         // Sectors per track MSB
            p3[11] = (byte)nsectors; // Sectors per track LSB
            p3[12] = 0x02;      // Data bytes per sector MSB (512)
            p3[13] = 0x00;      // Data bytes per sector LSB
            ms.Write(p3, 0, 24);
        }

        if (pageCode == 0x04 || pageCode == 0x3F)
        {
            // Page 4: Rigid Disk Drive Geometry (24 bytes)
            byte[] p4 = new byte[24];
            p4[0] = 0x04;       // Page code
            p4[1] = 22;         // Page length
            p4[2] = (byte)(ncylinders >> 16); // Cylinders MSB
            p4[3] = (byte)(ncylinders >> 8);
            p4[4] = (byte)ncylinders;         // Cylinders LSB
            p4[5] = (byte)ntracks;            // Number of heads
            p4[20] = 0x0E;      // Rotation rate MSB (3600 RPM)
            p4[21] = 0x10;      // Rotation rate LSB
            ms.Write(p4, 0, 24);
        }

        return ms.ToArray();
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
        // Block size = 512 (big-endian)
        data[4] = 0x00;
        data[5] = 0x00;
        data[6] = 0x02;
        data[7] = 0x00;
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = data, DataInLength = 8, HasDataIn = true };
    }

    private ScsiResult CmdRead6(byte[] cdb)
    {
        uint lba = (uint)((cdb[1] & 0x1F) << 16 | cdb[2] << 8 | cdb[3]);
        int count = cdb[4];
        if (count == 0) count = 256;
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

    private ScsiResult CmdWrite6(byte[] cdb)
    {
        uint lba = (uint)((cdb[1] & 0x1F) << 16 | cdb[2] << 8 | cdb[3]);
        int count = cdb[4];
        if (count == 0) count = 256;
        return DoWrite(lba, count);
    }

    private ScsiResult CmdWrite10(byte[] cdb)
    {
        uint lba = (uint)(cdb[2] << 24 | cdb[3] << 16 | cdb[4] << 8 | cdb[5]);
        int count = cdb[7] << 8 | cdb[8];
        if (count == 0)
        {
            ClearSense();
            return new ScsiResult { StatusByte = 0x00 };
        }
        return DoWrite(lba, count);
    }

    private ScsiResult DoRead(uint lba, int sectorCount)
    {
        if (lba + sectorCount > _totalSectors)
            return MakeCheckCondition(0x05, 0x21, 0x00); // ILLEGAL REQUEST, LBA OUT OF RANGE

        int byteCount = sectorCount * SectorSize;
        byte[] data = new byte[byteCount];
        _imageStream!.Seek((long)lba * SectorSize, SeekOrigin.Begin);
        int read = 0;
        while (read < byteCount)
        {
            int n = _imageStream.Read(data, read, byteCount - read);
            if (n == 0) break; // EOF — rest stays as zeros
            read += n;
        }

        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataIn = data, DataInLength = byteCount, HasDataIn = true };
    }

    private ScsiResult DoWrite(uint lba, int sectorCount)
    {
        if (lba + sectorCount > _totalSectors)
            return MakeCheckCondition(0x05, 0x21, 0x00); // ILLEGAL REQUEST, LBA OUT OF RANGE

        int byteCount = sectorCount * SectorSize;
        // DataOut: caller will provide data
        byte[] buffer = new byte[byteCount];
        ClearSense();
        return new ScsiResult { StatusByte = 0x00, DataOut = buffer, DataOutLength = byteCount, HasDataOut = true };
    }

    /// <summary>
    /// Called after DATA_OUT phase completes to flush written data to disk.
    /// </summary>
    public void CompleteWrite(uint lba, byte[] data, int length)
    {
        if (_imageStream == null) return;
        _imageStream.Seek((long)lba * SectorSize, SeekOrigin.Begin);
        _imageStream.Write(data, 0, length);
        _imageStream.Flush();
    }

    private ScsiResult MakeCheckCondition(byte senseKey, byte asc, byte ascq)
    {
        _senseData = new byte[18];
        _senseData[0] = 0x70;       // Response code: current errors, fixed format
        _senseData[2] = senseKey;    // Sense key
        _senseData[7] = 0x0A;       // Additional sense length
        _senseData[12] = asc;       // ASC
        _senseData[13] = ascq;      // ASCQ
        return new ScsiResult { StatusByte = 0x02 }; // CHECK CONDITION
    }

    private void ClearSense()
    {
        Array.Clear(_senseData);
        _senseData[0] = 0x70;  // Response code
        _senseData[7] = 0x0A;  // Additional sense length
    }

    private static void SetString(byte[] buf, int offset, int maxLen, string s)
    {
        for (int i = 0; i < maxLen; i++)
            buf[offset + i] = i < s.Length ? (byte)s[i] : (byte)' ';
    }

    /// <summary>
    /// Writes a valid NetBSD disklabel to sector 0 of a disk image file.
    /// For mvme68k: LABELSECTOR=0, LABELOFFSET=0, big-endian byte order.
    /// Defines partition 'a' (FFS, root), 'b' (swap/miniroot), and 'c' (whole disk).
    /// </summary>
    /// <summary>
    /// Write a NetBSD/mvme68k cpu_disklabel to sector 0 of a disk image.
    /// The mvme68k port uses a Motorola BUG PROM compatible label format
    /// (struct cpu_disklabel) instead of the standard BSD disklabel.
    /// </summary>
    public static void WriteNetBsdDisklabel(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        long totalSectors = fs.Length / SectorSize;
        if (totalSectors < 1) return;

        // Geometry: fabricate like the kernel does
        int nsectors = 32;   // sectors per track
        int ntracks = 64;    // tracks per cylinder
        int secpercyl = nsectors * ntracks;
        int ncylinders = (int)(totalSectors / secpercyl);
        int secperunit = (int)totalSectors;

        byte[] sector = new byte[SectorSize];

        const uint DISKMAGIC = 0x82564557;
        int npartitions = 8; // MAXPARTITIONS on mvme68k

        // ===== VID block (block 0, offsets 0x00 - 0xFF) =====
        SetLabelString(sector, 0x00, 4, "NBSD");           // vid_id
        PutBE32(sector, 0x14, 2);                           // vid_oss
        PutBE16(sector, 0x18, 30);                          // vid_osl
        PutBE16(sector, 0x1E, 0x003F);                      // vid_osa_u
        PutBE16(sector, 0x20, 0x0000);                      // vid_osa_l
        PutBE16(sector, 0x24, (ushort)npartitions);          // partitions
        SetLabelString(sector, 0x26, 16, "EMULATED");        // vid_vd (typename)
        PutBE32(sector, 0x36, 8192);                         // bbsize
        PutBE32(sector, 0x3A, DISKMAGIC);                    // magic1
        PutBE16(sector, 0x3E, 4);                            // type = DTYPE_SCSI
        SetLabelString(sector, 0x42, 16, "NetBSD");          // packname
        PutBE32(sector, 0x80, (uint)secpercyl);              // secpercyl
        PutBE32(sector, 0x84, (uint)secperunit);             // secperunit
        PutBE32(sector, 0x90, 1);                            // vid_cas = 1
        sector[0x94] = 1;                                    // vid_cal = 1

        // Partitions 0-3 in vid_4[64] at offset 0x98 (each entry 16 bytes)
        // Partition b: 64 MB swap (also used for miniroot during installation)
        int swapSectors = Math.Min(131072, secperunit / 4); // 64 MB or 25% of disk
        swapSectors = Math.Max(swapSectors, 16384);          // minimum 8 MB
        int aSectors = secperunit - swapSectors;
        int bOffset = aSectors;

        int pa = 0x98;
        // a: root filesystem
        PutBE32(sector, pa + 0, (uint)aSectors);             // a: p_size
        PutBE32(sector, pa + 4, 0);                          // a: p_offset
        PutBE32(sector, pa + 8, 1024);                       // a: p_fsize
        sector[pa + 12] = 7;                                 // a: FS_BSDFFS
        sector[pa + 13] = 8;                                 // a: p_frag
        PutBE16(sector, pa + 14, 16);                        // a: p_cpg

        // b: swap (miniroot written here during installation Phase 1)
        PutBE32(sector, pa + 16 + 0, (uint)swapSectors);     // b: p_size
        PutBE32(sector, pa + 16 + 4, (uint)bOffset);         // b: p_offset
        sector[pa + 16 + 12] = 1;                            // b: FS_SWAP

        // c: whole disk
        PutBE32(sector, pa + 32 + 0, (uint)secperunit);      // c: p_size
        PutBE32(sector, pa + 32 + 4, 0);                     // c: p_offset

        PutBE32(sector, 0xF4, 8192);                         // sbsize
        SetLabelString(sector, 0xF8, 8, "MOTOROLA");         // vid_mot

        // ===== CFG area (block 1, offsets 0x100 - 0x1FF) =====
        PutBE16(sector, 0x10A, 256);                         // cfg_rec
        PutBE16(sector, 0x114, 3600);                        // rpm
        sector[0x118] = (byte)nsectors;                      // cfg_spt
        sector[0x119] = (byte)ntracks;                       // cfg_hds
        PutBE16(sector, 0x11A, (ushort)ncylinders);          // cfg_trk
        sector[0x11C] = 1;                                   // cfg_ilv
        PutBE16(sector, 0x11E, 512);                         // cfg_psm
        PutBE32(sector, 0x13C, DISKMAGIC);                   // magic2

        // Write sector 0
        fs.Seek(0, SeekOrigin.Begin);
        fs.Write(sector, 0, SectorSize);
        fs.Flush();
    }

    private static void PutBE32(byte[] buf, int offset, uint value)
    {
        buf[offset + 0] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void PutBE16(byte[] buf, int offset, ushort value)
    {
        buf[offset + 0] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static void SetLabelString(byte[] buf, int offset, int maxLen, string s)
    {
        for (int i = 0; i < maxLen; i++)
            buf[offset + i] = i < s.Length ? (byte)s[i] : (byte)0;
    }
}

public interface IScsiTarget
{
    bool IsReady { get; }
    ScsiResult ProcessCommand(byte[] cdb, int cdbLength, int lun = 0);
    void CompleteWrite(uint lba, byte[] data, int length);
}

public struct ScsiResult
{
    public byte StatusByte;       // 0x00=GOOD, 0x02=CHECK_CONDITION
    public byte[]? DataIn;        // DATA_IN phase data (null if none)
    public int DataInLength;      // Actual data length
    public byte[]? DataOut;       // DATA_OUT buffer (null if none)
    public int DataOutLength;     // Expected DATA_OUT byte count
    public bool HasDataIn;        // DATA_IN phase present
    public bool HasDataOut;       // DATA_OUT phase present
}
