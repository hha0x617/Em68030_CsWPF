namespace Em68030.IO;

using Em68030.Core;

/// <summary>
/// WD33C93 SCSI controller emulation for MVME147.
/// Implements Level I protocol (phase-by-phase transfers) as expected by
/// the NetBSD sbic driver (sbic.c).
///
/// Mapped at $FFFE4000, 2 ports:
///   offset $0 write = Address Register (selects internal register)
///   offset $0 read  = Auxiliary Status Register (ASR)
///   offset $1 read/write = Data port (indirect register access, or PIO SCSI data)
///
/// Key internal registers (accessed indirectly via data port):
///   $00 = Own ID           $01 = Control
///   $0F = Target LUN (TLUN) — also used for status byte storage
///   $10 = Command Phase     $12-$14 = Transfer Count (TC, 24-bit)
///   $15 = Destination ID    $17 = SCSI Status (CSR) — reading clears INT
///   $18 = Command           $19 = Data
///
/// ASR bits (read from offset $0):
///   bit 7 = INT (interrupt pending)
///   bit 5 = BSY (busy)
///   bit 4 = CIP (command in progress)
///   bit 1 = PE  (parity error)
///   bit 0 = DBR (data buffer ready for PIO)
///
/// SBT (Single Byte Transfer) mode:
///   When XFER_INFO is issued with bit 7 set (SBT), exactly one byte is
///   transferred using the Data register ($19):
///   - Output phases (MSG_OUT, CMD, DATA_OUT): byte from $19 is sent to SCSI bus
///   - Input phases (DATA_IN, STATUS, MSG_IN): byte from SCSI bus is placed in $19
///   The transfer completes immediately (no PIO polling needed).
///   NetBSD uses SEND_BYTE (SBT out) and RECV_BYTE (SBT in) macros.
/// </summary>
public class Wd33c93Device : IMemoryMappedDevice
{
    private const uint BaseAddress = 0xFFFE4000;

    // Internal registers (32 regs, $00-$1F)
    private byte _addressReg;
    private readonly byte[] _regs = new byte[0x20];

    // External references
    private Memory? _memory;
    private PccDevice? _pcc;
    private readonly IScsiTarget?[] _targets = new IScsiTarget?[8];

    // SCSI bus state machine
    private enum ScsiPhase { Idle, MsgOut, Command, DataIn, DataOut, Status, MsgIn }
    private ScsiPhase _phase = ScsiPhase.Idle;
    private bool _pioTransferActive;

    // SBT (Single Byte Transfer) two-stage handshake state.
    // On real WD33C93, SBT + XFER_INFO sets DBR to signal the driver to
    // write (output) or read (input) the data register. The transfer
    // completes only after the driver accesses the data register.
    private bool _sbtPending;
    private bool _sbtOutput; // true = output (driver writes $19), false = input (driver reads $19)

    // CDB accumulation
    private byte[] _cdb = new byte[12];
    private int _cdbLength;
    private int _cdbOffset;

    // Data transfer buffer
    private byte[] _dataBuffer = Array.Empty<byte>();
    private int _dataOffset;
    private int _dataLength;

    // SCSI result state
    private byte _statusByte;
    private ScsiResult _currentResult;
    private int _selectedTarget = -1;
    private int _selectedLun = 0;

    // Write command tracking (for deferred disk writes)
    private uint _writeLba;
    private int _writeSectorCount;

    // Diagnostics
    public Action<bool>? InterruptOutput;
    public Action<string>? DiagLog;
    public int CommandCount { get; private set; }
    public int ReadCount { get; private set; }
    public int WriteCount { get; private set; }
    private int _scsiCmdLogCount; // limit detailed SCSI command logs

    public Wd33c93Device()
    {
        _regs[0x1F] = 0x00; // ASR: not busy, no interrupt
    }

    // --- External device attachment ---

    public void AttachMemory(Memory memory) { _memory = memory; }
    public void AttachPcc(PccDevice pcc) { _pcc = pcc; }
    public void AttachTarget(int scsiId, IScsiTarget target)
    {
        if (scsiId >= 0 && scsiId < 8)
            _targets[scsiId] = target;
    }

    public void DetachTarget(int scsiId)
    {
        if (scsiId >= 0 && scsiId < 8)
            _targets[scsiId] = null;
    }

    // Backward-compatible alias
    public void AttachDisk(int scsiId, ScsiDisk disk) => AttachTarget(scsiId, disk);

    // --- Register access ---

    public byte ReadByte(uint address)
    {
        ReadCount++;
        uint offset = address - BaseAddress;
        byte val = offset switch
        {
            0 => GetAsr(),
            1 => ReadDataPort(),
            _ => 0
        };
        if (ReadCount <= 50)
            DiagLog?.Invoke($"[SCSI] R off={offset} addr=${_addressReg:X2} val=${val:X2} (#{ReadCount})");
        return val;
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
        WriteCount++;
        uint offset = address - BaseAddress;
        if (WriteCount <= 50)
            DiagLog?.Invoke($"[SCSI] W off={offset} val=${value:X2} addr=${_addressReg:X2} (#{WriteCount})");
        switch (offset)
        {
            case 0:
                _addressReg = value;
                break;
            case 1:
                WriteDataPort(value);
                break;
        }
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

    // --- ASR (Auxiliary Status Register) ---

    private byte GetAsr()
    {
        // Build ASR from current state
        byte asr = 0;
        if ((_regs[0x1F] & 0x80) != 0) asr |= 0x80; // INT
        if (_pioTransferActive || _sbtPending) asr |= 0x01; // DBR (bit 0)
        return asr;
    }

    // --- Data port read ---

    private byte ReadDataPort()
    {
        if (_pioTransferActive)
        {
            return HandlePioDataRead();
        }

        byte idx = (byte)(_addressReg & 0x1F);
        byte val = _regs[idx];

        // Auto-increment address register
        _addressReg = (byte)((_addressReg + 1) & 0x1F);

        if (idx == 0x17)
        {
            // Reading CSR clears INT and deasserts interrupt
            _regs[0x1F] &= 0x7F;
            InterruptOutput?.Invoke(false);
        }

        // SBT input completion: driver has read the data byte from $19
        if (_sbtPending && !_sbtOutput && idx == 0x19)
        {
            CompleteSbtInput();
        }

        return val;
    }

    // --- Data port write ---

    private void WriteDataPort(byte value)
    {
        if (_pioTransferActive)
        {
            HandlePioDataWrite(value);
            return;
        }

        byte reg = (byte)(_addressReg & 0x1F);
        _regs[reg] = value;

        // Auto-increment address register
        _addressReg = (byte)((_addressReg + 1) & 0x1F);

        // SBT output completion: driver has written the data byte to $19
        if (_sbtPending && _sbtOutput && reg == 0x19)
        {
            CompleteSbtOutput(value);
            return;
        }

        if (reg == 0x18)
            HandleCommand(value);
    }

    // --- Transfer Count helpers ---

    private int GetTransferCount()
    {
        return (_regs[0x12] << 16) | (_regs[0x13] << 8) | _regs[0x14];
    }

    private void SetTransferCount(int count)
    {
        _regs[0x12] = (byte)((count >> 16) & 0xFF);
        _regs[0x13] = (byte)((count >> 8) & 0xFF);
        _regs[0x14] = (byte)(count & 0xFF);
    }

    // --- Interrupt signalling ---

    private void SetCsrAndInterrupt(byte csr)
    {
        _regs[0x17] = csr;
        _regs[0x1F] |= 0x80; // ASR.INT = 1
        InterruptOutput?.Invoke(true);
    }

    // --- Command handler ---

    private void HandleCommand(byte cmd)
    {
        CommandCount++;
        // Bit 7 = SBT (Single Byte Transfer) modifier — used with XFER_INFO
        bool sbt = (cmd & 0x80) != 0;
        byte baseCmd = (byte)(cmd & 0x7F);
        DiagLog?.Invoke($"[SCSI] cmd=${cmd:X2} phase={_phase} target={_regs[0x15] & 0x07} sbt={sbt} (#{CommandCount})");
        switch (baseCmd)
        {
            case 0x00: // RESET
                HandleReset();
                break;
            case 0x01: // ABORT
                HandleAbort();
                break;
            case 0x04: // DISCONNECT
                HandleDisconnect();
                break;
            case 0x06: // SEL_ATN — Select with ATN
                HandleSelectAtn();
                break;
            case 0x08: // SEL_ATN_XFER — Select and Transfer (used for status/completion)
                HandleSelAtnXfer();
                break;
            case 0x20: // XFER_INFO — Transfer Info (phase-based)
                HandleXferInfo(sbt);
                break;
            default:
                // Unknown command: selection timeout
                DiagLog?.Invoke($"[SCSI] Unknown cmd ${cmd:X2}, returning SEL_TIMEO");
                SetCsrAndInterrupt(0x42);
                break;
        }
    }

    // --- ABORT (0x01) / DISCONNECT (0x04) ---

    private void HandleAbort()
    {
        _phase = ScsiPhase.Idle;
        _pioTransferActive = false;
        _sbtPending = false;
        _selectedTarget = -1;
        SetCsrAndInterrupt(0x41); // DISC
    }

    private void HandleDisconnect()
    {
        _phase = ScsiPhase.Idle;
        _pioTransferActive = false;
        _sbtPending = false;
        _selectedTarget = -1;
        SetCsrAndInterrupt(0x41); // DISC
    }

    // --- RESET (0x00) ---

    private void HandleReset()
    {
        _phase = ScsiPhase.Idle;
        _pioTransferActive = false;
        _sbtPending = false;
        _selectedTarget = -1;
        _cdbOffset = 0;
        _dataOffset = 0;
        _dataLength = 0;
        // CSR = 0x01 (RESET_AM — reset with advanced features)
        SetCsrAndInterrupt(0x01);
    }

    // --- SEL_ATN (0x06) — Select with Attention ---

    private void HandleSelectAtn()
    {
        int target = _regs[0x15] & 0x07;
        DiagLog?.Invoke($"[SCSI] SEL_ATN target={target} hasTarget={_targets[target] != null} ready={_targets[target]?.IsReady}");

        if (_targets[target] == null || !_targets[target]!.IsReady)
        {
            _selectedTarget = -1;
            _phase = ScsiPhase.Idle;
            // CSR = 0x42 (SEL_TIMEO — Selection Timeout)
            SetCsrAndInterrupt(0x42);
        }
        else
        {
            _selectedTarget = target;
            _selectedLun = 0;
            _phase = ScsiPhase.MsgOut;
            _cdbOffset = 0;
            _cdbLength = 0;
            // CSR = 0x8E (MIS_2 | MESG_OUT phase) — MIS_2 after SEL_ATN
            // Phase bits: 110 = MSG_OUT
            SetCsrAndInterrupt(0x8E);
        }
    }

    // --- SEL_ATN_XFER (0x08) — Select-and-Transfer completion ---
    // NetBSD's sbicxfdone() issues this with cmd_phase=0x46 to complete
    // STATUS + MSG_IN + DISCONNECT automatically.

    private void HandleSelAtnXfer()
    {
        if (_selectedTarget < 0)
        {
            // No target selected — timeout
            SetCsrAndInterrupt(0x42);
            return;
        }

        // Store status byte in TLUN register ($0F)
        _regs[0x0F] = _statusByte;
        // Set cmd_phase to 0x60 (complete)
        _regs[0x10] = 0x60;
        _phase = ScsiPhase.Idle;
        _selectedTarget = -1;
        // CSR = 0x16 (S_XFERRED — Select-and-Transfer completed)
        SetCsrAndInterrupt(0x16);
    }

    // --- XFER_INFO (0x20) — Phase-based transfer ---

    private void HandleXferInfo(bool sbt = false)
    {
        int tc = GetTransferCount();
        if (sbt)
        {
            // SBT: force single byte transfer regardless of TC
            tc = 1;
            SetTransferCount(1);
        }
        DiagLog?.Invoke($"[SCSI] XFER_INFO phase={_phase} TC={tc} ctrl=${_regs[0x01]:X2} sbt={sbt}");

        // SBT mode: transfer one byte immediately using the Data register ($19).
        // On real WD33C93, SBT transfers the byte without entering PIO polling mode.
        // SEND_BYTE writes data reg then issues XFER_INFO|SBT — byte sent from $19.
        // RECV_BYTE issues XFER_INFO|SBT then reads data reg — byte received into $19.
        if (sbt)
        {
            HandleSbtTransfer();
            return;
        }

        switch (_phase)
        {
            case ScsiPhase.MsgOut:
                // PIO: driver sends IDENTIFY message
                StartPioTransfer(tc);
                break;

            case ScsiPhase.Command:
                // PIO: driver sends CDB bytes
                StartPioTransfer(tc);
                break;

            case ScsiPhase.DataIn:
                // DMA or PIO: send data to host
                if (IsDmaMode())
                    DoDmaDataIn();
                else
                    StartPioTransfer(tc);
                break;

            case ScsiPhase.DataOut:
                // DMA or PIO: receive data from host
                if (IsDmaMode())
                    DoDmaDataOut();
                else
                    StartPioTransfer(tc);
                break;

            case ScsiPhase.Status:
                // PIO: send status byte
                _dataBuffer = new byte[] { _statusByte };
                _dataOffset = 0;
                _dataLength = 1;
                StartPioTransfer(tc);
                break;

            case ScsiPhase.MsgIn:
                // PIO: send COMMAND COMPLETE message (0x00)
                _dataBuffer = new byte[] { 0x00 };
                _dataOffset = 0;
                _dataLength = 1;
                StartPioTransfer(tc);
                break;

            default:
                DiagLog?.Invoke($"[SCSI] XFER_INFO unexpected phase {_phase}");
                SetCsrAndInterrupt(0x42);
                break;
        }
    }

    /// <summary>
    /// Handle SBT (Single Byte Transfer) mode — stage 1.
    /// Sets DBR to signal the driver, then waits for the driver to access $19.
    ///
    /// For output phases (MsgOut, Command, DataOut):
    ///   Sets DBR. When the driver writes to $19, CompleteSbtOutput processes
    ///   the byte and completes the phase.
    ///
    /// For input phases (DataIn, Status, MsgIn):
    ///   Puts the data byte in $19, sets DBR. When the driver reads $19,
    ///   CompleteSbtInput completes the phase.
    /// </summary>
    private void HandleSbtTransfer()
    {
        switch (_phase)
        {
            case ScsiPhase.MsgOut:
            case ScsiPhase.Command:
            case ScsiPhase.DataOut:
                // Output: set DBR and wait for driver to write data register
                _sbtPending = true;
                _sbtOutput = true;
                // DBR is now reported via GetAsr()
                break;

            case ScsiPhase.DataIn:
                // Input: put next data byte into _regs[0x19] for driver to read
                if (_dataBuffer != null && _dataOffset < _dataLength)
                    _regs[0x19] = _dataBuffer[_dataOffset++];
                else
                    _regs[0x19] = 0;
                _sbtPending = true;
                _sbtOutput = false;
                break;

            case ScsiPhase.Status:
                // Input: put status byte into _regs[0x19]
                _regs[0x19] = _statusByte;
                DiagLog?.Invoke($"[SCSI] SBT Status: ${_statusByte:X2}");
                _sbtPending = true;
                _sbtOutput = false;
                break;

            case ScsiPhase.MsgIn:
                // Input: put COMMAND COMPLETE (0x00) into _regs[0x19]
                _regs[0x19] = 0x00;
                _sbtPending = true;
                _sbtOutput = false;
                break;

            default:
                DiagLog?.Invoke($"[SCSI] SBT unexpected phase {_phase}");
                SetCsrAndInterrupt(0x42);
                break;
        }
    }

    /// <summary>
    /// SBT output stage 2: driver has written the data byte to $19.
    /// Process the byte and complete the phase transition.
    /// </summary>
    private void CompleteSbtOutput(byte value)
    {
        _sbtPending = false;

        switch (_phase)
        {
            case ScsiPhase.MsgOut:
                if ((value & 0x80) != 0)
                    _selectedLun = value & 0x07;
                DiagLog?.Invoke($"[SCSI] SBT MsgOut: ${value:X2} (IDENTIFY lun={_selectedLun})");
                break;

            case ScsiPhase.Command:
                if (_cdbOffset < _cdb.Length)
                    _cdb[_cdbOffset++] = value;
                break;

            case ScsiPhase.DataOut:
                if (_dataBuffer != null && _dataOffset < _dataLength)
                    _dataBuffer[_dataOffset++] = value;
                break;
        }

        SetTransferCount(0);
        CompletePhaseTransfer();
    }

    /// <summary>
    /// SBT input stage 2: driver has read the data byte from $19.
    /// Complete the phase transition.
    /// </summary>
    private void CompleteSbtInput()
    {
        _sbtPending = false;
        SetTransferCount(0);
        CompletePhaseTransfer();
    }

    private bool IsDmaMode()
    {
        return (_regs[0x01] & 0x80) != 0; // Control register bit 7 = DMA mode
    }

    // --- PIO Transfer ---

    private void StartPioTransfer(int tc)
    {
        _pioTransferActive = true;
        // DBR is now indicated via GetAsr()
    }

    private byte HandlePioDataRead()
    {
        // Reading data from SCSI bus (DATA_IN, STATUS, MSG_IN phases)
        byte val = 0;
        if (_dataOffset < _dataLength && _dataBuffer != null)
        {
            val = _dataBuffer[_dataOffset++];
        }

        DecrementTcAndCheck();
        return val;
    }

    private void HandlePioDataWrite(byte value)
    {
        // Writing data to SCSI bus (MSG_OUT, COMMAND, DATA_OUT phases)
        switch (_phase)
        {
            case ScsiPhase.MsgOut:
                // IDENTIFY message byte: bit 7=1, bits 2-0 = LUN
                if ((value & 0x80) != 0)
                    _selectedLun = value & 0x07;
                DecrementTcAndCheck();
                break;

            case ScsiPhase.Command:
                if (_cdbOffset < _cdb.Length)
                    _cdb[_cdbOffset++] = value;
                DecrementTcAndCheck();
                break;

            case ScsiPhase.DataOut:
                if (_dataBuffer != null && _dataOffset < _dataLength)
                    _dataBuffer[_dataOffset++] = value;
                DecrementTcAndCheck();
                break;

            default:
                DecrementTcAndCheck();
                break;
        }
    }

    private void DecrementTcAndCheck()
    {
        int tc = GetTransferCount();
        tc--;
        if (tc < 0) tc = 0;
        SetTransferCount(tc);

        if (tc == 0)
        {
            // PIO transfer complete
            _pioTransferActive = false;
            CompletePhaseTransfer();
        }
    }

    // --- Phase completion (after PIO, SBT, or DMA transfer) ---

    private void CompletePhaseTransfer()
    {
        switch (_phase)
        {
            case ScsiPhase.MsgOut:
                // MSG_OUT complete → next phase: COMMAND
                _phase = ScsiPhase.Command;
                _cdbOffset = 0;
                // CSR = 0x2A (MIS | COMMAND phase)
                // MIS (0x28) + CMD phase bits (010) = 0x2A
                SetCsrAndInterrupt(0x2A);
                break;

            case ScsiPhase.Command:
                // CDB complete → execute SCSI command
                ExecuteScsiCommand();
                break;

            case ScsiPhase.DataIn:
                if (_dataOffset < _dataLength)
                {
                    // TC exhausted but target still has data — stay in DataIn phase
                    SetCsrAndInterrupt(0x29);
                }
                else
                {
                    // All data transferred → STATUS phase
                    _phase = ScsiPhase.Status;
                    SetCsrAndInterrupt(0x2B);
                }
                break;

            case ScsiPhase.DataOut:
                if (_dataOffset < _dataLength)
                {
                    // TC exhausted but target expects more data — stay in DataOut phase
                    SetCsrAndInterrupt(0x28);
                }
                else
                {
                    // All data received → flush to disk, then STATUS phase
                    CompleteDataOut();
                    _phase = ScsiPhase.Status;
                    SetCsrAndInterrupt(0x2B);
                }
                break;

            case ScsiPhase.Status:
                // Status sent → MSG_IN phase
                _phase = ScsiPhase.MsgIn;
                _dataBuffer = new byte[] { 0x00 }; // COMMAND COMPLETE
                _dataOffset = 0;
                _dataLength = 1;
                // CSR = 0x2F (MIS | MSG_IN phase)
                SetCsrAndInterrupt(0x2F);
                break;

            case ScsiPhase.MsgIn:
                // Message sent → DISCONNECT
                _phase = ScsiPhase.Idle;
                _selectedTarget = -1;
                // CSR = 0x41 (DISC — disconnect)
                SetCsrAndInterrupt(0x41);
                break;
        }
    }

    // --- Execute SCSI command from accumulated CDB ---

    private void ExecuteScsiCommand()
    {
        _cdbLength = _cdbOffset;

        if (_scsiCmdLogCount < 200)
        {
            _scsiCmdLogCount++;
            DiagLog?.Invoke($"[SCSI] Exec CDB[{_cdbLength}]: {FormatCdb()} target={_selectedTarget} lun={_selectedLun}");
        }

        if (_selectedTarget < 0 || _targets[_selectedTarget] == null)
        {
            _statusByte = 0x02; // CHECK CONDITION
            _phase = ScsiPhase.Status;
            SetCsrAndInterrupt(0x2B);
            return;
        }

        var result = _targets[_selectedTarget]!.ProcessCommand(_cdb, _cdbLength, _selectedLun);
        _currentResult = result;
        _statusByte = result.StatusByte;

        if (result.HasDataIn)
        {
            // Data to send to host
            _dataBuffer = result.DataIn ?? Array.Empty<byte>();
            _dataOffset = 0;
            _dataLength = result.DataInLength;
            _phase = ScsiPhase.DataIn;

            // Log first bytes of data for READ commands
            if (_scsiCmdLogCount <= 200 && _cdb[0] is 0x08 or 0x28 or 0x25 or 0x12)
            {
                LogDataBuffer("DataIn", _dataBuffer, _dataLength);
            }

            // CSR = 0x29 (MIS | DATA_IN phase)
            SetCsrAndInterrupt(0x29);
        }
        else if (result.HasDataOut)
        {
            // Data to receive from host (WRITE commands)
            _dataBuffer = result.DataOut ?? new byte[result.DataOutLength];
            _dataOffset = 0;
            _dataLength = result.DataOutLength;
            // Save write parameters for deferred completion
            SaveWriteParams();
            _phase = ScsiPhase.DataOut;
            // CSR = 0x28 (MIS | DATA_OUT phase)
            SetCsrAndInterrupt(0x28);
        }
        else
        {
            // No data phase → STATUS
            _phase = ScsiPhase.Status;
            // CSR = 0x2B (MIS | STATUS phase)
            SetCsrAndInterrupt(0x2B);
        }
    }

    private void SaveWriteParams()
    {
        // Extract LBA and count from CDB for deferred disk write
        byte opcode = _cdb[0];
        if (opcode == 0x0A) // WRITE(6)
        {
            _writeLba = (uint)((_cdb[1] & 0x1F) << 16 | _cdb[2] << 8 | _cdb[3]);
            _writeSectorCount = _cdb[4];
            if (_writeSectorCount == 0) _writeSectorCount = 256;
        }
        else if (opcode == 0x2A) // WRITE(10)
        {
            _writeLba = (uint)(_cdb[2] << 24 | _cdb[3] << 16 | _cdb[4] << 8 | _cdb[5]);
            _writeSectorCount = _cdb[7] << 8 | _cdb[8];
        }
    }

    private void CompleteDataOut()
    {
        // Only flush to disk for actual WRITE commands — not MODE SELECT etc.
        byte opcode = _cdb[0];
        if (opcode is 0x0A or 0x2A) // WRITE(6) or WRITE(10)
        {
            if (_selectedTarget >= 0 && _targets[_selectedTarget] != null && _dataBuffer != null)
            {
                _targets[_selectedTarget]!.CompleteWrite(_writeLba, _dataBuffer, _dataLength);
            }
        }
    }

    // --- DMA transfers ---

    private void DoDmaDataIn()
    {
        if (_memory == null || _pcc == null)
        {
            DiagLog?.Invoke("[SCSI] DMA DataIn: no memory/pcc attached");
            SetCsrAndInterrupt(0x42);
            return;
        }

        uint dmaAddr = _pcc.GetDmaDataAddress();
        int tc = GetTransferCount();
        int transferLen = Math.Min(tc, _dataLength - _dataOffset);

        DiagLog?.Invoke($"[SCSI] DMA DataIn: addr=${dmaAddr:X8} tc={tc} dataLen={_dataLength} xferLen={transferLen}");

        for (int i = 0; i < transferLen; i++)
        {
            byte b = (_dataBuffer != null && _dataOffset < _dataLength) ? _dataBuffer[_dataOffset++] : (byte)0;
            _memory.PokeByte(dmaAddr++, b);
        }

        // Log first bytes written to memory for verification
        if (_scsiCmdLogCount <= 200 && transferLen > 0)
        {
            uint verifyAddr = dmaAddr - (uint)transferLen;
            var sb = new System.Text.StringBuilder();
            int show = Math.Min(32, transferLen);
            for (int i = 0; i < show; i++)
                sb.Append($"{_memory.PeekByte(verifyAddr + (uint)i):X2} ");
            DiagLog?.Invoke($"[SCSI] DMA verify @${verifyAddr:X8}: {sb}");
        }

        SetTransferCount(0);
        _pcc.SetDmaDataAddress(dmaAddr); // Update PCC DMA address for next transfer
        _pcc.SetDmaDone();

        if (_dataOffset < _dataLength)
        {
            // TC exhausted but SCSI target still has data — stay in DataIn phase.
            // Report "service required, DataIn phase" so the driver sets up another DMA.
            SetCsrAndInterrupt(0x29);
        }
        else
        {
            // All data transferred — move to STATUS phase
            _phase = ScsiPhase.Status;
            SetCsrAndInterrupt(0x2B);
        }
    }

    private void DoDmaDataOut()
    {
        if (_memory == null || _pcc == null)
        {
            DiagLog?.Invoke("[SCSI] DMA DataOut: no memory/pcc attached");
            SetCsrAndInterrupt(0x42);
            return;
        }

        uint dmaAddr = _pcc.GetDmaDataAddress();
        int tc = GetTransferCount();
        int transferLen = Math.Min(tc, _dataLength - _dataOffset);

        DiagLog?.Invoke($"[SCSI] DMA DataOut: addr=${dmaAddr:X8} tc={tc} len={transferLen}");

        for (int i = 0; i < transferLen; i++)
        {
            byte b = _memory.PeekByte(dmaAddr++);
            if (_dataBuffer != null && _dataOffset < _dataLength)
                _dataBuffer[_dataOffset++] = b;
        }

        SetTransferCount(0);
        _pcc.SetDmaDataAddress(dmaAddr); // Update PCC DMA address for next transfer
        _pcc.SetDmaDone();

        if (_dataOffset < _dataLength)
        {
            // TC exhausted but target expects more data — stay in DataOut phase.
            SetCsrAndInterrupt(0x28);
        }
        else
        {
            // All data received — flush to disk and move to STATUS
            CompleteDataOut();
            _phase = ScsiPhase.Status;
            SetCsrAndInterrupt(0x2B);
        }
    }

    // --- Diagnostics ---

    private string FormatCdb()
    {
        var parts = new string[_cdbLength];
        for (int i = 0; i < _cdbLength; i++)
            parts[i] = $"{_cdb[i]:X2}";
        return string.Join(" ", parts);
    }

    private void LogDataBuffer(string label, byte[] data, int length)
    {
        int show = Math.Min(32, length);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < show; i++)
            sb.Append($"{data[i]:X2} ");
        DiagLog?.Invoke($"[SCSI] {label}[{length}]: {sb}");
    }
}
