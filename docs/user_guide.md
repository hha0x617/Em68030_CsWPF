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
| View | Breakpoints Window | | Open the breakpoints list |
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

## Breakpoints Window

Manages CPU breakpoints for debugging.

- **Setting a breakpoint**: Double-click an address in the disassembly view
- **Clearing all**: Click the **Clear All** button
- **Run to breakpoint**: Press F5; execution stops when a breakpoint address is reached

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
- **Z8530 SCC** — Serial console (real hardware). Always active; provides the serial console window.
- **WD33C93 SCSI** — Disk controller (real hardware). Configured via SCSI Disks below.
- **AMD LANCE** — Ethernet controller (real hardware). Configured via Network below.
- **16550A UART** — Virtual serial port at `$FFFE2000` (emulator-specific, Linux only). Provides `/dev/ttyS0` for Linux userspace console. Automatically enabled when Target OS is Linux.
- **Framebuffer** — Virtual display (emulator-specific). Configured via Framebuffer below.

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
