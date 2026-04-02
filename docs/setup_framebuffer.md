# Framebuffer Display Setup Guide

This guide explains how to set up the framebuffer display, framebuffer console (fbcon),
and X Window System on the Em68030 emulator's Linux guest.

## Prerequisites

- Emulator framebuffer enabled in Settings → Framebuffer
- `em68030fb` kernel module installed and loaded (see [em68030-guest-linux](https://github.com/hha0x617/em68030-guest-linux))
- `em68030input` kernel module installed and loaded (for keyboard/mouse input)

## Part 1: Framebuffer Device (`/dev/fb0`)

### Kernel Configuration

Ensure the following options are enabled in the kernel config:

- `CONFIG_FB=y`
- `CONFIG_FB_SIMPLE=y` (or `=m`)
- `CONFIG_TRIM_UNUSED_KSYMS` — must be **disabled** (for out-of-tree module loading)

### Loading the Module

```bash
insmod /path/to/em68030fb.ko
```

Verify:

```bash
ls -la /dev/fb0
```

### Auto-load at Boot

```bash
mkdir -p /lib/modules/$(uname -r)/extra
cp em68030fb.ko /lib/modules/$(uname -r)/extra/
depmod -a

# systemd:
echo em68030fb > /etc/modules-load.d/em68030fb.conf
```

## Part 2: Framebuffer Console (fbcon)

fbcon displays a text console on the framebuffer, replacing the serial-only console
with a graphical text display in the emulator's framebuffer window.

### Kernel Configuration

Enable the following options:

- `CONFIG_FRAMEBUFFER_CONSOLE=y`
- `CONFIG_INPUT=y`
- `CONFIG_INPUT_EVDEV=y` (or `=m`)

Optional (for font selection):
- `CONFIG_FONTS=y`
- `CONFIG_FONT_8x16=y`

### Kernel Command Line

Add `console=tty0` to the kernel command line in the emulator settings:

```
root=/dev/sda1 console=tty0 console=ttyS0 earlyprintk
```

> **Note:** The last `console=` argument becomes `/dev/console` (primary console).
> With `console=ttyS0` last, systemd output goes to the serial console window.
> With `console=tty0` last, systemd output goes to the framebuffer.
> Kernel messages (`printk`) go to **all** registered consoles regardless of order.

### Input Device

The `em68030input` module must be loaded for keyboard input on fbcon:

```bash
insmod /path/to/em68030input.ko
echo em68030input > /etc/modules-load.d/em68030input.conf
```

### Console Size

The console size is automatically calculated from the framebuffer resolution and font:

| Resolution | Font | Columns × Rows |
|------------|------|-----------------|
| 640×480 | 8×16 | 80×30 |
| 800×600 | 8×16 | 100×37 |
| 1024×768 | 8×16 | 128×48 |

## Part 3: X Window System

X Window System provides a graphical desktop environment on the framebuffer.

### Install X.org

```bash
apt install xorg
```

This installs the X server, `xf86-video-fbdev` (framebuffer video driver), and
`xf86-input-libinput` (input driver). The em68030input module's absolute coordinate
mode is compatible with libinput.

### Install a Window Manager

A lightweight window manager is recommended for the emulated m68k system:

```bash
apt install twm
```

Create `~/.xinitrc`:

```bash
cat > ~/.xinitrc << 'EOF'
twm &
xterm
EOF
```

Other lightweight options: `fvwm`, `icewm`, `openbox` (if available for m68k).

> **Note:** Some window managers require `dbus-x11`. If the window manager fails
> to start, installing `dbus-x11` may resolve the issue: `apt install dbus-x11`

> **Limitation:** The emulator does not provide a virtual GPU — the framebuffer
> (`simplefb`) is a plain memory-mapped pixel buffer with no acceleration.
> Additionally, the emulated MC68030 (~270 MHz) lacks the processing power for
> software-based compositing or rendering. As a result, window managers that require
> a compositor or OpenGL will not work. This includes Enlightenment, Compiz, KWin,
> Mutter (GNOME), and xfwm4. Use lightweight, non-compositing window managers such
> as `twm`, `fvwm`, `icewm`, `openbox`, or `fluxbox`.

### Start X

```bash
startx
```

The X server uses `/dev/fb0` for display and `/dev/input/event*` for keyboard/mouse.

### Mouse Input

The emulator provides an absolute coordinate pointing device. The mouse position in
the framebuffer window maps directly to the guest screen coordinates. No pointer
grab or capture is needed.

### Keyboard Input

The framebuffer window captures keyboard events when focused. All standard keys
are supported (US keyboard layout). Special shortcuts:

- **Ctrl+Shift+V** — Paste clipboard text as key events

### Performance Notes

X Window System on the emulated MC68030 (~270 MHz) is functional but slow.
Expect significant latency when rendering complex UI elements. Lightweight
window managers and simple applications work best.

## Troubleshooting

### fbcon shows no output

- Verify `CONFIG_FRAMEBUFFER_CONSOLE=y` in kernel config:
  `zcat /proc/config.gz | grep FRAMEBUFFER_CONSOLE`
- Verify `console=tty0` is in the kernel command line:
  `cat /proc/cmdline`
- Verify `/dev/fb0` exists: `ls -la /dev/fb0`

### Keyboard input does not work on fbcon

- Verify `em68030input` module is loaded: `lsmod | grep em68030input`
- Verify input devices: `cat /proc/bus/input/devices`
- Ensure the emulator's framebuffer window has focus

### X server fails to start

- Check the log: `cat /var/log/Xorg.0.log | grep EE`
- Verify `xf86-video-fbdev` is installed: `dpkg -l | grep xf86-video-fbdev`
- Verify `/dev/fb0` exists

### Mouse cursor not visible in X

- Verify `em68030input` module is loaded
- Check X log for input device recognition:
  `grep -i "mouse\|keyboard" /var/log/Xorg.0.log`

### systemd output only on framebuffer, not serial console

The last `console=` argument in the kernel command line is the primary console.
Place `console=ttyS0` last to direct systemd output to the serial console:
```
console=tty0 console=ttyS0
```

### xeyes pupils do not follow the mouse cursor

The emulator's mouse input device uses absolute coordinates (tablet mode).
Some X applications like `xeyes` track relative pointer motion to determine
the cursor direction, and do not respond to absolute positioning devices.
This is expected behavior and not a bug.
