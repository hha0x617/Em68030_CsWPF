# Getting Started

Setup guides for installing and booting guest operating systems on the Em68030 emulator.

## Guest OS Installation Guides

| Guide | Description |
|-------|-------------|
| [NetBSD](getting_started_netbsd.md) | Install and boot NetBSD/mvme68k 10.1 from a virtual SCSI disk |
| [Debian Linux](getting_started_debian.md) | Build a Debian root filesystem and boot Linux 6.12 |
| [Gentoo Linux](getting_started_gentoo.md) | Build a Gentoo root filesystem and boot Linux 6.12 |

## Additional Setup Guides

| Guide | Description |
|-------|-------------|
| [Framebuffer Display (Linux)](https://github.com/hha0x617/Em68030-Guest-Linux/blob/main/docs/setup_framebuffer.md) | fbcon and X Window System setup (Em68030-Guest-Linux) |
| [Framebuffer Display (NetBSD)](https://github.com/hha0x617/Em68030-Guest-NetBSD/blob/main/docs/setup_framebuffer.md) | wscons, wsfb, and X Window System setup (Em68030-Guest-NetBSD) |
| [NAT Network](setup_nat_network.md) | NAT mode guest network configuration (Linux/NetBSD) |
| [TAP Bridge Network](setup_tap_bridge.md) | Bridge networking via TAP-Windows adapter |

## Acknowledgments

This emulator exists only because several open-source operating system communities
continue to support the m68k architecture, long after most of the industry has moved
on. We would like to express our sincere thanks to the following projects and the
volunteers who sustain them:

- **The NetBSD Project** — for its long-standing commitment to "Of course it runs
  NetBSD" and for continuing to build, test, and release NetBSD/mvme68k as a Tier-II
  platform. Particular thanks to the `port-mvme68k` maintainers who keep the port
  functional against the moving target of pkgsrc, kernel changes, and evolving
  toolchains. See https://www.netbsd.org/ports/mvme68k/

- **The Linux kernel community** — and especially the m68k subsystem maintainers and
  contributors on the `linux-m68k` mailing list — for keeping the m68k architecture
  functional in modern Linux kernels. Without their work, neither the Debian nor the
  Gentoo guide in this directory would be possible. See http://www.linux-m68k.org/

- **The Debian Project and the Debian Ports team** — for maintaining the Debian/m68k
  unofficial port, running buildd infrastructure for the architecture, and keeping
  the `debian-68k` mailing list active. The broader Debian community's policy of
  welcoming non-release architectures is what makes this possible. See
  https://www.ports.debian.org/

- **The Gentoo Project and the Gentoo m68k team** — for publishing regular stage3
  tarballs, keeping the portage tree working for m68k, and supporting source-based
  installation on emulated hardware. The Gentoo community's approach of treating
  niche architectures as first-class is invaluable. See
  https://wiki.gentoo.org/wiki/M68k

Their continuous effort is what makes the installation guides in this directory
possible.
