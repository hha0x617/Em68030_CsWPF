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

using Em68030.Core;
using Em68030.IO;
using Xunit;

namespace Em68030.Tests.IoTests;

// ============================================================================
// Mock SCSI Target
// ============================================================================

public class MockScsiTarget : IScsiTarget
{
    public bool IsReady { get; set; } = true;
    public ScsiResult NextResult;
    public byte[] LastCdb = Array.Empty<byte>();
    public int LastCdbLength;
    public int LastLun;
    public int ProcessCount;

    // CompleteWrite tracking
    public uint LastWriteLba;
    public byte[] LastWriteData = Array.Empty<byte>();
    public int LastWriteLength;
    public int WriteCallCount;

    public ScsiResult ProcessCommand(byte[] cdb, int cdbLength, int lun = 0)
    {
        ProcessCount++;
        LastCdb = cdb[..cdbLength];
        LastCdbLength = cdbLength;
        LastLun = lun;
        return NextResult;
    }

    public void CompleteWrite(uint lba, byte[] data, int length)
    {
        WriteCallCount++;
        LastWriteLba = lba;
        LastWriteData = data[..length];
        LastWriteLength = length;
    }

    public void SetNoDataResult(byte status = 0x00)
    {
        NextResult = new ScsiResult { StatusByte = status };
    }

    public void SetDataInResult(byte[] data, byte status = 0x00)
    {
        NextResult = new ScsiResult
        {
            StatusByte = status,
            HasDataIn = true,
            DataIn = data,
            DataInLength = data.Length
        };
    }

    public void SetDataOutResult(int length, byte status = 0x00)
    {
        NextResult = new ScsiResult
        {
            StatusByte = status,
            HasDataOut = true,
            DataOutLength = length
        };
    }
}

// ============================================================================
// WD33C93 Device Tests
// ============================================================================

public class Wd33c93DeviceTests
{
    private readonly Wd33c93Device _device = new();
    private readonly MockScsiTarget _target0 = new();
    private readonly List<bool> _interruptHistory = new();

    private const uint Base = 0xFFFE4000;
    private const uint AddrPort = Base + 0;
    private const uint DataPort = Base + 1;

    public Wd33c93DeviceTests()
    {
        _device.InterruptOutput = active => _interruptHistory.Add(active);
    }

    // --- Register access helpers ---

    private void WriteReg(byte reg, byte value)
    {
        _device.WriteByte(AddrPort, reg);
        _device.WriteByte(DataPort, value);
    }

    private byte ReadReg(byte reg)
    {
        _device.WriteByte(AddrPort, reg);
        return _device.ReadByte(DataPort);
    }

    private byte ReadAsr() => _device.ReadByte(AddrPort);

    private byte ReadCsr() => ReadReg(0x17);

    private void SetTransferCount(int count)
    {
        WriteReg(0x12, (byte)((count >> 16) & 0xFF));
        WriteReg(0x13, (byte)((count >> 8) & 0xFF));
        WriteReg(0x14, (byte)(count & 0xFF));
    }

    private void SetDestId(int id) => WriteReg(0x15, (byte)(id & 0x07));

    private void IssueCommand(byte cmd) => WriteReg(0x18, cmd);

    private void AttachTarget0() => _device.AttachTarget(0, _target0);

    private void DoSelectAtn(int targetId = 0)
    {
        SetDestId(targetId);
        IssueCommand(0x06);
    }

    private void SbtSendByte(byte value)
    {
        IssueCommand(0xA0); // XFER_INFO | SBT → sbtPending, DBR set
        WriteReg(0x19, value); // Write data register → CompleteSbtOutput
    }

    private byte SbtRecvByte()
    {
        IssueCommand(0xA0);
        return ReadReg(0x19);
    }

    // ========================================================================
    // Basic Register Access
    // ========================================================================

    [Fact]
    public void AddressRegister_SelectsInternalReg()
    {
        WriteReg(0x00, 0x42);
        Assert.Equal(0x42, ReadReg(0x00));
    }

    [Fact]
    public void AddressRegister_AutoIncrements()
    {
        WriteReg(0x00, 0x11);
        WriteReg(0x01, 0x22);

        _device.WriteByte(AddrPort, 0x00);
        byte val0 = _device.ReadByte(DataPort); // reg 0x00, addr→0x01
        byte val1 = _device.ReadByte(DataPort); // reg 0x01, addr→0x02

        Assert.Equal(0x11, val0);
        Assert.Equal(0x22, val1);
    }

    [Fact]
    public void AddressRegister_Wraps()
    {
        WriteReg(0x00, 0xAB);
        _device.WriteByte(AddrPort, 0x1F);
        _device.ReadByte(DataPort); // read reg 0x1F, addr→0x00
        byte val = _device.ReadByte(DataPort); // reg 0x00
        Assert.Equal(0xAB, val);
    }

    // ========================================================================
    // ASR (Auxiliary Status Register)
    // ========================================================================

    [Fact]
    public void ASR_Default_NoInterrupt()
    {
        byte asr = ReadAsr();
        Assert.Equal(0, asr & 0x80); // No INT
        Assert.Equal(0, asr & 0x01); // No DBR
    }

    [Fact]
    public void ASR_INT_SetAfterCommand()
    {
        AttachTarget0();
        _target0.SetNoDataResult();
        DoSelectAtn(0);

        byte asr = ReadAsr();
        Assert.NotEqual(0, asr & 0x80); // INT set before CSR read
    }

    [Fact]
    public void CSR_Read_ClearsINT()
    {
        // Use RESET (no follow-up interrupt) to test CSR read clearing INT
        IssueCommand(0x00);

        Assert.NotEqual(0, ReadAsr() & 0x80);
        ReadCsr();
        Assert.Equal(0, ReadAsr() & 0x80);
    }

    [Fact]
    public void CSR_Read_DeassertsInterruptOutput()
    {
        // Use RESET (no follow-up interrupt) to test interrupt deassert
        IssueCommand(0x00);

        _interruptHistory.Clear();
        ReadCsr();

        Assert.NotEmpty(_interruptHistory);
        Assert.False(_interruptHistory.Last());
    }

    // ========================================================================
    // RESET Command (0x00)
    // ========================================================================

    [Fact]
    public void Reset_ReturnsCSR_0x01()
    {
        IssueCommand(0x00);
        Assert.Equal(0x01, ReadCsr());
    }

    [Fact]
    public void Reset_AssertsInterrupt()
    {
        IssueCommand(0x00);
        Assert.NotEqual(0, ReadAsr() & 0x80);
    }

    // ========================================================================
    // ABORT / DISCONNECT
    // ========================================================================

    [Fact]
    public void Abort_ReturnsCSR_0x41()
    {
        IssueCommand(0x01);
        Assert.Equal(0x41, ReadCsr());
    }

    [Fact]
    public void Disconnect_ReturnsCSR_0x41()
    {
        IssueCommand(0x04);
        Assert.Equal(0x41, ReadCsr());
    }

    // ========================================================================
    // Unknown Command
    // ========================================================================

    [Fact]
    public void UnknownCmd_ReturnsCSR_0x42()
    {
        IssueCommand(0x7F);
        Assert.Equal(0x42, ReadCsr());
    }

    // ========================================================================
    // SEL_ATN (0x06)
    // ========================================================================

    [Fact]
    public void SelAtn_NoTarget_ReturnsSelTimeout()
    {
        DoSelectAtn(0);
        Assert.Equal(0x42, ReadCsr());
    }

    [Fact]
    public void SelAtn_TargetNotReady_ReturnsSelTimeout()
    {
        _target0.IsReady = false;
        AttachTarget0();
        DoSelectAtn(0);
        Assert.Equal(0x42, ReadCsr());
    }

    [Fact]
    public void SelAtn_TargetReady_ReturnsCSR_0x11()
    {
        AttachTarget0();
        DoSelectAtn(0);
        Assert.Equal(0x11, ReadCsr());
    }

    [Fact]
    public void SelAtn_AssertsInterruptOutput()
    {
        AttachTarget0();
        _interruptHistory.Clear();
        SetDestId(0);
        IssueCommand(0x06); // SEL_ATN — don't read CSR

        Assert.NotEmpty(_interruptHistory);
        Assert.True(_interruptHistory.Last());
    }

    // ========================================================================
    // SEL_ATN_XFER (0x08) — Level II
    // ========================================================================

    [Fact]
    public void SelAtnXfer_NoTarget_ReturnsSelTimeout()
    {
        SetDestId(0);
        WriteReg(0x03, 0x00);
        IssueCommand(0x08);
        Assert.Equal(0x42, ReadCsr());
    }

    [Fact]
    public void SelAtnXfer_NoData_ReturnsSelXferDone()
    {
        AttachTarget0();
        _target0.SetNoDataResult();
        SetDestId(0);
        WriteReg(0x03, 0x00);
        IssueCommand(0x08);
        Assert.Equal(0x16, ReadCsr());
    }

    [Fact]
    public void SelAtnXfer_NoData_SetsStatusAndCmdPhase()
    {
        AttachTarget0();
        _target0.SetNoDataResult(0x00);
        SetDestId(0);
        WriteReg(0x03, 0x00);
        IssueCommand(0x08);

        ReadCsr();
        Assert.Equal(0x00, ReadReg(0x0F)); // Status byte
        Assert.Equal(0x60, ReadReg(0x10)); // Command phase complete
    }

    [Fact]
    public void SelAtnXfer_DataIn_ReturnsSrvReqDataIn()
    {
        AttachTarget0();
        _target0.SetDataInResult(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        SetDestId(0);
        WriteReg(0x03, 0x12); // INQUIRY
        IssueCommand(0x08);
        Assert.Equal(0x89, ReadCsr()); // SRV_REQ | DATA_IN
    }

    [Fact]
    public void SelAtnXfer_ReadsCdbFromRegisters()
    {
        AttachTarget0();
        _target0.SetNoDataResult();
        SetDestId(0);

        // READ(10) CDB
        WriteReg(0x03, 0x28);
        WriteReg(0x04, 0x00);
        WriteReg(0x05, 0x00);
        WriteReg(0x06, 0x01);
        WriteReg(0x07, 0x00);
        WriteReg(0x08, 0x00);
        WriteReg(0x09, 0x00);
        WriteReg(0x0A, 0x00);
        WriteReg(0x0B, 0x01);
        WriteReg(0x0C, 0x00);
        IssueCommand(0x08);

        Assert.Equal(1, _target0.ProcessCount);
        Assert.True(_target0.LastCdb.Length >= 10);
        Assert.Equal(0x28, _target0.LastCdb[0]);
        Assert.Equal(0x01, _target0.LastCdb[3]);
        Assert.Equal(0x01, _target0.LastCdb[8]);
    }

    [Fact]
    public void SelAtnXfer_CdbLength_Group0_Is6()
    {
        AttachTarget0();
        _target0.SetNoDataResult();
        SetDestId(0);
        WriteReg(0x03, 0x00);
        IssueCommand(0x08);
        Assert.Equal(6, _target0.LastCdbLength);
    }

    [Fact]
    public void SelAtnXfer_CdbLength_Group1_Is10()
    {
        AttachTarget0();
        _target0.SetNoDataResult();
        SetDestId(0);
        WriteReg(0x03, 0x28);
        IssueCommand(0x08);
        Assert.Equal(10, _target0.LastCdbLength);
    }

    // ========================================================================
    // Level I Full Flow (SBT)
    // ========================================================================

    [Fact]
    public void LevelI_FullReadFlow_SBT()
    {
        AttachTarget0();
        _target0.SetDataInResult(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

        // 1. Select
        DoSelectAtn(0);
        byte csr = ReadCsr();
        Assert.Equal(0x11, csr); // CSR_SELECT

        // 2. MsgOut via SBT — send IDENTIFY (LUN 0)
        SbtSendByte(0x80);
        csr = ReadCsr();
        Assert.Equal(0x1A, csr); // MIS | COMMAND

        // 3. Command — first SBT byte triggers ExecuteScsiCommand
        SbtSendByte(0x12); // INQUIRY opcode
        Assert.True(_target0.ProcessCount >= 1);

        csr = ReadCsr();
        Assert.Equal(0x19, csr); // MIS | DATA_IN
    }

    [Fact]
    public void LevelI_PIO_MsgOut_TransitionsToCommand()
    {
        AttachTarget0();
        _target0.SetNoDataResult();

        DoSelectAtn(0);
        ReadCsr(); // 0x11

        SetTransferCount(1);
        IssueCommand(0x20);

        Assert.NotEqual(0, ReadAsr() & 0x01); // DBR

        _device.WriteByte(DataPort, 0x80); // IDENTIFY

        byte csr = ReadCsr();
        Assert.Equal(0x1A, csr);
    }

    [Fact]
    public void LevelI_PIO_Command_ExecutesScsiCommand()
    {
        AttachTarget0();
        _target0.SetNoDataResult();

        DoSelectAtn(0); ReadCsr(); // 0x11
        SetTransferCount(1); IssueCommand(0x20);
        _device.WriteByte(DataPort, 0x80); // MsgOut PIO
        ReadCsr();

        SetTransferCount(6);
        IssueCommand(0x20);

        Assert.NotEqual(0, ReadAsr() & 0x01);

        for (int i = 0; i < 6; i++)
            _device.WriteByte(DataPort, 0x00);

        Assert.Equal(1, _target0.ProcessCount);

        byte csr = ReadCsr();
        Assert.Equal(0x1B, csr); // MIS | STATUS
    }

    [Fact]
    public void LevelI_StatusPhase_SBT_ReturnsStatusByte()
    {
        AttachTarget0();
        _target0.SetNoDataResult(0x02); // CHECK CONDITION

        DoSelectAtn(0); ReadCsr(); // 0x11
        SetTransferCount(1); IssueCommand(0x20);
        _device.WriteByte(DataPort, 0x80); ReadCsr();
        SetTransferCount(6); IssueCommand(0x20);
        for (int i = 0; i < 6; i++) _device.WriteByte(DataPort, 0x00);
        ReadCsr(); // STATUS

        byte statusByte = SbtRecvByte();
        Assert.Equal(0x02, statusByte);

        byte csr = ReadCsr();
        Assert.Equal(0x1F, csr); // MIS | MSG_IN
    }

    [Fact]
    public void LevelI_MsgInPhase_SBT_ReturnsCommandComplete()
    {
        AttachTarget0();
        _target0.SetNoDataResult();

        DoSelectAtn(0); ReadCsr(); // 0x11
        SetTransferCount(1); IssueCommand(0x20);
        _device.WriteByte(DataPort, 0x80); ReadCsr();
        SetTransferCount(6); IssueCommand(0x20);
        for (int i = 0; i < 6; i++) _device.WriteByte(DataPort, 0x00);
        ReadCsr();
        SbtRecvByte(); ReadCsr();

        byte msg = SbtRecvByte();
        Assert.Equal(0x00, msg);

        byte csr = ReadCsr();
        Assert.Equal(0x41, csr); // DISC
    }

    // ========================================================================
    // Level I DataIn via PIO
    // ========================================================================

    [Fact]
    public void LevelI_PIO_DataIn()
    {
        AttachTarget0();
        _target0.SetDataInResult(new byte[] { 0x11, 0x22, 0x33, 0x44 });

        DoSelectAtn(0); ReadCsr(); // 0x11
        SetTransferCount(1); IssueCommand(0x20);
        _device.WriteByte(DataPort, 0x80); ReadCsr();

        SetTransferCount(6); IssueCommand(0x20);
        byte[] cdb = { 0x12, 0x00, 0x00, 0x00, 0x04, 0x00 };
        for (int i = 0; i < 6; i++) _device.WriteByte(DataPort, cdb[i]);

        byte csr = ReadCsr();
        Assert.Equal(0x19, csr); // MIS | DATA_IN

        SetTransferCount(4);
        IssueCommand(0x20);

        Assert.NotEqual(0, ReadAsr() & 0x01);

        byte d0 = _device.ReadByte(DataPort);
        byte d1 = _device.ReadByte(DataPort);
        byte d2 = _device.ReadByte(DataPort);
        byte d3 = _device.ReadByte(DataPort);

        Assert.Equal(0x11, d0);
        Assert.Equal(0x22, d1);
        Assert.Equal(0x33, d2);
        Assert.Equal(0x44, d3);

        csr = ReadCsr();
        Assert.Equal(0x1B, csr); // MIS | STATUS
    }

    // ========================================================================
    // Transfer Count
    // ========================================================================

    [Fact]
    public void TransferCount_24bit()
    {
        WriteReg(0x12, 0x01);
        WriteReg(0x13, 0x02);
        WriteReg(0x14, 0x03);

        Assert.Equal(0x01, ReadReg(0x12));
        Assert.Equal(0x02, ReadReg(0x13));
        Assert.Equal(0x03, ReadReg(0x14));
    }

    // ========================================================================
    // Attach / Detach
    // ========================================================================

    [Fact]
    public void AttachDetach_Target()
    {
        AttachTarget0();
        DoSelectAtn(0);
        Assert.Equal(0x11, ReadCsr());

        _device.DetachTarget(0);
        DoSelectAtn(0);
        Assert.Equal(0x42, ReadCsr());
    }

    [Fact]
    public void AttachTarget_InvalidId_Ignored()
    {
        _device.AttachTarget(-1, _target0);
        _device.AttachTarget(8, _target0);
        DoSelectAtn(0);
        Assert.Equal(0x42, ReadCsr());
    }

    [Fact]
    public void MultipleTargets()
    {
        var target1 = new MockScsiTarget();
        target1.SetNoDataResult();
        _target0.SetNoDataResult();

        _device.AttachTarget(0, _target0);
        _device.AttachTarget(1, target1);

        DoSelectAtn(0);
        Assert.Equal(0x11, ReadCsr());
        DoSelectAtn(1);
        Assert.Equal(0x11, ReadCsr());
    }

    // ========================================================================
    // SBT — ASR.DBR
    // ========================================================================

    [Fact]
    public void SBT_Input_SetsDBR()
    {
        AttachTarget0();
        _target0.SetNoDataResult();

        DoSelectAtn(0); ReadCsr(); // 0x11
        SetTransferCount(1); IssueCommand(0x20);
        _device.WriteByte(DataPort, 0x80); ReadCsr();
        SetTransferCount(6); IssueCommand(0x20);
        for (int i = 0; i < 6; i++) _device.WriteByte(DataPort, 0x00);
        ReadCsr();

        // Status phase — issue SBT receive
        IssueCommand(0xA0);

        Assert.NotEqual(0, ReadAsr() & 0x01); // DBR set
    }

    // ========================================================================
    // Word/Long Access
    // ========================================================================

    [Fact]
    public void ReadWord_CombinesASRAndDataPort()
    {
        IssueCommand(0x00); // RESET → sets INT

        ushort word = _device.ReadWord(Base);
        byte hi = (byte)(word >> 8); // ASR
        Assert.NotEqual(0, hi & 0x80); // INT bit
    }

    // ========================================================================
    // Diagnostic Counters
    // ========================================================================

    [Fact]
    public void DiagnosticCounters()
    {
        _device.ReadByte(Base);
        _device.ReadByte(Base);
        Assert.Equal(2, _device.ReadCount);

        _device.WriteByte(Base, 0x00);
        Assert.Equal(1, _device.WriteCount);

        IssueCommand(0x00);
        Assert.Equal(1, _device.CommandCount);
    }
}

// ============================================================================
// DMA Integration Tests — SAT (SEL_ATN_XFER) with PCC DMA
// ============================================================================

public class Wd33c93DmaTests
{
    private const uint WdBase = 0xFFFE4000;
    private const uint WdAddr = WdBase + 0;
    private const uint WdData = WdBase + 1;
    private const uint PccBase = 0xFFFE1000;
    private const uint DmaBase = 0x00100000;

    private readonly Memory _memory;
    private readonly MC68030 _cpu;
    private readonly PccDevice _pcc;
    private readonly Wd33c93Device _wd = new();
    private readonly MockScsiTarget _target0 = new();
    private readonly List<bool> _interruptHistory = new();

    public Wd33c93DmaTests()
    {
        _memory = new Memory(16 * 1024 * 1024);
        _cpu = new MC68030(_memory);
        _pcc = new PccDevice(_cpu);
        _wd.AttachMemory(_memory);
        _wd.AttachPcc(_pcc);
        _wd.AttachTarget(0, _target0);
        _wd.InterruptOutput = active => _interruptHistory.Add(active);
    }

    private void WriteReg(byte reg, byte value)
    {
        _wd.WriteByte(WdAddr, reg);
        _wd.WriteByte(WdData, value);
    }

    private byte ReadReg(byte reg)
    {
        _wd.WriteByte(WdAddr, reg);
        return _wd.ReadByte(WdData);
    }

    private byte ReadCsr() => ReadReg(0x17);

    private void SetTransferCount(int count)
    {
        WriteReg(0x12, (byte)((count >> 16) & 0xFF));
        WriteReg(0x13, (byte)((count >> 8) & 0xFF));
        WriteReg(0x14, (byte)(count & 0xFF));
    }

    private int GetTransferCount()
    {
        return (ReadReg(0x12) << 16) | (ReadReg(0x13) << 8) | ReadReg(0x14);
    }

    private void SetDestId(int id) => WriteReg(0x15, (byte)(id & 0x07));

    private void IssueCommand(byte cmd) => WriteReg(0x18, cmd);

    private void SetupPccDma(uint addr, int count)
    {
        _pcc.WriteByte(PccBase + 0x04, (byte)(addr >> 24));
        _pcc.WriteByte(PccBase + 0x05, (byte)(addr >> 16));
        _pcc.WriteByte(PccBase + 0x06, (byte)(addr >> 8));
        _pcc.WriteByte(PccBase + 0x07, (byte)addr);
    }

    private void WriteCdb6(byte op, uint lba, byte count)
    {
        WriteReg(0x03, op);
        WriteReg(0x04, (byte)((lba >> 16) & 0x1F));
        WriteReg(0x05, (byte)((lba >> 8) & 0xFF));
        WriteReg(0x06, (byte)(lba & 0xFF));
        WriteReg(0x07, count);
        WriteReg(0x08, 0x00);
    }

    private void IssueSat()
    {
        SetDestId(0);
        IssueCommand(0x08);
    }

    [Fact]
    public void Sat_DataIn_SingleSegment_TransfersData()
    {
        var testData = new byte[512];
        for (int i = 0; i < 512; i++) testData[i] = (byte)(i & 0xFF);
        _target0.SetDataInResult(testData);

        WriteCdb6(0x08, 0, 1);
        SetTransferCount(512);
        SetupPccDma(DmaBase, 512);
        WriteReg(0x10, 0x00);

        IssueSat();

        Assert.Equal(0x16, ReadCsr()); // SEL_XFER_DONE

        for (int i = 0; i < 512; i++)
        {
            byte actual = _memory.PeekByte(DmaBase + (uint)i);
            Assert.True(actual == testData[i], $"Mismatch at offset {i}: expected {testData[i]}, got {actual}");
            if (actual != testData[i]) break;
        }

        Assert.Equal(0, GetTransferCount());
        Assert.Equal(0x00, ReadReg(0x0F)); // Status GOOD
        Assert.Equal(0x60, ReadReg(0x10)); // Command phase done
    }

    [Fact]
    public void Sat_DataIn_ScatterGather_TwoSegments()
    {
        var testData = new byte[1024];
        for (int i = 0; i < 1024; i++) testData[i] = (byte)((i * 7 + 3) & 0xFF);
        _target0.SetDataInResult(testData);

        WriteCdb6(0x08, 0, 2);

        // First segment
        SetTransferCount(512);
        SetupPccDma(DmaBase, 512);
        WriteReg(0x10, 0x00);
        IssueSat();

        Assert.Equal(0x89, ReadCsr()); // SRV_REQ|DATA_IN
        Assert.Equal(0, GetTransferCount());

        for (int i = 0; i < 512; i++)
        {
            Assert.True(_memory.PeekByte(DmaBase + (uint)i) == testData[i],
                $"Segment 1 mismatch at offset {i}");
            if (_memory.PeekByte(DmaBase + (uint)i) != testData[i]) break;
        }

        // Second segment — cmdPhase=0x45
        SetTransferCount(512);
        SetupPccDma(DmaBase + 512, 512);
        WriteReg(0x10, 0x45);
        IssueSat();

        Assert.Equal(0x16, ReadCsr()); // SEL_XFER_DONE

        for (int i = 0; i < 512; i++)
        {
            Assert.True(_memory.PeekByte(DmaBase + 512 + (uint)i) == testData[512 + i],
                $"Segment 2 mismatch at offset {i}");
            if (_memory.PeekByte(DmaBase + 512 + (uint)i) != testData[512 + i]) break;
        }

        Assert.Equal(0x60, ReadReg(0x10));
    }

    [Fact]
    public void Sat_DataIn_TcReflectsRemainingBytes()
    {
        var testData = new byte[1024];
        for (int i = 0; i < 1024; i++) testData[i] = (byte)(i & 0xFF);
        _target0.SetDataInResult(testData);

        WriteCdb6(0x08, 0, 2);
        SetTransferCount(256);
        SetupPccDma(DmaBase, 256);
        WriteReg(0x10, 0x00);

        IssueSat();

        Assert.Equal(0x89, ReadCsr()); // SRV_REQ — data remaining
        Assert.Equal(0, GetTransferCount());
    }

    [Fact]
    public void Sat_DataOut_SingleSegment()
    {
        _target0.SetDataOutResult(512);

        for (int i = 0; i < 512; i++)
            _memory.PokeByte(DmaBase + (uint)i, (byte)((i + 0x55) & 0xFF));

        WriteReg(0x03, 0x0A);
        WriteReg(0x04, 0x00);
        WriteReg(0x05, 0x00);
        WriteReg(0x06, 0x00);
        WriteReg(0x07, 0x01);
        WriteReg(0x08, 0x00);

        SetTransferCount(512);
        SetupPccDma(DmaBase, 512);
        WriteReg(0x10, 0x00);

        IssueSat();

        Assert.Equal(0x16, ReadCsr());
        Assert.Equal(0, GetTransferCount());
        Assert.Equal(0x60, ReadReg(0x10));
    }

    [Fact]
    public void Sat_DataOut_ScatterGather()
    {
        _target0.SetDataOutResult(1024);

        for (int i = 0; i < 512; i++)
        {
            _memory.PokeByte(DmaBase + (uint)i, (byte)(i & 0xFF));
            _memory.PokeByte(DmaBase + 0x1000 + (uint)i, (byte)((i + 0x80) & 0xFF));
        }

        WriteReg(0x03, 0x0A);
        WriteReg(0x04, 0x00);
        WriteReg(0x05, 0x00);
        WriteReg(0x06, 0x00);
        WriteReg(0x07, 0x02);
        WriteReg(0x08, 0x00);

        SetTransferCount(512);
        SetupPccDma(DmaBase, 512);
        WriteReg(0x10, 0x00);
        IssueSat();

        Assert.Equal(0x88, ReadCsr()); // SRV_REQ|DATA_OUT

        SetTransferCount(512);
        SetupPccDma(DmaBase + 0x1000, 512);
        WriteReg(0x10, 0x45);
        IssueSat();

        Assert.Equal(0x16, ReadCsr());
        Assert.Equal(0x60, ReadReg(0x10));
    }

    [Fact]
    public void Sat_NoData_CompletesImmediately()
    {
        _target0.SetNoDataResult(0x00);
        WriteCdb6(0x00, 0, 0);
        SetTransferCount(0);
        WriteReg(0x10, 0x00);

        IssueSat();

        Assert.Equal(0x16, ReadCsr());
        Assert.Equal(0x00, ReadReg(0x0F));
        Assert.Equal(0x60, ReadReg(0x10));
    }

    [Fact]
    public void Sat_CheckCondition_ReportsStatus()
    {
        _target0.SetNoDataResult(0x02);
        WriteCdb6(0x00, 0, 0);
        SetTransferCount(0);
        WriteReg(0x10, 0x00);

        IssueSat();

        Assert.Equal(0x16, ReadCsr());
        Assert.Equal(0x02, ReadReg(0x0F));
    }

    [Fact]
    public void Sat_TargetNotReady_SelectionTimeout()
    {
        _target0.IsReady = false;
        WriteCdb6(0x00, 0, 0);
        SetTransferCount(0);
        WriteReg(0x10, 0x00);

        IssueSat();

        Assert.Equal(0x42, ReadCsr());
    }

    [Fact]
    public void Sat_DataIn_ThreeSegments()
    {
        var testData = new byte[768];
        for (int i = 0; i < 768; i++) testData[i] = (byte)((i * 13 + 5) & 0xFF);
        _target0.SetDataInResult(testData);

        WriteCdb6(0x08, 0, 2);

        // Segment 1
        SetTransferCount(256);
        SetupPccDma(DmaBase, 256);
        WriteReg(0x10, 0x00);
        IssueSat();
        Assert.Equal(0x89, ReadCsr());

        // Segment 2
        SetTransferCount(256);
        SetupPccDma(DmaBase + 256, 256);
        WriteReg(0x10, 0x45);
        IssueSat();
        Assert.Equal(0x89, ReadCsr());

        // Segment 3 (final)
        SetTransferCount(256);
        SetupPccDma(DmaBase + 512, 256);
        WriteReg(0x10, 0x45);
        IssueSat();
        Assert.Equal(0x16, ReadCsr());

        for (int i = 0; i < 768; i++)
        {
            Assert.True(_memory.PeekByte(DmaBase + (uint)i) == testData[i],
                $"Data mismatch at offset {i}");
            if (_memory.PeekByte(DmaBase + (uint)i) != testData[i]) break;
        }
    }

    [Fact]
    public void Sat_CdbGroup2_10ByteCdb()
    {
        _target0.SetNoDataResult();

        WriteReg(0x03, 0x28);
        WriteReg(0x04, 0x00);
        WriteReg(0x05, 0x00);
        WriteReg(0x06, 0x00);
        WriteReg(0x07, 0x01);
        WriteReg(0x08, 0x00);
        WriteReg(0x09, 0x00);
        WriteReg(0x0A, 0x01);
        WriteReg(0x0B, 0x00);
        WriteReg(0x0C, 0x00);

        SetTransferCount(0);
        WriteReg(0x10, 0x00);

        IssueSat();
        ReadCsr();

        Assert.Equal(10, _target0.LastCdbLength);
        Assert.Equal(0x28, _target0.LastCdb[0]);
    }
}
