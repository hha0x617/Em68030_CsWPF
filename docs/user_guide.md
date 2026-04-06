# Em68030 User Guide

## Quick Start

### Installation

1. Extract the archive to a folder of your choice
2. Run `Em68030.exe`

> **Note**: Windows Defender SmartScreen may block the unsigned executable on first run.
> Click "More info" → "Run anyway", or right-click the exe → Properties → check "Unblock".

### First Boot (NetBSD)

1. Open **Settings** (Settings menu)
2. Set **Board Type** to "MVME147"
3. Add a SCSI disk image: click **+ Add Disk**, browse to your disk image, select SCSI ID
4. (Optional) Set a CD-ROM ISO image for installation
5. Click **OK**
6. **File → Open ELF** (Ctrl+E) to load a NetBSD kernel (`netbsd-GENERIC`)
7. Press **Run** (F5) to start execution

For detailed OS installation guides, see [Getting Started](getting_started.md).

### First Boot (Linux)

1. Open **Settings**, set **Board Type** to "MVME147"
2. Set **Target OS** to "Linux"
3. Set the **Command Line** (e.g., `root=/dev/sda1 console=tty0 console=ttyS0 earlyprintk`)
4. Add SCSI disk images and click **OK**
5. **File → Open ELF** (Ctrl+E) to load a Linux kernel (`vmlinux`)
6. Press **Run** (F5)

## Main Window

### Menu Bar

| Menu | Item | Shortcut | Description |
|------|------|----------|-------------|
| File | Open ELF... | Ctrl+E | Load an ELF executable |
| File | Open Binary... | Ctrl+O | Load a raw binary file (prompts for load address) |
| File | Open S-Record... | Ctrl+S | Load a Motorola S-Record file |
| File | Exit | Alt+F4 | Close the application |
| Run | Run | F5 | Start or resume CPU execution |
| Run | Stop | Shift+F5 | Halt CPU execution |
| Run | Step | F10 | Execute one instruction |
| Run | Run to Cursor | F4 | Execute until the selected address |
| Run | Set PC to Cursor | | Set PC to the selected disassembly address |
| Run | Reset | | CPU soft reset |
| Run | Full Reset | | Complete system reset |
| View | Serial Console Window | | Open the serial console |
| View | Framebuffer Window | | Open the framebuffer display |
| View | Breakpoints Window | | Open the breakpoints and watchpoints list |
| View | Call Stack Window | | Open the call stack viewer |
| Settings | Emulator Settings... | | Open the settings dialog |
| Help | About... | | Version and license information |

### Toolbar

| Button | Description |
|--------|-------------|
| Run (F5) | Start or resume execution |
| Stop | Halt execution |
| Step (F10) | Single-step one instruction |
| Reset | CPU soft reset |
| Full Reset | Complete system reset |
| Trace | Toggle execution trace mode |

### Disassembly View (Upper Left)

Displays disassembled instructions around the current PC.

- **Address field**: Enter a hex address and press Enter or click "Go" to navigate
- **Follow PC**: When enabled, the view automatically follows the program counter
- **Right-click context menu**: Copy, Run to Cursor, Set PC to Address

### Register Panel (Right)

Displays and allows editing of CPU registers.

- **D0–D7**: Data registers
- **A0–A7**: Address registers (A7 is the stack pointer)
- **PC**: Program counter
- **SR**: Status register with individual flag checkboxes (X, N, Z, V, C, S, T)
- **SSP**: Supervisor stack pointer
- **VBR**: Vector base register
- **FP0–FP7**: Floating-point registers
- **FPCR/FPSR/FPIAR**: FPU control registers

To edit registers: click **Edit**, modify values, then click **Apply** (or **Cancel** to discard).

### Memory Dump View (Lower Left)

Displays raw memory contents in hex and ASCII.

- **Address field**: Enter a hex address to navigate
- **Size field**: Number of bytes to display
- **Editing**: Click **Edit**, modify hex values, then **Apply**

**Navigation keys** (in edit mode):
| Key | Action |
|-----|--------|
| Arrow keys | Move between cells |
| Tab / Shift+Tab | Next / previous cell |
| Enter | Confirm edit |
| Escape | Cancel edit |

### Status Bar

Displays runtime information at the bottom of the main window:

- **Running status**: Running: True/False
- **Network mode**: Net: Virtual / NAT / TAP (Bridge)
- **JIT status**: JIT: ON / OFF
- **Trace status**: [TRACE ON] (when active)

## Serial Console Window

VT100-compatible terminal emulator for serial I/O.

### Features
- Scrollback buffer (configurable, default 2000 lines)
- Resizable window (adjusts terminal columns and rows)
- Text selection and copy
- Clipboard paste
- Text search with regular expression support
- Soft-wrap aware copy (visual line wraps do not insert newlines in clipboard)

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+C | Copy selected text (or send ETX if no selection) |
| Ctrl+V | Paste clipboard text to guest |
| Ctrl+Shift+F | Open search bar |
| F3 | Find next match (search mode) |
| Shift+F3 | Find previous match (search mode) |
| Escape | Close search bar (search mode) |
| Ctrl+A–Z | Send control codes to guest |

### Search

Press **Ctrl+Shift+F** to open the search bar at the bottom of the window.

- Type a search term and press **Enter** or **F3** to find the next match
- **Shift+F3** to find the previous match
- The **`.*`** toggle button enables regular expression mode (case-insensitive)
- Match counter shows the current match number and total (e.g., "3/15")
- Press **Escape** to close the search bar

> **Note (C# WPF)**: In regex mode, inline modifiers such as `(?-i)` are supported
> for case-sensitive matching.
>
> **Note (C++ WinUI3)**: ECMAScript regex syntax is used; inline modifiers are not supported.

### Context Menu

Right-click in the console to access:
- **Copy** (Ctrl+C)
- **Paste** (Ctrl+V)
- **Select All** (Ctrl+A)

## Framebuffer Window

Displays the emulated framebuffer output. Requires the framebuffer to be enabled in Settings
and the `em68030fb` kernel module loaded in the guest OS.

### Features
- Real-time VRAM display (8/16/32-bit color)
- Keyboard input capture (when focused)
- Mouse input (absolute coordinates for X Window System compatibility)

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+Shift+V | Paste clipboard text as key events |

### Mouse Input

The mouse position in the framebuffer window maps directly to guest screen coordinates
(absolute positioning). No pointer grab is required. Left, right, and middle mouse
buttons are forwarded to the guest.

## Breakpoints & Watchpoints Window

Manages breakpoints and memory watchpoints for debugging. Open via **View → Breakpoints Window**.

### Breakpoints

A breakpoint pauses execution when the CPU reaches a specific address.

- **Setting a breakpoint**: Double-click an address in the disassembly view
- **Enable/Disable**: Toggle the checkbox next to each breakpoint
- **Delete**: Click the **Del** button on a specific breakpoint
- **Clear All**: Click the **Clear All** button to remove all breakpoints and watchpoints
- **Run to breakpoint**: Press F5; execution stops when a breakpoint address is reached
- **Jump to address**: Double-click a breakpoint in the list to navigate the disassembly view

### Conditional Breakpoints

A breakpoint can have an optional condition expression. Execution only pauses when
the condition evaluates to true.

To set or edit a condition:

1. Open the **Breakpoints Window** (View → Breakpoints Window)
2. Click the **Cond** button next to the target breakpoint
3. Enter a condition expression in the dialog (e.g., `D0==0x1234`)
4. Click **OK** to apply, or **Clear** to remove the condition

Breakpoints with conditions display the expression below the address as `if D0==0x1234`.

Conditions support:

| Element | Examples | Description |
|---------|----------|-------------|
| Data registers | `D0`, `D7` | 32-bit value of the data register |
| Address registers | `A0`, `A7`, `SP` | 32-bit value of the address register (`SP` = `A7`) |
| Special registers | `PC`, `SR` | Program counter or status register |
| Comparison | `==`, `!=`, `<`, `>`, `<=`, `>=` | Unsigned 32-bit comparison |
| Bitwise AND | `SR&0x2000!=0` | Test specific bits: `(SR & 0x2000) != 0` |
| Memory read | `[0x1000].b`, `[A0].w`, `[$2000].l` | Read byte/word/long from address |
| Address arithmetic | `[A7+12].l`, `[A0+0xC].w`, `[A7-4].b` | `+` or `-` offset inside `[...]` |
| IN set | `D0 IN {1, 3, 7, 20}` | True if value matches any element in `{...}` |
| Logical OR | `expr1 \|\| expr2` | True if either expression is true |
| Logical AND | `expr1 && expr2` | True if both expressions are true |
| Grouping | `(expr1 \|\| expr2) && expr3` | `()` overrides default precedence |
| Number formats | `255`, `0xFF`, `$FF` | Decimal, C-style hex, or Motorola-style hex |

`&&` has higher precedence than `||`: `A || B && C` is evaluated as `A || (B && C)`.
Use `()` to override: `(A || B) && C`.

**Condition examples:**

| Condition | Meaning |
|-----------|---------|
| `D0==0x1234` | Break when D0 equals 0x1234 |
| `A7<0x10000` | Break when stack pointer is below 64KB |
| `SR&0x2000!=0` | Break when in supervisor mode |
| `[0x1000].w==0xBEEF` | Break when word at address $1000 equals $BEEF |
| `[A7+12].l==0` | Break when longword at (A7+12) equals 0 (stack-relative) |
| `[A0+D1].w!=0` | Break when word at (A0+D1) is non-zero |
| `D0 IN {1, 3, 7, 20}` | Break when D0 is 1, 3, 7, or 20 |
| `[A7+12].l IN {1, 3}` | Break when longword at (A7+12) is 1 or 3 |
| `D0==1 \|\| D0==3` | Break when D0 is 1 or 3 |
| `D0>0 && D0<100` | Break when D0 is between 1 and 99 |
| `(D0==1 \|\| D0==3) && SR&0x2000!=0` | D0 is 1 or 3, AND in supervisor mode |
| `D0==D1` | Break when D0 and D1 have the same value |
| `D0` | Break when D0 is non-zero (bare expression) |

If a condition is empty or cannot be parsed, the breakpoint is unconditional (always triggers).

### Memory Watchpoints

A watchpoint pauses execution when a specific memory address is read or written.

- **Add Watchpoint**: Click the **Add Watchpoint** button to open the dialog
  - **Address**: Memory address to watch (hex: `0x1000`, `$1000`, or plain `1000`)
  - **Size**: Byte (.B), Word (.W), or Long (.L)
  - **Type**: Write only, Read only, or Read/Write
  - **Condition** (optional): Same expression syntax as conditional breakpoints
- **Enable/Disable**: Toggle the checkbox
- **Delete**: Click the **Del** button

When a watchpoint triggers, the status bar shows the access details:
```
Write watchpoint at $00001000.W: $0000 -> $BEEF
```

> **Performance note**: Watchpoints add overhead to every memory access while active.
> Emulation speed will be reduced when watchpoints are enabled. Disable or remove
> watchpoints when they are no longer needed.

## Call Stack Window (Experimental)

> **This feature is under development.** The accuracy of the call stack depends on
> how the guest code was compiled. See "Known Limitations" below.

Displays the call stack by walking the A6 frame pointer chain. Open via **View → Call Stack Window**.
Call stack frame addresses are also shown in the disassembly view as green triangle markers (**▸**).

The window shows:

- **Frame #0**: Current PC and A6 value (highlighted in yellow)
- **A6 chain frames**: Return address and saved frame pointer, traced via the
  `LINK A6` / `UNLK A6` convention (`[A6]` = saved FP, `[A6+4]` = return address)
- **Heuristic frames** (gray, marked `(heuristic)`): The window scans the top 4KB of
  the stack for values that look like code addresses (even addresses within the program
  range). Duplicates with A6 chain results are removed.

**Operations:**

- **Double-click** a frame to navigate the disassembly view to that address
- The window **auto-refreshes** on every Step, Stop, or breakpoint hit
- While running, the window shows "Running..."

**When the call stack works well:**

- Code compiled with `-fno-omit-frame-pointer` (A6 chain is fully intact)
- User programs compiled with `-O0` for debugging
- Kernel panic analysis (NetBSD kernel uses frame pointers by default in many paths)

**Known Limitations:**

- **Idle loops and interrupt handlers**: Stopping the CPU during an idle loop or
  interrupt handler typically yields only the current frame (#0), because A6 does
  not form a valid frame chain in these contexts.
- **`-fomit-frame-pointer` (GCC default with optimization)**: Most optimized code
  does not use `LINK A6` / `UNLK A6`, so the A6 chain is empty or incomplete.
- **Heuristic false positives**: The stack scan cannot distinguish real return
  addresses from data values that happen to fall within the code address range.
  This can produce noisy results. Double-click a heuristic frame to verify in the
  disassembly view.
- **No symbol resolution**: Addresses are displayed as raw hex values. There is no
  ELF symbol table integration to show function names.

## Settings Reference

Settings are saved to `%LOCALAPPDATA%\Em68030_CsWPF\appsettings.json`.
The settings dialog is organized into three tabs: **General**, **MVME147**, and **Advanced**.

### General Tab

#### Board Type

| Setting | Values | Default | Description |
|---------|--------|---------|-------------|
| Board Type | Generic, MVME147 | Generic | Selects the emulated board |

#### Memory

| Setting | Range | Default | Description |
|---------|-------|---------|-------------|
| Memory Size (MB) | 4–4096 | 48 | Guest RAM size |

> **Note**: Memory size changes take effect after reloading the kernel image.

#### I/O Devices

These are emulator-specific virtual devices for the Generic board mode.
They do not correspond to real hardware.

| Setting | Default | Description |
|---------|---------|-------------|
| Console Device Enabled | On | Virtual serial console (MMIO) |
| Console Base Addr | 0x00FF0000 | Console MMIO address |
| Scrollback Lines | 2000 | Console scrollback buffer size |
| Terminal Size | 80 x 24 | Console columns and rows |
| HDD Device Enabled | On | Virtual HDD controller (MMIO) |
| HDD Base Addr | 0x00FF1000 | HDD MMIO address |

#### Display

| Setting | Default | Description |
|---------|---------|-------------|
| Font Family | Consolas | Console and disassembly font |
| Font Size | 14.0 | Font size in points |

### MVME147 Tab

This tab is only enabled when Board Type is set to MVME147.

The MVME147 board emulates the following devices:

| Device | Address | Type | Description |
|--------|---------|------|-------------|
| PCC | `$FFFE1000` | Real hardware | Peripheral Channel Controller (timers, interrupt control) |
| AMD LANCE | `$FFFE1800` | Real hardware | Ethernet controller. Configured via Network below |
| 16550A UART | `$FFFE2000` | Virtual (Linux only) | Serial port for `/dev/ttyS0`. Auto-enabled when Target OS is Linux |
| Z8530 SCC | `$FFFE3000` | Real hardware | Serial console. Always active |
| WD33C93 SCSI | `$FFFE4000` | Real hardware | Disk controller. Configured via SCSI Disks below |
| Mk48T02 RTC | `$FFFE0000` | Real hardware | Real-time clock and NVRAM |
| Framebuffer | `$FFFE8000` | Virtual | Display device. Configured via Framebuffer below |
| Input | `$FFFE9000` | Virtual | Keyboard/mouse (framebuffer only) |

> Virtual devices are emulator-specific extensions not present on real MVME147 hardware.
> For detailed register maps and implementation notes, see [Hardware Platform](hardware_platform.md).

#### MVME147 Settings

| Setting | Description |
|---------|-------------|
| ROM Image | Path to MVME147 ROM image (optional) |

#### Target OS

| Setting | Values | Default | Description |
|---------|--------|---------|-------------|
| Operating System | NetBSD, Linux | NetBSD | Guest OS type |

**NetBSD settings:**

| Setting | Description |
|---------|-------------|
| Kernel Image | Path to NetBSD kernel (auto-loaded on startup if specified) |
| Boot Partition | a or b |

**Linux settings:**

| Setting | Description |
|---------|-------------|
| Kernel Image | Path to Linux kernel (auto-loaded on startup if specified) |
| Command Line | Kernel command line |

> **Note**: Kernel images for NetBSD and Linux are stored separately, so switching
> between operating systems does not require re-specifying the kernel path.

#### SCSI Disks

SCSI disk emulation via the WD33C93 SCSI controller (real MVME147 hardware).

| Setting | Description |
|---------|-------------|
| Disk Image Path | Path to a raw SCSI disk image file |
| SCSI ID | 0–6 (each disk must have a unique ID) |
| + Add Disk | Add another SCSI disk |
| Create | Create a new empty disk image (size in MB) |

> **Note**: SCSI disk path changes take effect after rebooting the guest OS.
> CD-ROM ISO images can be swapped at any time.

#### Network

Network emulation via the AMD LANCE Ethernet controller (real MVME147 hardware).
The network backend determines how the emulated NIC connects to the host.

| Setting | Values | Default | Description |
|---------|--------|---------|-------------|
| Network Mode | Virtual, NAT, TAP (Bridge) | Virtual | Network backend |
| TAP Adapter | (dropdown) | | TAP-Windows adapter (TAP mode only) |
| Gateway IP | IP address | 10.0.2.2 | NAT gateway address (NAT mode only) |
| Gateway MAC | MAC address | 52:54:00:12:34:56 | NAT gateway MAC (NAT mode only) |

> **Note**: Network mode changes take effect after reloading the kernel image.
> For guest OS configuration, see:
> - [NAT Network Setup Guide](setup_nat_network.md) — NAT mode guest configuration (Linux/NetBSD)
> - [TAP Bridge Setup Guide](setup_tap_bridge.md) — TAP bridge mode (requires TAP-Windows driver)

#### Framebuffer

Emulator-specific virtual framebuffer device. The real MVME147 does not have a built-in display.

| Setting | Values | Default | Description |
|---------|--------|---------|-------------|
| Enable | On/Off | Off | Enable virtual framebuffer |
| Resolution | 320x240 to 1920x1080 | 640x480 | Display resolution |
| Bits per Pixel | 8, 16, 32 | 16 | Color depth |

> **Note**: VRAM is placed at the top of RAM automatically.

### Advanced Tab

#### Performance

| Setting | Default | Description |
|---------|---------|-------------|
| Enable JIT Compiler | Off | Experimental JIT for register-only blocks |
| Min Block Length | 3 | Minimum instructions for JIT compilation |
| Compile Threshold | 32 | Executions before a block is compiled |

#### Debug

| Setting | Default | Description |
|---------|---------|-------------|
| Show Trace Button | Off | Shows the Trace button on the toolbar |

When the Trace button is visible and clicked, verbose instruction tracing is toggled.
Trace output is written to `%LOCALAPPDATA%\Em68030_CsWPF\tracelog.txt`.
The trace includes instruction execution, exception vectors, and syscall entries.

> **Note:** Tracing significantly reduces emulation speed and produces large log files.
> Use only for debugging specific issues.

## Command Line Arguments

| Argument | Example | Description |
|----------|---------|-------------|
| `--lang=xx-XX` | `--lang=en-US` | Override UI language (e.g., `en-US`, `ja-JP`) |

When no argument is specified, the system language is used.
