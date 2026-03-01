# Em68030 - MC68030 Emulator (C# / WPF)

An emulator for the [Motorola MC68030](https://en.wikipedia.org/wiki/Motorola_68030) microprocessor, a 32-bit CPU from the late 1980s used in workstations, embedded systems, and single-board computers. This emulator targets the [MVME147](https://en.wikipedia.org/wiki/MVME147), a VMEbus single-board computer built around the MC68030, and can boot [NetBSD/mvme68k](https://www.netbsd.org/ports/mvme68k/), the mvme68k port of the NetBSD operating system.

Developed through vibe coding with [Claude Code](https://docs.anthropic.com/en/docs/claude-code).

[Japanese documentation (README_ja.md)](README_ja.md)

![NetBSD booting on Em68030](screenshot.png)

## Features

### CPU Emulation
- Full MC68030 instruction set (including privileged instructions)
- MMU (page table walk, ATC, transparent translation, PTEST)
- MC68882-compatible FPU (FP0-FP7, FPCR/FPSR/FPIAR)
- Bus error recovery (Format A stack frame)

### Board Emulation (MVME147)
| Device | Emulation |
|---|---|
| WD33C93 SCSI Controller | Hard disk and CD-ROM (multiple units) |
| AM7990 LANCE Ethernet | Virtual network (ARP/ICMP/TCP/UDP) |
| Z8530 SCC Serial | VT100 terminal emulation |
| Mk48t02 RTC | Real-time clock |
| PCC | Interrupt controller |

### Debugger UI
- Disassembly view (auto-follow PC, address jump)
- Register display and editing (D0-D7, A0-A7, PC, SR, SSP, VBR, FP0-FP7)
- Memory dump and editing
- Breakpoints
- Console window (VT100 terminal with scrollback)
- ELF / S-Record / binary file loading

### Performance
Achieves emulation speed equivalent to 34-36 MHz on an i7-13700. Key optimizations:

- 65,536-entry opcode delegate table
- Specialized fast handlers for frequent instructions (MOVEQ, MOVE.L, Bcc.B, RTS, etc.)
- Inline fast path for direct ATC lookup
- Data page cache (1-entry read cache)

As an interpreter, each instruction consumes a large number of host CPU cycles. The C# JIT compiler has limitations on method inlining, resulting in approximately 25% lower speed compared to the C++ native version.

## Requirements

- Windows 10 or later
- .NET 8.0 SDK

## Build

```bash
dotnet build Em68030/Em68030.csproj -c Release
```

## Test

```bash
dotnet test Em68030.Tests/Em68030.Tests.csproj -c Release
```

## Run

```bash
dotnet run --project Em68030/Em68030.csproj -c Release
```

On first launch, an `appsettings.json` file is generated from the Settings menu.

## Configuration (appsettings.json)

```json
{
    "BoardType": "MVME147",
    "MemorySize": 67108864,
    "Mvme147ScsiDisks": [
        { "Path": "path/to/scsi0.img", "ScsiId": 0 }
    ],
    "Mvme147ScsiCdromPath": "path/to/NetBSD-10.1-mvme68k.iso",
    "Mvme147ScsiCdromId": 3,
    "ConsoleScrollbackLines": 2000
}
```

| Setting | Description | Default |
|---|---|---|
| `BoardType` | `"Generic"` or `"MVME147"` | `"Generic"` |
| `MemorySize` | RAM size in bytes | 48 MB |
| `Mvme147ScsiDisks` | List of SCSI disk images (Path + ScsiId) | `[]` |
| `Mvme147ScsiCdromPath` | SCSI CD-ROM ISO image path | `""` |
| `ConsoleScrollbackLines` | Console scrollback lines (0-100000) | 2000 |

## Booting NetBSD

1. Prepare a NetBSD/mvme68k disk image
2. In Settings, set `BoardType` to `MVME147` and specify the SCSI disk image path
3. Load a NetBSD kernel (`netbsd-GENERIC`) via File > Open ELF
4. Start execution with Run (F5)

## Project Structure

```
Em68030_CsWpf/
├── Em68030_CsWpf.sln
├── Em68030/
│   ├── Core/           MC68030, MMU, Memory, InstructionDecoder, ALU, FPU
│   ├── IO/             SCSI, Ethernet, Serial, RTC, PCC devices
│   ├── Config/         EmulatorConfig (appsettings.json)
│   ├── ViewModels/     MainViewModel
│   ├── Views/          ConsoleWindow, BreakpointsWindow, SettingsWindow
│   └── MainWindow.xaml Main debugger UI
└── Em68030.Tests/      xUnit tests (102 tests)
```

## Limitations

### CPU
- FPU uses 64-bit double internally as an approximation of the MC68882's 80-bit extended precision. This does not affect normal OS operation but may produce different results in high-precision floating-point calculations
- FPU Packed Decimal format is not implemented
- FSAVE/FRESTORE are simplified (null/idle frame only)
- CACR/CAAR registers are readable and writable but hardware cache emulation is not performed
- PTEST level 0 ATC search is simplified
- Cycle-accurate instruction timing is not guaranteed (cycle count is for measurement only, not used for timing control)

### Devices
- **SCSI**: Only standard commands used by NetBSD are implemented; not the full SCSI-2 command set
- **Ethernet**: Virtual network supports only ARP reply, ICMP Echo (ping), and TCP/UDP echo server. No connection to the host OS network stack (TAP/bridge)
- **Serial (SCC)**: No baud rate simulation or modem control signals (RTS/CTS)
- **RTC**: Read-only implementation returning host system time. Time set by the guest OS is not persisted
- **NVRAM**: In-memory only; not persisted to file
- **PCC**: Printer port and watchdog timer are not implemented

### Board
- VMEbus is not implemented
- NetBSD kernel can be loaded and run directly without a ROM image (built-in boot stub)

## Roadmap

- Ethernet: Connection to host OS network (TAP device / user-mode NAT)
- FPU: Accurate 80-bit extended precision emulation
- NVRAM file persistence
- Graphics output (framebuffer)

## Related Projects

- [Em68030 C++/WinUI3 version](https://github.com/hha0x617/Em68030_WinUI3Cpp) - Same emulator implemented in C++/WinRT (faster)

## License

[Apache License 2.0](LICENSE)
