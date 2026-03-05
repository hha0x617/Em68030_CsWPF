namespace Em68030.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Em68030.Config;
using Em68030.Core;
using Em68030.IO;

public class MainViewModel : INotifyPropertyChanged
{
    private MC68030 _cpu = null!;
    private Memory _memory = null!;
    private Disassembler _disassembler = null!;
    private ConsoleDevice _consoleDevice = null!;
    private HddDevice _hddDevice = null!;
    private EmulatorConfig _config;
    private Thread? _emulationThread;
    private volatile bool _stopRequested;
    private Dispatcher? _dispatcher;
    private uint? _runToCursorAddress;
    private bool _isRunning;
    private string? _lstFilePath;
    private List<LstLine>? _lstLines;
    private bool _showLst;
    private uint _memoryDumpAddress;
    private uint _disasmAddress;
    private uint _programStartAddress;
    private uint _programEndAddress;
    private bool _disasmFollowPC = true;
    private bool _fullProgramDisassembled;
    private bool _manualDisasmMode;
    private string _loadedFileName = "";

    // Clock frequency estimation
    private long _mhzCyclesSnapshot;
    private long _mipsInsnSnapshot;
    private long _mhzTimestamp;       // Stopwatch ticks
    private double _estimatedMHz;
    private double _estimatedMips;

    // Average MHz/MIPS (cumulative since Run started)
    private long _runStartCycleCount;
    private long _runStartInsnCount;
    private long _runStartTimestamp;
    private double _avgMHz;
    private double _avgMips;
    private bool _showAvgMhz;

    // MVME147 devices
    private PccDevice? _pccDevice;
    private Z8530Device? _sccDevice;
    private Mk48t02Device? _rtcDevice;
    private Wd33c93Device? _scsiDevice;
    private List<ScsiDisk> _scsiDisks = new();
    private ScsiCdrom? _scsiCdrom;
    private int _scsiCdromId = -1; // Current SCSI ID of the CD-ROM (-1 = not attached)
    private LanceDevice? _lanceDevice;
    private uint _brdIdAddress; // Address of board ID packet in memory
    private bool _systemBooted; // True after first .BRD_ID call; used to detect warm reboot

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<char>? ConsoleCharOutput;
    public event Action<string>? ConsoleStringOutput;
    public Func<char>? ConsoleCharInput;
    public Func<string>? ConsoleStringInput;

    // Register display properties
    public uint D0 { get => _cpu.D[0]; set { _cpu.D[0] = value; OnPropertyChanged(); } }
    public uint D1 { get => _cpu.D[1]; set { _cpu.D[1] = value; OnPropertyChanged(); } }
    public uint D2 { get => _cpu.D[2]; set { _cpu.D[2] = value; OnPropertyChanged(); } }
    public uint D3 { get => _cpu.D[3]; set { _cpu.D[3] = value; OnPropertyChanged(); } }
    public uint D4 { get => _cpu.D[4]; set { _cpu.D[4] = value; OnPropertyChanged(); } }
    public uint D5 { get => _cpu.D[5]; set { _cpu.D[5] = value; OnPropertyChanged(); } }
    public uint D6 { get => _cpu.D[6]; set { _cpu.D[6] = value; OnPropertyChanged(); } }
    public uint D7 { get => _cpu.D[7]; set { _cpu.D[7] = value; OnPropertyChanged(); } }

    public uint A0 { get => _cpu.A[0]; set { _cpu.A[0] = value; OnPropertyChanged(); } }
    public uint A1 { get => _cpu.A[1]; set { _cpu.A[1] = value; OnPropertyChanged(); } }
    public uint A2 { get => _cpu.A[2]; set { _cpu.A[2] = value; OnPropertyChanged(); } }
    public uint A3 { get => _cpu.A[3]; set { _cpu.A[3] = value; OnPropertyChanged(); } }
    public uint A4 { get => _cpu.A[4]; set { _cpu.A[4] = value; OnPropertyChanged(); } }
    public uint A5 { get => _cpu.A[5]; set { _cpu.A[5] = value; OnPropertyChanged(); } }
    public uint A6 { get => _cpu.A[6]; set { _cpu.A[6] = value; OnPropertyChanged(); } }
    public uint A7 { get => _cpu.A[7]; set { _cpu.A[7] = value; OnPropertyChanged(); } }

    public uint PC { get => _cpu.PC; set { _cpu.PC = value; OnPropertyChanged(); UpdateDisassembly(); } }
    public ushort SR { get => _cpu.SR; set { _cpu.SR = value; OnPropertyChanged(); NotifyFlagChanges(); } }
    public uint SSP { get => _cpu.SSP; set { _cpu.SSP = value; OnPropertyChanged(); } }
    public uint VBR { get => _cpu.VBR; set { _cpu.VBR = value; OnPropertyChanged(); } }
    public uint CACR { get => _cpu.CACR; set { _cpu.CACR = value; OnPropertyChanged(); } }

    // FPU registers
    public double FP0 { get => _cpu.Fpu.FP[0]; set { _cpu.Fpu.FP[0] = value; OnPropertyChanged(); } }
    public double FP1 { get => _cpu.Fpu.FP[1]; set { _cpu.Fpu.FP[1] = value; OnPropertyChanged(); } }
    public double FP2 { get => _cpu.Fpu.FP[2]; set { _cpu.Fpu.FP[2] = value; OnPropertyChanged(); } }
    public double FP3 { get => _cpu.Fpu.FP[3]; set { _cpu.Fpu.FP[3] = value; OnPropertyChanged(); } }
    public double FP4 { get => _cpu.Fpu.FP[4]; set { _cpu.Fpu.FP[4] = value; OnPropertyChanged(); } }
    public double FP5 { get => _cpu.Fpu.FP[5]; set { _cpu.Fpu.FP[5] = value; OnPropertyChanged(); } }
    public double FP6 { get => _cpu.Fpu.FP[6]; set { _cpu.Fpu.FP[6] = value; OnPropertyChanged(); } }
    public double FP7 { get => _cpu.Fpu.FP[7]; set { _cpu.Fpu.FP[7] = value; OnPropertyChanged(); } }
    public uint FPCR { get => _cpu.Fpu.FPCR; set { _cpu.Fpu.FPCR = value; OnPropertyChanged(); } }
    public uint FPSR { get => _cpu.Fpu.FPSR; set { _cpu.Fpu.FPSR = value; OnPropertyChanged(); } }
    public uint FPIAR { get => _cpu.Fpu.FPIAR; set { _cpu.Fpu.FPIAR = value; OnPropertyChanged(); } }

    // Flag accessors
    public bool FlagX { get => _cpu.FlagX; set { _cpu.FlagX = value; OnPropertyChanged(); OnPropertyChanged(nameof(SR)); } }
    public bool FlagN { get => _cpu.FlagN; set { _cpu.FlagN = value; OnPropertyChanged(); OnPropertyChanged(nameof(SR)); } }
    public bool FlagZ { get => _cpu.FlagZ; set { _cpu.FlagZ = value; OnPropertyChanged(); OnPropertyChanged(nameof(SR)); } }
    public bool FlagV { get => _cpu.FlagV; set { _cpu.FlagV = value; OnPropertyChanged(); OnPropertyChanged(nameof(SR)); } }
    public bool FlagC { get => _cpu.FlagC; set { _cpu.FlagC = value; OnPropertyChanged(); OnPropertyChanged(nameof(SR)); } }
    public bool FlagS { get => _cpu.SupervisorMode; set { _cpu.SupervisorMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(SR)); } }
    public bool FlagT { get => _cpu.TraceT1; set { _cpu.TraceT1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(SR)); } }

    public int InterruptMask
    {
        get => _cpu.InterruptMask;
        set { _cpu.InterruptMask = value; OnPropertyChanged(); OnPropertyChanged(nameof(SR)); }
    }

    private bool _isMemoryEditMode;
    private bool _isRegisterEditMode;

    public bool IsRunning
    {
        get => _isRunning;
        set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText
    {
        get
        {
            if (_isRunning) return "Running: True";
            if (_cpu.Halted) return "HALTED";
            if (_cpu.Stopped) return "STOPPED";
            return "Running: False";
        }
    }

    public string NetworkModeText => "Net: " + _config.NetworkMode;

    public string JitStatusText => _config.JitEnabled ? "JIT: ON" : "JIT: OFF";

    public bool IsMemoryEditMode
    {
        get => _isMemoryEditMode;
        set { _isMemoryEditMode = value; OnPropertyChanged(); }
    }

    public bool IsRegisterEditMode
    {
        get => _isRegisterEditMode;
        set { _isRegisterEditMode = value; OnPropertyChanged(); }
    }

    public bool IsHalted => _cpu.Halted;
    public bool IsStopped => _cpu.Stopped;
    public bool IsTracing => _cpu.VerboseTrace;
    public string? StopReason => _cpu.StopReason;
    public long CycleCount => _cpu.CycleCount;
    public string EstimatedMHz
    {
        get
        {
            double mhz = _showAvgMhz ? _avgMHz : _estimatedMHz;
            double mips = _showAvgMhz ? _avgMips : _estimatedMips;
            return mhz > 0
                ? (_showAvgMhz ? $"Avg {mhz:F2} MHz ({mips:F2} MIPS)" : $"{mhz:F2} MHz ({mips:F2} MIPS)")
                : "";
        }
    }

    public void ToggleMhzDisplayMode()
    {
        _showAvgMhz = !_showAvgMhz;
        OnPropertyChanged(nameof(EstimatedMHz));
    }

    public bool ShowLst
    {
        get => _showLst;
        set { _showLst = value; OnPropertyChanged(); UpdateDisassembly(); }
    }

    public bool HasLstFile => _lstLines != null;

    public string LoadedFileName
    {
        get => _loadedFileName;
        set { _loadedFileName = value; OnPropertyChanged(); }
    }

    public uint MemoryDumpAddress
    {
        get => _memoryDumpAddress;
        set
        {
            _memoryDumpAddress = value;
            OnPropertyChanged();
            UpdateMemoryDump();
        }
    }

    public void NavigateMemoryDump(uint address)
    {
        _memoryDumpAddress = address;
        OnPropertyChanged(nameof(MemoryDumpAddress));
        UpdateMemoryDump();
    }

    public void NavigateDisassembly(uint address)
    {
        _disasmAddress = address;
        DisasmFollowPC = false;
        _fullProgramDisassembled = false;
        UpdateDisassemblyAt(_disasmAddress);
    }

    /// <summary>
    /// 指定アドレスが逆アセンブリペインの中央付近に来るようにスクロールする。
    /// 現在の表示範囲にアドレスが含まれていなければ再逆アセンブルする。
    /// </summary>
    public void ScrollToAddress(uint address)
    {
        // Check if the address is already visible in the current disassembly
        int targetIndex = -1;
        for (int i = 0; i < DisassemblyLines.Count; i++)
        {
            if (DisassemblyLines[i].HasAddress && DisassemblyLines[i].Address == address)
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex < 0)
        {
            // Address not in current view — navigate so the address appears
            DisasmFollowPC = false;
            _fullProgramDisassembled = false;
            // Back up ~20 instructions worth of bytes so target ends up near center
            uint backBytes = Math.Min(address, 80u);
            uint startAddr = (address - backBytes) & 0xFFFFFFFE;
            _disasmAddress = startAddr;
            UpdateDisassemblyAt(startAddr);

            // Find the target line after re-populating
            for (int i = 0; i < DisassemblyLines.Count; i++)
            {
                if (DisassemblyLines[i].HasAddress && DisassemblyLines[i].Address == address)
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex >= 0)
            ScrollToLineRequested?.Invoke(targetIndex);
    }

    public void NavigateToProgram()
    {
        _disasmAddress = _programStartAddress;
        DisasmFollowPC = false;
        _fullProgramDisassembled = false;
        UpdateDisassemblyRange(_programStartAddress, _programEndAddress);
    }

    public bool HasProgramLoaded => _programEndAddress > _programStartAddress;

    public bool DisasmFollowPC
    {
        get => _disasmFollowPC;
        set { if (_disasmFollowPC != value) { _disasmFollowPC = value; OnPropertyChanged(); } }
    }

    public void ResetDisasmFollowPC()
    {
        DisasmFollowPC = true;
        _fullProgramDisassembled = false;
        UpdateDisassembly();
    }

    public bool IsManualDisasmMode => _manualDisasmMode;

    public void ManualDisassembly(uint address, uint sizeBytes)
    {
        _manualDisasmMode = true;
        DisasmFollowPC = false;
        _fullProgramDisassembled = false;
        _disasmAddress = address;
        UpdateDisassemblyRange(address, address + sizeBytes);
    }

    public void ClearManualDisasmMode()
    {
        _manualDisasmMode = false;
    }

    public ObservableCollection<DisasmLineViewModel> DisassemblyLines { get; } = new();
    public ObservableCollection<MemoryDumpRow> MemoryDumpRows { get; } = new();
    public Dictionary<uint, BreakpointData> Breakpoints { get; } = new();
    public HashSet<uint> EnabledBreakpoints { get; } = new();

    public MC68030 Cpu => _cpu;
    public Memory Memory => _memory;
    public EmulatorConfig Config => _config;

    /// <summary>
    /// Send a character to the emulated system's console input.
    /// In MVME147 mode, feeds into SCC Channel A user input staging queue.
    /// Characters are promoted to the hardware RX FIFO one at a time during
    /// Tick(), simulating serial baud rate. This works for both polled I/O
    /// (cngetc during boot) and interrupt-driven I/O (tty after boot).
    /// </summary>
    public void SendConsoleChar(byte ch)
    {
        _sccDevice?.ChannelA.QueueInput(ch);
    }

    // Commands
    public RelayCommand StepCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand FullResetCommand { get; }
    public RelayCommand EditMemoryCommand { get; }
    public RelayCommand ApplyMemoryCommand { get; }
    public RelayCommand CancelMemoryCommand { get; }
    public RelayCommand EditRegistersCommand { get; }
    public RelayCommand ApplyRegistersCommand { get; }
    public RelayCommand CancelRegistersCommand { get; }
    public RelayCommand ToggleTraceCommand { get; }

    public MainViewModel()
    {
        _config = EmulatorConfig.Load();

        // Initialize _consoleDevice and _hddDevice with defaults (may be replaced by SetupGeneric)
        _consoleDevice = new ConsoleDevice(_config.ConsoleBaseAddress);
        _hddDevice = new HddDevice(_config.HddBaseAddress);

        if (_config.BoardType == "MVME147")
            SetupMvme147();
        else
            SetupGeneric();

        _disassembler = new Disassembler(_memory);
        SetupTrapHandler();

        _cpu.Reset();
        _disasmAddress = _cpu.PC;

        StepCommand = new RelayCommand(_ => Step(), _ => !IsRunning);
        RunCommand = new RelayCommand(_ => Run(), _ => !IsRunning);
        StopCommand = new RelayCommand(_ => Stop(), _ => IsRunning);
        ResetCommand = new RelayCommand(_ => DoReset(), _ => !IsRunning);
        FullResetCommand = new RelayCommand(_ => DoFullReset(), _ => !IsRunning);
        EditMemoryCommand = new RelayCommand(_ => EnterMemoryEditMode(), _ => !IsMemoryEditMode);
        ApplyMemoryCommand = new RelayCommand(_ => ApplyMemoryEdits(), _ => IsMemoryEditMode);
        CancelMemoryCommand = new RelayCommand(_ => CancelMemoryEdits(), _ => IsMemoryEditMode);
        EditRegistersCommand = new RelayCommand(_ => EnterRegisterEditMode(), _ => !IsRunning && !IsRegisterEditMode);
        ApplyRegistersCommand = new RelayCommand(_ => ApplyRegisterEdits(), _ => IsRegisterEditMode);
        CancelRegistersCommand = new RelayCommand(_ => CancelRegisterEdits(), _ => IsRegisterEditMode);
        ToggleTraceCommand = new RelayCommand(_ => ToggleTrace());

        UpdateDisassembly();
        UpdateMemoryDump();
    }

    private void SetupGeneric()
    {
        if (_config.MemoryRegions.Count > 0)
        {
            _memory = new Memory();
            foreach (var r in _config.MemoryRegions)
                _memory.AddRegion(r.BaseAddress, r.Size,
                    Enum.Parse<RegionType>(r.Type));
        }
        else
        {
            _memory = new Memory(_config.MemorySize);
        }
        _cpu = new MC68030(_memory);
        _cpu.JitEnabled = _config.JitEnabled;
        _cpu.JitMinBlockLength = _config.JitMinBlockLength;
        _cpu.JitCompileThreshold = _config.JitCompileThreshold;

        _consoleDevice = new ConsoleDevice(_config.ConsoleBaseAddress);
        _hddDevice = new HddDevice(_config.HddBaseAddress);
        _hddDevice.AttachMemory(_memory);

        if (_config.ConsoleEnabled)
            _memory.RegisterDevice(_config.ConsoleBaseAddress, 256, _consoleDevice);
        if (_config.HddEnabled)
            _memory.RegisterDevice(_config.HddBaseAddress, 256, _hddDevice);

        _consoleDevice.CharOutput += ch => { ConsoleCharOutput?.Invoke(ch); _traceWriter?.Write(ch); };
        _consoleDevice.StringOutput += s => { ConsoleStringOutput?.Invoke(s); _traceWriter?.Write(s); };
        _consoleDevice.CharInput = () => ConsoleCharInput?.Invoke() ?? '\0';
        _consoleDevice.StringInput = () => ConsoleStringInput?.Invoke() ?? "";

        if (!string.IsNullOrEmpty(_config.HddImagePath) && File.Exists(_config.HddImagePath))
            _hddDevice.MountImage(_config.HddImagePath);
    }

    private void SetupMvme147()
    {
        _memory = new Memory();
        _memory.AddRegion(0x00000000, _config.MemorySize, RegionType.Ram);
        if (!string.IsNullOrEmpty(_config.Mvme147RomPath))
            _memory.AddRegion(0xFF800000, 4 * 1024 * 1024, RegionType.Rom);

        _cpu = new MC68030(_memory);
        _cpu.JitEnabled = _config.JitEnabled;
        _cpu.JitMinBlockLength = _config.JitMinBlockLength;
        _cpu.JitCompileThreshold = _config.JitCompileThreshold;

        // Create MVME147 devices
        _pccDevice = new PccDevice(_cpu);
        _sccDevice = new Z8530Device();
        _rtcDevice = new Mk48t02Device();
        _rtcDevice.SetMvme147Config(
            onboardRamEnd: (uint)_config.MemorySize,
            ethernetAddr: new byte[] { 0x21, 0x00, 0x00 } // 08:00:3E:21:00:00
        );
        _scsiDevice = new Wd33c93Device();
        _scsiDevice.AttachMemory(_memory);
        _lanceDevice = new LanceDevice();
        _lanceDevice.AttachMemory(_memory);
        if (_config.NetworkMode == "NAT")
        {
            var gwIp = SlirpNetworkHandler.ParseIpAddress(_config.NatGatewayIp);
            var gwMac = SlirpNetworkHandler.ParseMacAddress(_config.NatGatewayMac);
            var natHandler = new SlirpNetworkHandler(gwIp, gwMac);
            natHandler.DiagnosticOutput = msg => _traceWriter?.Write(msg);
            _lanceDevice.SetNetworkHandler(natHandler);
        }

        // First, register catch-all for the entire I/O space ($FFFE0000-$FFFEFFFF)
        // This prevents bus errors when kernel probes unmapped device addresses
        var ioSpace = new Mvme147IoSpaceDevice();
        _memory.RegisterDevice(0xFFFE0000, 0x10000, ioSpace);

        // Then register specific devices (overrides catch-all for their ranges)
        _memory.RegisterDevice(0xFFFE1000, 48, _pccDevice);
        _memory.RegisterDevice(0xFFFE3000, 8, _sccDevice); // 8 bytes to detect word-spaced access
        _memory.RegisterDevice(0xFFFE0000, 2048, _rtcDevice);
        _memory.RegisterDevice(0xFFFE4000, 4, _scsiDevice);
        _memory.RegisterDevice(0xFFFE1800, 4, _lanceDevice);

        // Wire device interrupts through PCC
        _sccDevice.InterruptOutput = active => _pccDevice.SetDeviceInterrupt("scc", active);
        _scsiDevice.InterruptOutput = active => _pccDevice.SetDeviceInterrupt("scsi", active);
        _scsiDevice.DiagLog = msg => _traceWriter?.Write(msg + "\n");
        _scsiDevice.AttachPcc(_pccDevice);
        _lanceDevice.InterruptOutput = active => _pccDevice.SetDeviceInterrupt("lance", active);

        // Mount SCSI disk images if configured
        _scsiDisks.Clear();
        foreach (var diskConfig in _config.Mvme147ScsiDisks)
        {
            if (!string.IsNullOrEmpty(diskConfig.Path) && File.Exists(diskConfig.Path))
            {
                EnsureCpuDisklabel(diskConfig.Path);
                var disk = new ScsiDisk();
                disk.DiagLog = msg => _traceWriter?.Write(msg + "\n");
                disk.MountImage(diskConfig.Path);
                _scsiDevice.AttachDisk(diskConfig.ScsiId, disk);
                _scsiDisks.Add(disk);
            }
        }

        // Mount SCSI CD-ROM image if configured
        _scsiCdrom = new ScsiCdrom();
        if (!string.IsNullOrEmpty(_config.Mvme147ScsiCdromPath) &&
            File.Exists(_config.Mvme147ScsiCdromPath))
        {
            _scsiCdrom.MountImage(_config.Mvme147ScsiCdromPath);
        }
        _scsiCdromId = _config.Mvme147ScsiCdromId;
        _scsiDevice.AttachTarget(_scsiCdromId, _scsiCdrom);

        // SCC Channel A -> console output
        _sccDevice.ChannelA.CharTransmitted += ch => { ConsoleCharOutput?.Invoke((char)ch); _traceWriter?.Write((char)ch); };

        // SCC Channel B -> also wire to console output (kernel may use either channel)
        _sccDevice.ChannelB.CharTransmitted += ch => { ConsoleCharOutput?.Invoke((char)ch); _traceWriter?.Write((char)ch); };

        // Diagnostic output: trace file only (not shown in console window)
        _cpu.DiagnosticOutput = msg => _traceWriter?.Write(msg);

        // CPU tick handlers for PCC timer, SCC TX/RX, and LANCE TX
        _cpu.AddTickHandler(() =>
        {
            _pccDevice.Tick();
            _sccDevice.Tick(_cpu.Stopped);
            _lanceDevice.Tick();
        });

        // RESET instruction: reset all external devices (PCC timers, interrupt lines)
        _cpu.OnResetInstruction = () =>
        {
            _pccDevice.HardwareReset();
        };

        // Load ROM image if configured
        if (!string.IsNullOrEmpty(_config.Mvme147RomPath) &&
            File.Exists(_config.Mvme147RomPath))
        {
            byte[] rom = File.ReadAllBytes(_config.Mvme147RomPath);
            _memory.LoadData(0xFF800000, rom);
        }

        // Also create generic devices (unused but avoid null refs)
        _consoleDevice = new ConsoleDevice(_config.ConsoleBaseAddress);
        _hddDevice = new HddDevice(_config.HddBaseAddress);
    }

    private void SetupTrapHandler()
    {
        _cpu.TrapExecuted += trapNum =>
        {
            if (_config.BoardType == "Generic" && trapNum == 15)
            {
                _consoleDevice.HandleTrap(_cpu);
                _cpu.TrapHandled = true;
            }
            else if (_config.BoardType == "MVME147" && trapNum == 15)
            {
                Handle147BugCall();
                _cpu.TrapHandled = true;
            }
        };
    }

    /// <summary>
    /// Emulates 147Bug TRAP #15 system calls.
    /// The TRAP #15 instruction is followed by a 16-bit inline function code.
    /// PC currently points to the function code word (past the TRAP opcode).
    /// </summary>
    private void Handle147BugCall()
    {
        // Read inline function code from PC and advance past it
        // Must use CPU's ReadWord (MMU-translated) since PC is a virtual address when MMU is on
        ushort funcCode = _cpu.ReadWord(_cpu.PC);
        _cpu.PC += 2;

        _cpu.DiagnosticOutput?.Invoke($"\n[EMU] TRAP #15 function code: 0x{funcCode:X4} at PC=${_cpu.PC - 4:X8}\n");

        switch (funcCode)
        {
            case 0x0000: // .INCHR - Read character into D0
                if (ConsoleCharInput != null)
                {
                    char ch = ConsoleCharInput();
                    _cpu.D[0] = (uint)(_cpu.D[0] & 0xFFFFFF00) | (byte)ch;
                }
                break;

            case 0x0001: // .INSTAT - Check input status (Z=1 if no char available)
                // For simplicity, always report no char available (Z=1)
                // Real implementation would check SCC RX FIFO
                _cpu.FlagZ = true;
                break;

            case 0x0020: // .OUTCHR - Output character from D0.B
                ConsoleCharOutput?.Invoke((char)(_cpu.D[0] & 0xFF));
                break;

            case 0x0021: // .OUTSTR - Output NUL-terminated string at (A0), no CR/LF
            {
                uint addr = _cpu.A[0];
                for (int i = 0; i < 4096; i++) // safety limit
                {
                    byte b = _cpu.ReadByte(addr++);
                    if (b == 0) break;
                    ConsoleCharOutput?.Invoke((char)b);
                }
                break;
            }

            case 0x0022: // .OUTLN - Output string at (A0) + CR/LF
            {
                uint addr = _cpu.A[0];
                for (int i = 0; i < 4096; i++)
                {
                    byte b = _cpu.ReadByte(addr++);
                    if (b == 0) break;
                    ConsoleCharOutput?.Invoke((char)b);
                }
                ConsoleCharOutput?.Invoke('\r');
                ConsoleCharOutput?.Invoke('\n');
                break;
            }

            case 0x0026: // .PCRLF - Print CR/LF
                ConsoleCharOutput?.Invoke('\r');
                ConsoleCharOutput?.Invoke('\n');
                break;

            case 0x0053: // .RTC_RD - Read Real Time Clock
            {
                // Returns BCD time in registers:
                // D0.B = year (0-99), D1.B = month (1-12), D2.B = day (1-31)
                // D3.B = hour (0-23), D4.B = minute (0-59), D5.B = second (0-59)
                var now = DateTime.UtcNow;
                _cpu.D[0] = (_cpu.D[0] & 0xFFFFFF00) | ToBcd((now.Year - 1968) % 100); // YEAR0=1968
                _cpu.D[1] = (_cpu.D[1] & 0xFFFFFF00) | ToBcd(now.Month);
                _cpu.D[2] = (_cpu.D[2] & 0xFFFFFF00) | ToBcd(now.Day);
                _cpu.D[3] = (_cpu.D[3] & 0xFFFFFF00) | ToBcd(now.Hour);
                _cpu.D[4] = (_cpu.D[4] & 0xFFFFFF00) | ToBcd(now.Minute);
                _cpu.D[5] = (_cpu.D[5] & 0xFFFFFF00) | ToBcd(now.Second);
                break;
            }

            case 0x0060: // .RETURN (alias)
            case 0x0063: // .RETURN / .EXIT - Return to Bug monitor
                _cpu.Halted = true;
                _cpu.StopReason = "147Bug .RETURN";
                break;

            case 0x0070: // .BRD_ID - Return board identification
            {
                if (_systemBooted)
                {
                    // Warm reboot detected: the kernel restarted from the entry point.
                    // On real hardware, the Bug monitor would reload the kernel from disk.
                    // We reload the ELF to give the kernel fresh .text/.data/.bss sections.
                    _cpu.DiagnosticOutput?.Invoke("\n[EMU] Warm reboot detected — reloading kernel and resetting\n");
                    _pccDevice?.HardwareReset();
                    _systemBooted = false;

                    if (!string.IsNullOrEmpty(_config.LastOpenedFile) && System.IO.File.Exists(_config.LastOpenedFile))
                    {
                        var result = FileLoader.LoadElf(_memory, _config.LastOpenedFile);
                        _cpu.PC = result.EntryPoint;
                        _programStartAddress = result.StartAddress;
                        _programEndAddress = result.EndAddress;

                        if (_config.BoardType == "MVME147")
                        {
                            uint topOfRam = (uint)_config.MemorySize;
                            SetupMvme147BootStub(topOfRam);
                            _cpu.SR = 0x2700;
                        }
                    }
                    else
                    {
                        _cpu.Reset();
                    }
                    return;
                }
                _systemBooted = true;
                // Calling convention: caller pushes a longword on stack (clrl sp@-),
                // we fill it with a pointer to the board ID packet.
                // Use CPU write (MMU-translated) since SP is a virtual address when MMU is on.
                uint sp = _cpu.A[7];
                _cpu.WriteLong(sp, _brdIdAddress);
                break;
            }

            default:
                // Unknown function code - log and continue
                ConsoleStringOutput?.Invoke($"[147Bug] Unknown TRAP #15 function ${funcCode:X4} at PC=${_cpu.PC - 2:X8}\n");
                break;
        }
    }

    private static byte ToBcd(int val) => (byte)(((val / 10) << 4) | (val % 10));

    /// <summary>
    /// Writes an mvmeprom_brdid structure for MVME147 at the given address.
    /// </summary>
    private void WriteBoardIdPacket(uint addr)
    {
        // struct mvmeprom_brdid layout (big-endian):
        //  0: eye_catcher  (u_long)   = 0x01234567
        //  4: rev          (u_char)   = 0x01
        //  5: month        (u_char)   = current month
        //  6: day          (u_char)   = current day
        //  7: year         (u_char)   = current year % 100
        //  8: size         (u_short)  = 0x00E0 (packet size)
        // 10: rsv1         (u_short)  = 0
        // 12: model        (u_short)  = 0x0147 (MVME_147)
        // 14: suffix       (u_short)  = 0x0053 ('S')
        // 16: options      (u_short)  = 0x0002
        // 18: family       (u_char)   = 0x01 (68K)
        // 19: cpu          (u_char)   = 0x03 (68030)
        // 20: ctrlun       (u_short)  = 0
        // 22: devlun       (u_short)  = 0
        // 24: devtype      (u_short)  = 0
        // 26: devnum       (u_short)  = 0
        // 28: bug          (u_long)   = 0x01470000
        // 52: longname     (12 bytes) = "MVME147-010 "
        // 80: speed        (4 bytes)  = "2500" (25 MHz)
        var now = DateTime.Now;

        _memory.PokeLong(addr + 0, 0x01234567);    // eye_catcher
        _memory.PokeByte(addr + 4, 0x01);           // rev
        _memory.PokeByte(addr + 5, (byte)now.Month); // month
        _memory.PokeByte(addr + 6, (byte)now.Day);   // day
        _memory.PokeByte(addr + 7, (byte)(now.Year % 100)); // year
        _memory.PokeWord(addr + 8, 0x00E0);         // size
        _memory.PokeWord(addr + 10, 0x0000);         // rsv1
        _memory.PokeWord(addr + 12, 0x0147);         // model = MVME_147
        _memory.PokeWord(addr + 14, 0x0053);         // suffix = 'S'
        _memory.PokeWord(addr + 16, 0x0002);         // options
        _memory.PokeByte(addr + 18, 0x01);           // family (68K)
        _memory.PokeByte(addr + 19, 0x03);           // cpu (68030)
        _memory.PokeWord(addr + 20, 0x0000);         // ctrlun
        _memory.PokeWord(addr + 22, 0x0000);         // devlun
        _memory.PokeWord(addr + 24, 0x0000);         // devtype
        _memory.PokeWord(addr + 26, 0x0000);         // devnum
        _memory.PokeLong(addr + 28, 0x01470000);     // bug version

        // longname at offset 52: "MVME147-010 "
        string longname = "MVME147-010 ";
        for (int i = 0; i < 12; i++)
            _memory.PokeByte(addr + 52 + (uint)i,
                i < longname.Length ? (byte)longname[i] : (byte)0);

        // speed at offset 80: "2500" (25.00 MHz)
        string speed = "2500";
        for (int i = 0; i < 4; i++)
            _memory.PokeByte(addr + 80 + (uint)i, (byte)speed[i]);
    }

    public void LoadBinaryFile(string path, uint loadAddress)
    {
        uint size = FileLoader.LoadBinary(_memory, path, loadAddress);
        _cpu.PC = loadAddress;
        _programStartAddress = loadAddress;
        _programEndAddress = loadAddress + size;
        DisasmFollowPC = true;
        _fullProgramDisassembled = false;
        ClearManualDisasmMode();
        InitStackPointer();
        LoadedFileName = System.IO.Path.GetFileName(path);
        CheckForLstFile(path);
        _config.LastOpenedFile = path;
        _config.LastLoadAddress = loadAddress;
        _config.Save();
        RefreshAll();
    }

    public void LoadSRecordFile(string path)
    {
        var (start, end, entry, hasEntry) = FileLoader.LoadSRecord(_memory, path);
        if (hasEntry)
        {
            _cpu.PC = entry;
            _programStartAddress = entry;
        }
        else
        {
            _cpu.PC = start;
            _programStartAddress = start;
        }
        _programEndAddress = end;
        DisasmFollowPC = true;
        _fullProgramDisassembled = false;
        ClearManualDisasmMode();
        InitStackPointer();
        LoadedFileName = System.IO.Path.GetFileName(path);
        CheckForLstFile(path);
        _config.LastOpenedFile = path;
        _config.Save();
        RefreshAll();
    }

    public ElfLoadResult LoadElfFile(string path)
    {
        var result = FileLoader.LoadElf(_memory, path);
        _cpu.PC = result.EntryPoint;
        _programStartAddress = result.StartAddress;
        _programEndAddress = result.EndAddress;
        DisasmFollowPC = true;
        _fullProgramDisassembled = false;
        ClearManualDisasmMode();

        if (_config.BoardType == "MVME147")
        {
            uint topOfRam = (uint)_config.MemorySize;
            SetupMvme147BootStub(topOfRam);
            _cpu.SR = 0x2700; // Supervisor mode, IPL 7
        }
        else
        {
            InitStackPointer();
        }

        LoadedFileName = System.IO.Path.GetFileName(path);
        _config.LastOpenedFile = path;
        _config.Save();
        RefreshAll();
        return result;
    }

    /// <summary>
    /// Set up minimal boot stub for MVME147 kernel direct loading.
    /// Emulates what 147Bug firmware provides: exception vectors at PA=0,
    /// handler stubs in low memory (identity-mapped page), VBR=0,
    /// and boot parameters on the stack.
    ///
    /// Memory layout:
    ///   PA $00000000-$003FF   : Vector table (256 entries, like real 147Bug)
    ///   PA $00000400          : Exception handler stub (RTE)
    ///   topOfRam - $3000      : SSP (stack grows downward, used before MMU)
    ///   topOfRam - $2000      : Board ID packet (96 bytes)
    ///
    /// Boot parameters (locore.s reads from SP@(4)..SP@(24)):
    ///   SP@(0)  : return address (dummy)
    ///   SP@(4)  : boothowto   (boot flags, 0=normal)
    ///   SP@(8)  : bootaddr    (controller physical address, 0xFFFE3000 for WDSC)
    ///   SP@(12) : bootctrllun (controller LUN, 0)
    ///   SP@(16) : bootdevlun  (SCSI target ID of boot disk)
    ///   SP@(20) : bootpart    (partition number, 0='a')
    ///   SP@(24) : esyms       (end of symbol table, 0=none)
    /// </summary>
    private void SetupMvme147BootStub(uint topOfRam)
    {
        uint ssp = topOfRam - 0x3000;

        // --- Board ID packet in high RAM (read before MMU is enabled) ---
        _brdIdAddress = topOfRam - 0x2000;
        WriteBoardIdPacket(_brdIdAddress);

        // NOTE: We do NOT write a vector table or RTE handler to PA=$0-$401.
        // SetupMvme147BootStub() is called AFTER LoadElf() has loaded the kernel,
        // so writing to PA=$0-$401 would OVERWRITE kernel data/code.
        // The kernel sets its own VBR early in locore.s before any exceptions occur.

        // --- Boot parameters on stack (like real 147Bug bootloader) ---
        // The kernel entry point (locore.s:start) reads boot arguments from the
        // stack. Without these, bootaddr/bootdevlun are garbage, causing:
        //   - "boot device: <unknown>" (autoconf.c can't match WDSC controller)
        //   - Incorrect root device detection
        //
        // WDSC controller address: PCC_PADDR(PCC_WDSC_OFF) = 0xFFFE0000 + 0x3000
        const uint PCC_WDSC_ADDR = 0xFFFE3000;
        uint bootdevlun = (uint)(_config.Mvme147ScsiDisks.Count > 0 ? _config.Mvme147ScsiDisks[0].ScsiId : 0); // SCSI target ID
        uint bootArgs = ssp - 28; // 7 longwords below SSP
        _memory.PokeLong(bootArgs + 0,  0);              // return address (dummy)
        _memory.PokeLong(bootArgs + 4,  0);                // boothowto (0 = multi-user mode)
        _memory.PokeLong(bootArgs + 8,  PCC_WDSC_ADDR);  // bootaddr (WDSC)
        _memory.PokeLong(bootArgs + 12, 0);              // bootctrllun (0)
        _memory.PokeLong(bootArgs + 16, bootdevlun);     // bootdevlun (SCSI ID)
        _memory.PokeLong(bootArgs + 20, 0);              // bootpart (0 = 'a')
        _memory.PokeLong(bootArgs + 24, 0);              // esyms (0 = none)

        // Set CPU state - VBR=0 like real 147Bug
        _cpu.VBR = 0;
        _cpu.SSP = bootArgs;
        _cpu.A[7] = bootArgs;
    }

    /// <summary>
    /// Check if a disk image has a valid mvme68k cpu_disklabel (VID/CFG).
    /// If not, write one so that the kernel can properly manage partitions.
    /// </summary>
    private static void EnsureCpuDisklabel(string path)
    {
        bool needsLabel;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (fs.Length < 512) return;

            // Check for VID magic ("NBSD") at offset 0
            byte[] header = new byte[4];
            fs.Read(header, 0, 4);
            if (header[0] == (byte)'N' && header[1] == (byte)'B' &&
                header[2] == (byte)'S' && header[3] == (byte)'D')
                return;

            // Check magic1 at offset 0x3A
            fs.Seek(0x3A, SeekOrigin.Begin);
            byte[] magic = new byte[4];
            fs.Read(magic, 0, 4);
            uint m = (uint)(magic[0] << 24 | magic[1] << 16 | magic[2] << 8 | magic[3]);
            if (m == 0x82564557)
                return;

            needsLabel = true;
        }

        if (needsLabel)
            ScsiDisk.WriteNetBsdDisklabel(path);
    }

    private void InitStackPointer()
    {
        // Read SSP from reset vector at address $0000
        uint ssp = _memory.PeekLong(0);
        if (ssp != 0)
        {
            _cpu.SSP = ssp;
            _cpu.A[7] = ssp;
        }
    }

    private void CheckForLstFile(string filePath)
    {
        _lstFilePath = FileLoader.FindLstFile(filePath);
        if (_lstFilePath != null)
        {
            _lstLines = FileLoader.LoadLstFile(_lstFilePath);
        }
        else
        {
            _lstLines = null;
        }
        OnPropertyChanged(nameof(HasLstFile));
    }

    public void Step()
    {
        if (_cpu.Halted) return;
        // In MVME147 mode, allow stepping even when Stopped (to process tick/interrupts)
        if (_cpu.Stopped && !_cpu.HasExternalDevices) return;
        _cpu.ExecuteStep();
        RefreshAll();
    }

    public void Run()
    {
        if (_cpu.Halted) return;
        if (_cpu.Stopped && !_cpu.HasExternalDevices) return;
        _runToCursorAddress = null;
        StartEmulation();
    }

    public void RunToCursor(uint address)
    {
        if (_cpu.Halted) return;
        if (_cpu.Stopped && !_cpu.HasExternalDevices) return;
        _runToCursorAddress = address;
        StartEmulation();
    }

    private void StartEmulation()
    {
        if (_emulationThread != null) return; // Already running
        _dispatcher = Dispatcher.CurrentDispatcher;
        _stopRequested = false;
        IsRunning = true;
        _mhzCyclesSnapshot = _cpu.CycleCount;
        _mipsInsnSnapshot = _cpu.InstructionCount;
        _mhzTimestamp = Stopwatch.GetTimestamp();
        _estimatedMHz = 0;
        _estimatedMips = 0;
        _runStartCycleCount = _cpu.CycleCount;
        _runStartInsnCount = _cpu.InstructionCount;
        _runStartTimestamp = Stopwatch.GetTimestamp();
        _avgMHz = 0;
        _avgMips = 0;
        _emulationThread = new Thread(EmulationThreadLoop)
        {
            Name = "EmulationThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _emulationThread.Start();
    }

    private void EmulationThreadLoop()
    {
        bool hasBreakpoints = EnabledBreakpoints.Count > 0;
        bool hasRunToCursor = _runToCursorAddress.HasValue;
        uint runToCursorAddr = _runToCursorAddress.GetValueOrDefault();
        long lastMhzUpdate = Stopwatch.GetTimestamp();

        // Boolean branch instead of Func<bool> delegate — delegate invocation prevents
        // .NET JIT inlining of AggressiveInlining methods, causing ~17% speed regression.
        // A constant-direction branch is essentially free thanks to branch prediction.
        bool useJit = _cpu.JitEnabled;

        try
        {
            while (!_stopRequested)
            {
                if (_cpu.Halted) { RequestStopOnUI(); return; }
                if (_cpu.Stopped && !_cpu.HasExternalDevices) { RequestStopOnUI(); return; }

                if (!hasBreakpoints && !hasRunToCursor)
                {
                    // Fast path: no breakpoints or run-to-cursor — skip BP check entirely
                    uint loopDetectPC = uint.MaxValue;
                    int loopDetectCount = 0;
                    for (int i = 0; i < 100000; i++)
                    {
                        try
                        {
                            if (!(useJit ? _cpu.ExecuteNextFastJit() : _cpu.ExecuteNextFast()))
                            {
                                if (_cpu.Halted || (!_cpu.HasExternalDevices && _cpu.Stopped))
                                { RequestStopOnUI(); return; }
                                continue;
                            }
                        }
                        catch (BusErrorException ex)
                        {
                            _cpu.HandleBusError(ex);
                        }

                        if (_cpu.Halted) { RequestStopOnUI(); return; }

                        // Detect supervisor-mode infinite loop (e.g., kernel panic's for(;;);)
                        if (_cpu.PC == loopDetectPC)
                        {
                            if (++loopDetectCount >= 1000)
                            {
                                _cpu.Halted = true;
                                _cpu.StopReason = $"Infinite loop detected at ${_cpu.PC:X8}";
                                _cpu.DiagnosticOutput?.Invoke($"\n[EMU] Infinite loop detected at PC=${_cpu.PC:X8}, SR=${_cpu.SR:X4} — halting\n");
                                RequestStopOnUI(); return;
                            }
                        }
                        else
                        {
                            loopDetectPC = _cpu.PC;
                            loopDetectCount = 0;
                        }
                    }
                }
                else
                {
                    // Slow path: check breakpoints after each instruction
                    uint loopDetectPC = uint.MaxValue;
                    int loopDetectCount = 0;
                    for (int i = 0; i < 100000; i++)
                    {
                        try
                        {
                            if (!(useJit ? _cpu.ExecuteNextFastJit() : _cpu.ExecuteNextFast()))
                            {
                                if (_cpu.Halted || (!_cpu.HasExternalDevices && _cpu.Stopped))
                                { RequestStopOnUI(); return; }
                                // Stopped with devices — keep iterating so tick handlers
                                // fire and can generate interrupts to wake the CPU from STOP.
                                continue;
                            }
                        }
                        catch (BusErrorException ex)
                        {
                            _cpu.HandleBusError(ex);
                        }

                        if (_cpu.Halted) { RequestStopOnUI(); return; }
                        if (hasBreakpoints && EnabledBreakpoints.Contains(_cpu.PC))
                        {
                            _cpu.StopReason = $"Breakpoint at ${_cpu.PC:X8}";
                            RequestStopOnUI(); return;
                        }
                        if (hasRunToCursor && _cpu.PC == runToCursorAddr)
                        { RequestStopOnUI(); return; }

                        // Detect supervisor-mode infinite loop
                        if (_cpu.PC == loopDetectPC)
                        {
                            if (++loopDetectCount >= 1000)
                            {
                                _cpu.Halted = true;
                                _cpu.StopReason = $"Infinite loop detected at ${_cpu.PC:X8}";
                                _cpu.DiagnosticOutput?.Invoke($"\n[EMU] Infinite loop detected at PC=${_cpu.PC:X8}, SR=${_cpu.SR:X4} — halting\n");
                                RequestStopOnUI(); return;
                            }
                        }
                        else
                        {
                            loopDetectPC = _cpu.PC;
                            loopDetectCount = 0;
                        }
                    }
                }

                // Periodic MHz display update (~every 500ms)
                long now = Stopwatch.GetTimestamp();
                double seconds = (double)(now - lastMhzUpdate) / Stopwatch.Frequency;
                if (seconds >= 0.5)
                {
                    long cycles = _cpu.CycleCount - _mhzCyclesSnapshot;
                    _estimatedMHz = cycles / seconds / 1_000_000.0;
                    _mhzCyclesSnapshot = _cpu.CycleCount;

                    long insns = _cpu.InstructionCount - _mipsInsnSnapshot;
                    _estimatedMips = insns / seconds / 1_000_000.0;
                    _mipsInsnSnapshot = _cpu.InstructionCount;

                    lastMhzUpdate = now;
                    _mhzTimestamp = now;

                    // Cumulative average since Run started
                    double totalSec = (double)(now - _runStartTimestamp) / Stopwatch.Frequency;
                    if (totalSec > 0.01)
                    {
                        _avgMHz = (_cpu.CycleCount - _runStartCycleCount) / totalSec / 1_000_000.0;
                        _avgMips = (_cpu.InstructionCount - _runStartInsnCount) / totalSec / 1_000_000.0;
                    }

                    _dispatcher?.BeginInvoke(() =>
                    {
                        OnPropertyChanged(nameof(EstimatedMHz));
                        OnPropertyChanged(nameof(CycleCount));
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // Catch any unhandled exception (e.g., bus error during exception frame
            // construction, device handler errors, etc.) to prevent thread crash.
            _cpu.Halted = true;
            _cpu.StopReason = $"Unhandled exception: {ex.GetType().Name}: {ex.Message}";
            RequestStopOnUI();
            return;
        }

        // Thread ending due to stop request — UI update handled by Stop()
    }

    /// <summary>
    /// Called from the emulation thread when the CPU halts or hits a breakpoint.
    /// Dispatches the UI stop update to the UI thread.
    /// </summary>
    private void RequestStopOnUI()
    {
        _dispatcher?.BeginInvoke(() => FinalizeStop());
    }

    private void FinalizeStop()
    {
        _emulationThread = null;
        _runToCursorAddress = null;
        IsRunning = false;
        // Final MHz/MIPS calculation
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _mhzTimestamp;
        double seconds = (double)elapsed / Stopwatch.Frequency;
        if (seconds > 0.01)
        {
            long cycles = _cpu.CycleCount - _mhzCyclesSnapshot;
            _estimatedMHz = cycles / seconds / 1_000_000.0;
            long insns = _cpu.InstructionCount - _mipsInsnSnapshot;
            _estimatedMips = insns / seconds / 1_000_000.0;
        }
        // Final average calculation
        double totalSec = (double)(now - _runStartTimestamp) / Stopwatch.Frequency;
        if (totalSec > 0.01)
        {
            _avgMHz = (_cpu.CycleCount - _runStartCycleCount) / totalSec / 1_000_000.0;
            _avgMips = (_cpu.InstructionCount - _runStartInsnCount) / totalSec / 1_000_000.0;
        }
        RefreshAll();
    }

    private void StopEmulation()
    {
        _stopRequested = true;
        _emulationThread?.Join(2000); // Wait up to 2 seconds
        _emulationThread = null;
        _runToCursorAddress = null;
        IsRunning = false;
        // Final MHz/MIPS calculation
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _mhzTimestamp;
        double seconds = (double)elapsed / Stopwatch.Frequency;
        if (seconds > 0.01)
        {
            long cycles = _cpu.CycleCount - _mhzCyclesSnapshot;
            _estimatedMHz = cycles / seconds / 1_000_000.0;
            long insns = _cpu.InstructionCount - _mipsInsnSnapshot;
            _estimatedMips = insns / seconds / 1_000_000.0;
        }
        // Final average calculation
        double totalSec = (double)(now - _runStartTimestamp) / Stopwatch.Frequency;
        if (totalSec > 0.01)
        {
            _avgMHz = (_cpu.CycleCount - _runStartCycleCount) / totalSec / 1_000_000.0;
            _avgMips = (_cpu.InstructionCount - _runStartInsnCount) / totalSec / 1_000_000.0;
        }
        RefreshAll();
    }

    public void Stop()
    {
        StopEmulation();
    }

    public void DoReset()
    {
        _systemBooted = false;
        _cpu.Reset();
        RefreshAll();
    }

    public void DoFullReset()
    {
        if (IsRunning)
            Stop();

        _systemBooted = false;

        // Unmount SCSI disks and CD-ROM
        foreach (var disk in _scsiDisks)
            disk.UnmountImage();
        _scsiDisks.Clear();
        _scsiCdrom?.UnmountImage();

        // Re-initialize console and HDD devices with defaults
        _consoleDevice = new ConsoleDevice(_config.ConsoleBaseAddress);
        _hddDevice = new HddDevice(_config.HddBaseAddress);

        // Re-run full hardware setup
        if (_config.BoardType == "MVME147")
            SetupMvme147();
        else
            SetupGeneric();

        _disassembler = new Disassembler(_memory);
        SetupTrapHandler();

        _cpu.Reset();
        _disasmAddress = _cpu.PC;
        _fullProgramDisassembled = false;
        _programStartAddress = 0;
        _programEndAddress = 0;

        RefreshAll();
    }

    private StreamWriter? _traceWriter;

    public void ToggleTrace()
    {
        _cpu.VerboseTrace = !_cpu.VerboseTrace;
        _cpu.FpuTraceEnabled = _cpu.VerboseTrace;
        if (_cpu.VerboseTrace)
        {
            // Open trace log file in the application directory
            string traceDir = AppDomain.CurrentDomain.BaseDirectory;
            string tracePath = Path.Combine(traceDir, "tracelog.txt");
            try
            {
                _traceWriter = new StreamWriter(tracePath, append: false) { AutoFlush = true };
                ConsoleStringOutput?.Invoke($"\n[EMU] Verbose trace ON → {tracePath}\n");
            }
            catch
            {
                ConsoleStringOutput?.Invoke("\n[EMU] Verbose trace ON (file open failed, console only)\n");
            }
        }
        else
        {
            _traceWriter?.Close();
            _traceWriter = null;
            ConsoleStringOutput?.Invoke("\n[EMU] Verbose trace OFF\n");
        }
        OnPropertyChanged(nameof(IsTracing));
    }

    public void SetPCToCursor(uint address)
    {
        _cpu.PC = address;
        RefreshAll();
    }

    public void ToggleBreakpoint(uint address)
    {
        if (!Breakpoints.Remove(address))
            Breakpoints[address] = new BreakpointData { Address = address, Enabled = true };
        RebuildEnabledSet();
        UpdateDisassembly();
    }

    public void EnableBreakpoint(uint address, bool enabled)
    {
        if (Breakpoints.TryGetValue(address, out var bp))
        {
            bp.Enabled = enabled;
            RebuildEnabledSet();
            UpdateDisassembly();
        }
    }

    public void RemoveBreakpoint(uint address)
    {
        Breakpoints.Remove(address);
        RebuildEnabledSet();
        UpdateDisassembly();
    }

    public void ClearAllBreakpoints()
    {
        Breakpoints.Clear();
        EnabledBreakpoints.Clear();
        UpdateDisassembly();
    }

    private void RebuildEnabledSet()
    {
        EnabledBreakpoints.Clear();
        foreach (var (addr, bp) in Breakpoints)
        {
            if (bp.Enabled)
                EnabledBreakpoints.Add(addr);
        }
    }

    public void ApplyConfig(EmulatorConfig newConfig)
    {
        // Unregister old devices
        _memory.UnregisterDevice(_config.ConsoleBaseAddress, 256);
        _memory.UnregisterDevice(_config.HddBaseAddress, 256);

        _config = newConfig;

        // Re-register devices
        _consoleDevice.BaseAddress = _config.ConsoleBaseAddress;
        _hddDevice.BaseAddress = _config.HddBaseAddress;

        if (_config.ConsoleEnabled)
            _memory.RegisterDevice(_config.ConsoleBaseAddress, 256, _consoleDevice);
        if (_config.HddEnabled)
            _memory.RegisterDevice(_config.HddBaseAddress, 256, _hddDevice);

        // Mount/unmount HDD
        if (!string.IsNullOrEmpty(_config.HddImagePath) && File.Exists(_config.HddImagePath))
            _hddDevice.MountImage(_config.HddImagePath);
        else
            _hddDevice.UnmountImage();

        // Hot-swap SCSI disks (MVME147 mode only)
        if (_scsiDevice != null)
        {
            // Detach all old SCSI disk IDs from the bus before destroying disk objects
            for (int id = 0; id < 7; id++)
            {
                if (id != _scsiCdromId)
                    _scsiDevice.DetachTarget(id);
            }

            // Close file handles and destroy old ScsiDisk objects
            foreach (var disk in _scsiDisks)
                disk.UnmountImage();
            _scsiDisks.Clear();

            // Mount and attach new SCSI disks from updated config
            foreach (var diskConfig in _config.Mvme147ScsiDisks)
            {
                if (!string.IsNullOrEmpty(diskConfig.Path) && File.Exists(diskConfig.Path))
                {
                    var disk = new ScsiDisk();
                    disk.DiagLog = msg => _traceWriter?.Write(msg + "\n");
                    disk.MountImage(diskConfig.Path);
                    _scsiDevice.AttachDisk(diskConfig.ScsiId, disk);
                    _scsiDisks.Add(disk);
                }
            }

            // Hot-swap SCSI CD-ROM
            if (_scsiCdrom != null)
            {
                if (_scsiCdromId != _config.Mvme147ScsiCdromId)
                {
                    _scsiDevice.DetachTarget(_scsiCdromId);
                    _scsiCdromId = _config.Mvme147ScsiCdromId;
                    _scsiDevice.AttachTarget(_scsiCdromId, _scsiCdrom);
                }

                if (!string.IsNullOrEmpty(_config.Mvme147ScsiCdromPath) &&
                    File.Exists(_config.Mvme147ScsiCdromPath))
                    _scsiCdrom.MountImage(_config.Mvme147ScsiCdromPath);
                else
                    _scsiCdrom.UnmountImage();
            }
        }

        // Apply JIT setting
        _cpu.JitEnabled = _config.JitEnabled;
        _cpu.JitMinBlockLength = _config.JitMinBlockLength;
        _cpu.JitCompileThreshold = _config.JitCompileThreshold;
        if (!_config.JitEnabled)
            _cpu.JitCache.InvalidateAll();

        _config.Save();
        OnPropertyChanged(nameof(Config));
        OnPropertyChanged(nameof(NetworkModeText));
        OnPropertyChanged(nameof(JitStatusText));
    }

    public void UpdateDisassembly()
    {
        if (_manualDisasmMode)
        {
            UpdatePCHighlight();
            return;
        }

        if (_disasmFollowPC)
        {
            if (_fullProgramDisassembled)
            {
                // Full program already disassembled — just update PC highlight and scroll
                UpdatePCHighlight();
                return;
            }

            if (HasProgramLoaded)
            {
                // First call after loading — disassemble full program range
                UpdateDisassemblyRange(_programStartAddress, _programEndAddress);
                _fullProgramDisassembled = true;
                return;
            }

            // No program loaded (e.g. kernel) — show 40 lines around PC
            uint startAddr = _cpu.PC;
            if (startAddr > _programStartAddress)
            {
                uint backBytes = Math.Min(startAddr - _programStartAddress, 30);
                startAddr -= backBytes;
                startAddr &= 0xFFFFFFFE;
            }
            UpdateDisassemblyAt(startAddr);
        }
        else
        {
            // Manual navigation — keep current view but update PC highlight
            UpdatePCHighlight();
        }
    }

    public event Action<int>? ScrollToLineRequested;

    private void UpdatePCHighlight()
    {
        int pcIndex = -1;
        for (int i = 0; i < DisassemblyLines.Count; i++)
        {
            var line = DisassemblyLines[i];
            bool isCurrent = line.HasAddress && line.Address == _cpu.PC;
            line.IsCurrentPC = isCurrent;
            if (isCurrent) pcIndex = i;

            // Update breakpoint markers
            if (line.HasAddress)
            {
                bool hasBp = Breakpoints.ContainsKey(line.Address);
                bool isDisabled = hasBp && !Breakpoints[line.Address].Enabled;
                line.HasBreakpoint = hasBp;
                line.HasDisabledBreakpoint = isDisabled;
            }
        }
        if (pcIndex >= 0 && _disasmFollowPC)
            ScrollToLineRequested?.Invoke(pcIndex);
    }

    private void UpdateDisassemblyAt(uint startAddress)
    {
        DisassemblyLines.Clear();

        if (_showLst && _lstLines != null)
        {
            // Show LST file content around current PC
            int matchIdx = -1;
            for (int i = 0; i < _lstLines.Count; i++)
            {
                if (_lstLines[i].HasAddress && _lstLines[i].Address == _cpu.PC)
                {
                    matchIdx = i;
                    break;
                }
            }

            int startIdx = Math.Max(0, matchIdx - 5);
            int endIdx = Math.Min(_lstLines.Count, startIdx + 40);

            for (int i = startIdx; i < endIdx; i++)
            {
                var lstLine = _lstLines[i];
                bool hasBp = lstLine.HasAddress && Breakpoints.ContainsKey(lstLine.Address);
                bool isDisabled = hasBp && !Breakpoints[lstLine.Address].Enabled;
                DisassemblyLines.Add(new DisasmLineViewModel
                {
                    Address = lstLine.Address,
                    HasAddress = lstLine.HasAddress,
                    Text = lstLine.RawText,
                    IsCurrentPC = lstLine.HasAddress && lstLine.Address == _cpu.PC,
                    HasBreakpoint = hasBp,
                    HasDisabledBreakpoint = isDisabled
                });
            }
        }
        else
        {
            // Show disassembly starting from startAddress
            var lines = _disassembler.Disassemble(startAddress, 40);
            foreach (var line in lines)
            {
                bool hasBp = Breakpoints.ContainsKey(line.Address);
                bool isDisabled = hasBp && !Breakpoints[line.Address].Enabled;
                DisassemblyLines.Add(new DisasmLineViewModel
                {
                    Address = line.Address,
                    HasAddress = true,
                    Text = line.ToString(),
                    IsCurrentPC = line.Address == _cpu.PC,
                    HasBreakpoint = hasBp,
                    HasDisabledBreakpoint = isDisabled,
                    RawBytes = line.RawBytes,
                    Mnemonic = line.Mnemonic,
                    Operands = line.Operands,
                    Length = line.Length
                });
            }
        }
    }

    private void UpdateDisassemblyRange(uint startAddress, uint endAddress)
    {
        DisassemblyLines.Clear();

        var lines = _disassembler.DisassembleRange(startAddress, endAddress);
        foreach (var line in lines)
        {
            bool hasBp = Breakpoints.ContainsKey(line.Address);
            bool isDisabled = hasBp && !Breakpoints[line.Address].Enabled;
            DisassemblyLines.Add(new DisasmLineViewModel
            {
                Address = line.Address,
                HasAddress = true,
                Text = line.ToString(),
                IsCurrentPC = line.Address == _cpu.PC,
                HasBreakpoint = hasBp,
                HasDisabledBreakpoint = isDisabled,
                RawBytes = line.RawBytes,
                Mnemonic = line.Mnemonic,
                Operands = line.Operands,
                Length = line.Length
            });
        }
    }

    public void UpdateMemoryDump()
    {
        uint addr = _memoryDumpAddress & 0xFFFFFFF0; // Align to 16

        if (MemoryDumpRows.Count != 16)
        {
            MemoryDumpRows.Clear();
            for (int row = 0; row < 16; row++)
            {
                uint lineAddr = addr + (uint)(row * 16);
                var dumpRow = new MemoryDumpRow(lineAddr, _memory, row);
                MemoryDumpRows.Add(dumpRow);
            }
        }
        else
        {
            for (int row = 0; row < 16; row++)
            {
                uint lineAddr = addr + (uint)(row * 16);
                MemoryDumpRows[row].Update(lineAddr, _memory, row);
            }
        }
    }

    public void EnterMemoryEditMode()
    {
        IsMemoryEditMode = true;
    }

    public void ApplyMemoryEdits()
    {
        foreach (var row in MemoryDumpRows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.IsModified)
                {
                    byte? val = cell.GetEditedValue();
                    if (val.HasValue)
                        _memory.PokeByte(cell.Address, val.Value);
                }
            }
        }
        IsMemoryEditMode = false;
        UpdateMemoryDump();
    }

    public void CancelMemoryEdits()
    {
        IsMemoryEditMode = false;
        UpdateMemoryDump();
    }

    // --- Register Edit Mode ---

    // Snapshot of register values for Cancel support
    private uint[] _savedD = new uint[8];
    private uint[] _savedAReg = new uint[8];
    private uint _savedPC, _savedSSP, _savedVBR, _savedCACR;
    private ushort _savedSR;
    private double[] _savedFP = new double[8];
    private uint _savedFPCR, _savedFPSR, _savedFPIAR;

    public void EnterRegisterEditMode()
    {
        if (IsRunning) return;
        // Save snapshot for Cancel
        for (int i = 0; i < 8; i++) _savedD[i] = _cpu.D[i];
        for (int i = 0; i < 8; i++) _savedAReg[i] = _cpu.A[i];
        _savedPC = _cpu.PC;
        _savedSR = _cpu.SR;
        _savedSSP = _cpu.SSP;
        _savedVBR = _cpu.VBR;
        _savedCACR = _cpu.CACR;
        for (int i = 0; i < 8; i++) _savedFP[i] = _cpu.Fpu.FP[i];
        _savedFPCR = _cpu.Fpu.FPCR;
        _savedFPSR = _cpu.Fpu.FPSR;
        _savedFPIAR = _cpu.Fpu.FPIAR;
        IsRegisterEditMode = true;
    }

    public void ApplyRegisterEdits()
    {
        IsRegisterEditMode = false;
        // Values are already written to CPU via two-way bindings.
        // Update disassembly in case PC changed.
        RefreshAll();
    }

    public void CancelRegisterEdits()
    {
        // Restore from snapshot
        for (int i = 0; i < 8; i++) _cpu.D[i] = _savedD[i];
        for (int i = 0; i < 8; i++) _cpu.A[i] = _savedAReg[i];
        _cpu.PC = _savedPC;
        _cpu.SR = _savedSR;
        _cpu.SSP = _savedSSP;
        _cpu.VBR = _savedVBR;
        _cpu.CACR = _savedCACR;
        for (int i = 0; i < 8; i++) _cpu.Fpu.FP[i] = _savedFP[i];
        _cpu.Fpu.FPCR = _savedFPCR;
        _cpu.Fpu.FPSR = _savedFPSR;
        _cpu.Fpu.FPIAR = _savedFPIAR;
        IsRegisterEditMode = false;
        NotifyAllRegisters();
    }

    public void RefreshAll()
    {
        _disasmAddress = _cpu.PC;
        UpdateDisassembly();
        UpdateMemoryDump();
        NotifyAllRegisters();
        OnPropertyChanged(nameof(IsHalted));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(StopReason));
        OnPropertyChanged(nameof(CycleCount));
        OnPropertyChanged(nameof(EstimatedMHz));
    }

    private void NotifyAllRegisters()
    {
        OnPropertyChanged(nameof(D0)); OnPropertyChanged(nameof(D1));
        OnPropertyChanged(nameof(D2)); OnPropertyChanged(nameof(D3));
        OnPropertyChanged(nameof(D4)); OnPropertyChanged(nameof(D5));
        OnPropertyChanged(nameof(D6)); OnPropertyChanged(nameof(D7));
        OnPropertyChanged(nameof(A0)); OnPropertyChanged(nameof(A1));
        OnPropertyChanged(nameof(A2)); OnPropertyChanged(nameof(A3));
        OnPropertyChanged(nameof(A4)); OnPropertyChanged(nameof(A5));
        OnPropertyChanged(nameof(A6)); OnPropertyChanged(nameof(A7));
        OnPropertyChanged(nameof(PC)); OnPropertyChanged(nameof(SR));
        OnPropertyChanged(nameof(SSP)); OnPropertyChanged(nameof(VBR));
        OnPropertyChanged(nameof(CACR));
        OnPropertyChanged(nameof(FP0)); OnPropertyChanged(nameof(FP1));
        OnPropertyChanged(nameof(FP2)); OnPropertyChanged(nameof(FP3));
        OnPropertyChanged(nameof(FP4)); OnPropertyChanged(nameof(FP5));
        OnPropertyChanged(nameof(FP6)); OnPropertyChanged(nameof(FP7));
        OnPropertyChanged(nameof(FPCR)); OnPropertyChanged(nameof(FPSR));
        OnPropertyChanged(nameof(FPIAR));
        NotifyFlagChanges();
    }

    private void NotifyFlagChanges()
    {
        OnPropertyChanged(nameof(FlagX)); OnPropertyChanged(nameof(FlagN));
        OnPropertyChanged(nameof(FlagZ)); OnPropertyChanged(nameof(FlagV));
        OnPropertyChanged(nameof(FlagC)); OnPropertyChanged(nameof(FlagS));
        OnPropertyChanged(nameof(FlagT)); OnPropertyChanged(nameof(InterruptMask));
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class BreakpointData
{
    public uint Address { get; set; }
    public bool Enabled { get; set; } = true;
    // Future extension: string? Condition; uint HitCount; uint HitTarget;
}

public class DisasmLineViewModel : INotifyPropertyChanged
{
    public uint Address { get; set; }
    public bool HasAddress { get; set; }
    public string Text { get; set; } = "";

    private bool _isCurrentPC;
    public bool IsCurrentPC
    {
        get => _isCurrentPC;
        set { if (_isCurrentPC != value) { _isCurrentPC = value; OnPropertyChanged(nameof(IsCurrentPC)); } }
    }

    private bool _hasBreakpoint;
    public bool HasBreakpoint
    {
        get => _hasBreakpoint;
        set { if (_hasBreakpoint != value) { _hasBreakpoint = value; OnPropertyChanged(nameof(HasBreakpoint)); } }
    }

    private bool _hasDisabledBreakpoint;
    public bool HasDisabledBreakpoint
    {
        get => _hasDisabledBreakpoint;
        set { if (_hasDisabledBreakpoint != value) { _hasDisabledBreakpoint = value; OnPropertyChanged(nameof(HasDisabledBreakpoint)); } }
    }
    public string RawBytes { get; set; } = "";
    public string Mnemonic { get; set; } = "";
    public string Operands { get; set; } = "";
    public int Length { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class InverseBoolConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }
}

public class MemoryByteCell : INotifyPropertyChanged
{
    private uint _address;
    private byte _originalValue;
    private string _editText;
    private bool _isSelected;

    public uint Address => _address;

    public byte OriginalValue => _originalValue;

    public string EditText
    {
        get => _editText;
        set
        {
            if (_editText == value) return;
            _editText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModified));
        }
    }

    public bool IsModified
    {
        get
        {
            if (byte.TryParse(_editText, NumberStyles.HexNumber, null, out byte b))
                return b != _originalValue;
            return false;
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public int Row { get; set; }
    public int Column { get; set; }

    public MemoryByteCell(uint address, byte value, int row, int col)
    {
        _address = address;
        _originalValue = value;
        _editText = value.ToString("X2");
        Row = row;
        Column = col;
    }

    public void Reload(uint address, byte value)
    {
        _address = address;
        _originalValue = value;
        _editText = value.ToString("X2");
        _isSelected = false;
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(OriginalValue));
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(IsSelected));
    }

    public byte? GetEditedValue()
    {
        if (byte.TryParse(_editText, NumberStyles.HexNumber, null, out byte b))
            return b;
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class MemoryDumpRow : INotifyPropertyChanged
{
    private uint _address;

    public string AddressText => _address.ToString("X8");
    public uint Address => _address;
    public ObservableCollection<MemoryByteCell> Cells { get; } = new();

    public string AsciiText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            foreach (var cell in Cells)
            {
                byte b = cell.GetEditedValue() ?? cell.OriginalValue;
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            return sb.ToString();
        }
    }

    public MemoryDumpRow(uint address, Memory memory, int rowIndex)
    {
        _address = address;
        for (int i = 0; i < 16; i++)
        {
            byte b = memory.PeekByte(address + (uint)i);
            Cells.Add(new MemoryByteCell(address + (uint)i, b, rowIndex, i));
        }
    }

    public void Update(uint address, Memory memory, int rowIndex)
    {
        _address = address;
        OnPropertyChanged(nameof(AddressText));
        for (int i = 0; i < 16; i++)
        {
            byte b = memory.PeekByte(address + (uint)i);
            if (i < Cells.Count)
                Cells[i].Reload(address + (uint)i, b);
        }
        OnPropertyChanged(nameof(AsciiText));
    }

    public void RefreshAscii()
    {
        OnPropertyChanged(nameof(AsciiText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
