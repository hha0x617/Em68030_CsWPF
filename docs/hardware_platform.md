# MC68030 Emulator (C++ WinUI3) - Hardware Platform Emulation

Date: 2026-03-05

---

## Platform Overview

The emulator supports two hardware platform configurations:

| Platform | Description | Board Type Config |
|----------|-------------|-------------------|
| Generic | Minimal MC68030 system with custom console and HDD | `"Generic"` |
| MVME147 | Motorola MVME147 VMEbus single-board computer | `"MVME147"` |

---

## CPU / On-chip Peripherals (Both Platforms)

### MC68030 Processor

| Item | Detail |
|------|--------|
| File | `Core/MC68030.h/.cpp` |
| Chip | Motorola MC68030 (32-bit) |
| Registers | D0-D7, A0-A7, PC, SR, SSP, VBR, CACR, SFC, DFC |
| Status | Fully implemented |

### MC68030 On-chip MMU

| Item | Detail |
|------|--------|
| File | `Core/Mmu.h/.cpp` |
| Chip | MC68030 integrated MMU |
| Features | CRP/SRP root pointers, TC register, TT0/TT1 transparent translation, ATC (1024-entry hash), multi-level page table walk, FC support |
| Status | Fully implemented |

### MC68881/MC68882 FPU

| Item | Detail |
|------|--------|
| File | `Core/Fpu.h/.cpp`, `Core/FpuInstructionDecoder.h/.cpp` |
| Chip | MC68881/MC68882 compatible |
| Features | 8 FP registers, FPCR/FPSR/FPIAR, all arithmetic/transcendental/hyperbolic/logarithmic operations, FMOVECR constant ROM |
| Status | Fully implemented |

---

## Generic Platform

Minimal system for standalone MC68030 program execution.

### Memory Map

| Address Range | Size | Description |
|---------------|------|-------------|
| 0x00000000 - (configurable) | Up to 48 MB | Main RAM |
| 0x00FF0000 | 256 B | Console Device |
| 0x00FF1000 | 256 B | HDD Device |

### Console Device

| Item | Detail |
|------|--------|
| File | `IO/ConsoleDevice.h/.cpp` |
| Chip | Custom (not real hardware) |
| Base Address | 0x00FF0000 (configurable) |
| Interface | TRAP #15 handler for 147Bug-compatible monitor I/O |
| Features | Character/string input and output via callbacks |
| Status | Fully implemented |

### HDD Device

| Item | Detail |
|------|--------|
| File | `IO/HddDevice.h/.cpp` |
| Chip | Custom (not real hardware) |
| Base Address | 0x00FF1000 (configurable) |
| Interface | Memory-mapped register I/O with DMA |
| Sector Size | 512 bytes |
| Status | Fully implemented |

Register map:

| Offset | Register | Description |
|--------|----------|-------------|
| 0x00 | Command | NOP=0, READ=1, WRITE=2, STATUS=3 |
| 0x04 | LBA | 32-bit sector address |
| 0x08 | Status | READY=0x01, ERROR=0x02 |
| 0x0C | DMA Address | 32-bit memory address for transfer |

---

## MVME147 Platform

Emulates a Motorola MVME147 VMEbus single-board computer running NetBSD/mvme68k and Linux/m68k.

### Memory Map

| Address Range | Size | Description |
|---------------|------|-------------|
| 0x00000000 - (configurable) | Up to 32 MB | Main RAM |
| 0xFF800000 - 0xFFBFFFFF | 4 MB | ROM (optional) |
| 0xFFFE0000 - 0xFFFE07FF | 2 KB | MK48T02 NVRAM/RTC |
| 0xFFFE1000 - 0xFFFE102F | 48 B | PCC (Peripheral Channel Controller) |
| 0xFFFE1800 - 0xFFFE1803 | 4 B | LANCE Ethernet Controller |
| 0xFFFE2000 - 0xFFFE2007 | 8 B | 16550A UART (Linux only, virtual) |
| 0xFFFE3000 - 0xFFFE3003 | 4 B | Z8530 SCC (Serial Controller) |
| 0xFFFE4000 - 0xFFFE4001 | 2 B | WD33C93 SCSI Controller |
| 0xFFFE0000 - 0xFFFEFFFF | 64 KB | I/O Space Catch-all (fallback) |

### Interrupt Routing

All device interrupts are routed through the PCC:

```
Z8530 SCC  ──→ PCC ICR ($26) ──→ CPU IPL
LANCE      ──→ PCC ICR ($28) ──→ CPU IPL
WD33C93    ──→ PCC ICR ($2A) ──→ CPU IPL
Timer 1    ──→ PCC ICR ($18) ──→ CPU IPL
Timer 2    ──→ PCC ICR ($1A) ──→ CPU IPL
```

### PCC (Peripheral Channel Controller)

| Item | Detail |
|------|--------|
| File | `IO/PccDevice.h/.cpp` |
| Chip | MVME147 PCC (custom ASIC) |
| Base Address | 0xFFFE1000 |
| Size | 48 bytes (0x00-0x2F) |
| Status | Fully implemented |

Features:
- **2 x 16-bit timers** at 160 kHz (wall-clock based, independent of CPU speed)
- **DMA controller** registers (table address, data address, byte count, data hold)
- **12 interrupt control registers** (ICR) for all MVME147 devices
- **Hardware reset** via RESET instruction (0x4E70)
- **Watchdog timer** ($1D): writing 0xA5 arms the watchdog and triggers an immediate warm reboot (used by Linux `mvme147_reset()`)

Register map:

| Offset | Register | Description |
|--------|----------|-------------|
| $00-$03 | DMA Table Addr | DMA table pointer |
| $04-$07 | DMA Data Addr | DMA data pointer |
| $08-$0B | DMA Byte Count | DMA transfer size (24-bit) |
| $0C-$0F | DMA Data Hold | DMA data holding register |
| $10-$11 | Timer 1 Preload | Timer 1 initial value |
| $12-$13 | Timer 1 Count | Timer 1 current value |
| $14-$15 | Timer 2 Preload | Timer 2 initial value |
| $16-$17 | Timer 2 Count | Timer 2 current value |
| $18 | Timer 1 ICR | Timer 1 interrupt control |
| $19 | Timer 1 Control | Timer 1 enable/COC/overflow |
| $1A | Timer 2 ICR | Timer 2 interrupt control |
| $1B | Timer 2 Control | Timer 2 enable/COC/overflow |
| $1D | Watchdog Timer | Write 0x0A=clear, 0xA5=arm (triggers reset) |
| $26 | SCC ICR | Z8530 interrupt control |
| $28 | LANCE ICR | AM7990 interrupt control |
| $2A | SCSI ICR | WD33C93 interrupt control |
| $2C | SW1 ICR | Software interrupt 1 |
| $2D | Vector Base | Interrupt vector base (default 0x40) |
| $2E | SW2 ICR | Software interrupt 2 |

ICR format: INT (bit 7) | IEN (bit 3) | IL[2:0] (bits 2-0)

### Z8530 SCC (Serial Communications Controller)

| Item | Detail |
|------|--------|
| File | `IO/Z8530Device.h/.cpp` |
| Chip | Zilog Z8530 (dual-channel SCC) |
| Base Address | 0xFFFE3000 |
| Size | 4 bytes |
| Status | Fully implemented |

Features:
- **Dual-channel**: Channel A (console), Channel B (auxiliary)
- **16-register control model** per channel (WR0-WR15, RR0-RR15)
- **RX FIFO** with interrupt support
- **TX simulation** with idle tracking and periodic interrupt reassertion
- **DCD/CTS** always asserted (terminal connected)

> **Linux note**: The Linux kernel 6.x series removed the Z8530-based tty driver for MVME147 (`drivers/char/vme_scc.c` was present in 2.6.x but dropped during the 3.x/4.x cleanup). As a result, Linux/m68k on MVME147 has no kernel tty driver for the Z8530. The SCC is still used by Linux's early boot console (`earlyprintk`) via direct register access in `arch/m68k/kernel/head.S`, but userspace programs (init, shell, getty) require a proper tty device backed by a serial driver. Since no upstream Z8530 tty driver exists for MVME147 in modern kernels, the emulator provides a virtual 16550A UART (see below) as a substitute console device for Linux. NetBSD is unaffected -- it has its own Z8530 driver (`zs(4)`) and uses the SCC for all console I/O.

Address map:

| Offset | Register |
|--------|----------|
| $0 | Channel B Control |
| $1 | Channel B Data |
| $2 | Channel A Control |
| $3 | Channel A Data |

### Virtual 16550A UART (Linux Console)

| Item | Detail |
|------|--------|
| File | `IO/Uart16550Device.h/.cpp` |
| Chip | Virtual 16550A UART (not present on real MVME147) |
| Base Address | 0xFFFE2000 |
| Size | 8 bytes |
| Activated | Only when Target OS = `Linux` |
| Status | Fully implemented |

This device does not exist on real MVME147 hardware. It is a virtual peripheral added by the emulator to provide Linux userspace with a working console (`/dev/ttyS0`).

**Background**: The real MVME147 uses a Z8530 SCC for serial communication. The Linux kernel's Z8530 tty driver for VME boards (`vme_scc.c`) was removed in the kernel 3.x/4.x era and is not present in 6.x. Without a tty driver, Linux can output kernel messages via earlyprintk (direct SCC register writes in assembly) but cannot provide a `/dev/ttyS*` device for userspace. The emulator solves this by mapping a 16550A-compatible UART at 0xFFFE2000, allowing the well-supported `8250/16550` serial driver (`CONFIG_SERIAL_8250`) to register `/dev/ttyS0` as the system console.

**Kernel modifications required**: Two patches to the Linux kernel source are needed:
1. `arch/m68k/mvme147/config.c` -- Register the UART as a platform device (`serial8250` with `mapbase=0xFFFE2000`)
2. `arch/m68k/kernel/early_printk.c` -- (Optional) Prevent early console unregistration on MVME147 for `keep_bootcon` support

See the [Getting Started: Debian](getting_started_debian.md) or [Getting Started: Gentoo](getting_started_gentoo.md) guides for the complete patch instructions.

Features:
- **Full 16550A register set**: RBR/THR, IER, IIR/FCR, LCR, MCR, LSR, MSR, SCR
- **DLAB** (Divisor Latch Access Bit) for baud rate divisor registers
- **MCR loopback mode** (bit 4) for 8250 driver autodetection and FIFO size probing
- **64-byte receive FIFO**
- **Interrupt output** (active high) with RX data and TX empty priorities
- **TX always ready**: LSR reports THRE and TEMT permanently set (instantaneous transmission)

Register map (DLAB=0):

| Offset | Read | Write |
|--------|------|-------|
| $0 | RBR (Receive Buffer) | THR (Transmit Holding) |
| $1 | IER (Interrupt Enable) | IER |
| $2 | IIR (Interrupt ID) | FCR (FIFO Control) |
| $3 | LCR (Line Control) | LCR |
| $4 | MCR (Modem Control) | MCR |
| $5 | LSR (Line Status) | (ignored) |
| $6 | MSR (Modem Status) | (ignored) |
| $7 | SCR (Scratch) | SCR |

Register map (DLAB=1, LCR bit 7 set):

| Offset | Register |
|--------|----------|
| $0 | DLL (Divisor Latch Low) |
| $1 | DLM (Divisor Latch High) |

### MK48T02 NVRAM/RTC

| Item | Detail |
|------|--------|
| File | `IO/Mk48t02Device.h/.cpp` |
| Chip | Mostek MK48T02 (2 KB SRAM + RTC) |
| Base Address | 0xFFFE0000 |
| Size | 2048 bytes |
| Status | Fully implemented |

Structure:
- **0x0000-0x07F7**: General SRAM (NVRAM storage)
- **0x07F8-0x07FF**: Clock registers (BCD format)

Clock registers:

| Offset | Register | Format |
|--------|----------|--------|
| $7F8 | Control | bit 7: Write, bit 6: Read |
| $7F9 | Seconds | BCD 0-59 |
| $7FA | Minutes | BCD 0-59 |
| $7FB | Hours | BCD 0-23 |
| $7FC | Day of Week | 1-7 |
| $7FD | Date | BCD 1-31 |
| $7FE | Month | BCD 1-12 |
| $7FF | Year | BCD 0-99 (base year 1968) |

Pre-populated MVME147 configuration:
- $0774: End+1 of onboard RAM (32-bit BE)
- $0778-$077A: Ethernet MAC address (08:00:3E prefix)

### WD33C93 SCSI Controller

| Item | Detail |
|------|--------|
| File | `IO/Wd33c93Device.h/.cpp` |
| Chip | Western Digital WD33C93 (SCSI Level I) |
| Base Address | 0xFFFE4000 |
| Size | 2 ports |
| Status | Fully implemented |

Features:
- **Phase-by-phase protocol**: MSG_OUT -> CMD -> DATA_IN/OUT -> STATUS -> MSG_IN
- **32 internal registers** ($00-$1F, indirect access)
- **PIO and DMA transfer** modes
- **SBT (Single Byte Transfer)** mode
- **Up to 8 SCSI targets** (IDs 0-7)

Port access:

| Offset | Read | Write |
|--------|------|-------|
| $0 | ASR (Auxiliary Status) | Address Register |
| $1 | Data Port | Data Port |

ASR bits: INT (7), BSY (5), CIP (4), PE (1), DBR (0)

Commands:

| Code | Command | Description |
|------|---------|-------------|
| 0x00 | RESET | Reset controller |
| 0x01 | ABORT | Abort current command |
| 0x04 | DISCONNECT | Close connection |
| 0x06 | SEL_ATN | Select target with attention |
| 0x08 | SEL_ATN_XFER | Select + auto transfer |
| 0x20 | XFER_INFO | Phase-based data transfer |

### SCSI Disk Target

| Item | Detail |
|------|--------|
| File | `IO/ScsiDisk.h/.cpp` |
| Interface | IScsiTarget |
| Sector Size | 512 bytes |
| Max Targets | 8 (IDs 0-7) |
| Status | Fully implemented |

Supported SCSI commands:

| Opcode | Command | Description |
|--------|---------|-------------|
| 0x00 | TEST UNIT READY | Check device ready |
| 0x03 | REQUEST SENSE | Return sense data (18 bytes) |
| 0x08 | READ(6) | Read sectors (21-bit LBA) |
| 0x0A | WRITE(6) | Write sectors (21-bit LBA) |
| 0x12 | INQUIRY | Device identification (36 bytes) |
| 0x15 | MODE SELECT(6) | Accept parameters (ignored) |
| 0x1A | MODE SENSE(6) | Return geometry (pages 3, 4) |
| 0x1B | START STOP UNIT | Start/stop (accepted) |
| 0x1E | PREVENT/ALLOW REMOVAL | Media lock (accepted) |
| 0x25 | READ CAPACITY | Return size + block size |
| 0x28 | READ(10) | Read sectors (32-bit LBA) |
| 0x2A | WRITE(10) | Write sectors (32-bit LBA) |
| 0x35 | SYNCHRONIZE CACHE | Flush to disk |
| 0x55 | MODE SELECT(10) | Accept parameters (ignored) |
| 0x5A | MODE SENSE(10) | Return geometry |

### SCSI CD-ROM Target

| Item | Detail |
|------|--------|
| File | `IO/ScsiCdrom.h/.cpp` |
| Interface | IScsiTarget |
| Sector Size | 2048 bytes (ISO 9660) |
| Max Targets | 1 |
| Status | Fully implemented (read-only) |

Additional SCSI commands (beyond disk):

| Opcode | Command | Description |
|--------|---------|-------------|
| 0x43 | READ TOC | Table of contents (single data track, MSF/LBA) |

Limitations: Read-only. WRITE commands not supported.

### AM7990 LANCE Ethernet Controller

| Item | Detail |
|------|--------|
| File | `IO/LanceDevice.h/.cpp` |
| Chip | AMD AM7990 LANCE |
| Base Address | 0xFFFE1800 |
| Size | 4 bytes |
| Status | Fully implemented |

Port access:

| Offset | Register | Description |
|--------|----------|-------------|
| $0 | RDP | Register Data Port (CSR read/write) |
| $2 | RAP | Register Address Port (CSR select) |

CSR registers:

| CSR | Description |
|-----|-------------|
| CSR0 | Control/Status (INIT, STRT, STOP, TDMD, RXON, TXON, INEA, INTR, IDON, TINT, RINT, etc.) |
| CSR1 | Init Block Address Low |
| CSR2 | Init Block Address High |
| CSR3 | Bus Master Control |

Features:
- **Initialization block**: Mode, MAC address, multicast filter, RX/TX ring descriptors
- **TX/RX ring processing** with OWN-bit protocol
- **Pluggable network backend** via INetworkHandler interface

### Network Backends

| Backend | File | Description | Config |
|---------|------|-------------|--------|
| VirtualNetworkHandler | `IO/VirtualNetworkHandler.h/.cpp` | Internal echo server (ARP, ICMP, TCP/UDP echo). No host network access. | NetworkMode = "Virtual" (default) |
| SlirpNetworkHandler | `IO/SlirpNetworkHandler.h/.cpp` | User-mode NAT via libslirp. Full host network access. | NetworkMode = "NAT" |

### MVME147 I/O Space Catch-all

| Item | Detail |
|------|--------|
| File | `IO/Mvme147IoSpaceDevice.h/.cpp` |
| Base Address | 0xFFFE0000 |
| Size | 64 KB |
| Purpose | Prevents bus errors on kernel probes of unmapped I/O addresses |
| Behavior | Returns 0x00 for all reads, ignores all writes |
| Status | Fully implemented |

---

## Device Summary

| Device | Chip | Address | Platform | Status |
|--------|------|---------|----------|--------|
| CPU | MC68030 | - | Both | Complete |
| MMU | MC68030 on-chip | - | Both | Complete |
| FPU | MC68881/882 | - | Both | Complete |
| Console | Custom | 0x00FF0000 | Generic | Complete |
| HDD | Custom | 0x00FF1000 | Generic | Complete |
| PCC | MVME147 ASIC | 0xFFFE1000 | MVME147 | Complete |
| SCC | Zilog Z8530 | 0xFFFE3000 | MVME147 | Complete |
| UART | Virtual 16550A | 0xFFFE2000 | MVME147 (Linux) | Complete |
| RTC/NVRAM | Mostek MK48T02 | 0xFFFE0000 | MVME147 | Complete |
| SCSI | WD WD33C93 | 0xFFFE4000 | MVME147 | Complete |
| SCSI Disk | File-backed | via WD33C93 | MVME147 | Complete |
| SCSI CD-ROM | ISO 9660 | via WD33C93 | MVME147 | Complete (R/O) |
| Ethernet | AMD AM7990 | 0xFFFE1800 | MVME147 | Complete |
| Virtual Net | Echo server | backend | MVME147 | Complete |
| NAT Net | libslirp | backend | MVME147 | Complete |
| I/O Catch-all | - | 0xFFFE0000 | MVME147 | Complete |
