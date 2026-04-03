# TAP Bridge Network Setup Guide

This guide explains how to configure TAP bridge networking for the Em68030 emulator.
TAP bridge mode connects the guest directly to the host LAN, allowing the guest to
have its own IP address on the network.

## Overview

| Mode | Description | Use Case |
|------|-------------|----------|
| Virtual (Echo Server) | Built-in echo server, no host network | Testing network stack |
| NAT (Host Network) | User-mode NAT via emulator | Internet access without host configuration |
| **TAP (Bridge)** | **Direct L2 bridge to host LAN** | **Full network participation, DHCP, server hosting** |

## Prerequisites

- Windows 10/11
- OpenVPN TAP-Windows driver installed
- Physical network adapter connected to a LAN with a DHCP server (for DHCP mode)

## Step 1: Install TAP-Windows Driver

The TAP-Windows driver is included with the OpenVPN installer.

1. Download the OpenVPN installer from [OpenVPN Community Downloads](https://openvpn.net/community-downloads/)
2. Run the installer
3. Ensure the **TAP-Windows** component is selected during installation
4. Complete the installation and restart if prompted

**Verify:** Open Network Connections (`ncpa.cpl`). You should see a new adapter named
"TAP-Windows Adapter V9" (or similar).

## Step 2: Configure Windows Network Bridge

The TAP adapter must be bridged with your physical network adapter so that the guest
can communicate with the host LAN.

1. Open Network Connections: press `Win+R`, type `ncpa.cpl`, press Enter
2. Select both adapters:
   - Click the **TAP-Windows Adapter V9**
   - Hold `Ctrl` and click your **physical network adapter** (e.g., "Ethernet" or "Wi-Fi")
3. Right-click on either selected adapter
4. Click **"Bridge Connections"**
5. Wait for Windows to create the bridge (a new "Network Bridge" adapter appears)

> **Note:** The bridge may temporarily disconnect your host network. The connection
> will restore automatically once the bridge is configured.

> **Note:** Wi-Fi adapters may not support bridging on some systems. If bridging fails
> with Wi-Fi, use a wired Ethernet connection instead.

## Step 3: Configure the Emulator

1. Open the emulator settings (Settings menu or toolbar button)
2. In the **Network Mode** dropdown, select **"TAP (Bridge)"**
   - If this option is grayed out, the TAP-Windows driver is not installed (see Step 1)
3. In the **TAP Adapter** dropdown, select your TAP adapter
4. Click OK to save

## Step 4: Configure the Guest OS

### Linux

#### Option A: DHCP (Recommended)

Edit `/etc/systemd/network/10-eth0.network` on the guest:

```ini
[Match]
Name=eth0

[Network]
DHCP=yes
```

Apply the configuration:

```bash
systemctl restart systemd-networkd
```

Or use `dhcpcd` directly:

```bash
dhcpcd eth0
```

#### Option B: Static IP Address

Edit `/etc/systemd/network/10-eth0.network` on the guest:

```ini
[Match]
Name=eth0

[Network]
Address=192.168.1.100/24
Gateway=192.168.1.1
DNS=192.168.1.1
```

Replace the addresses with values appropriate for your network.

Apply the configuration:

```bash
systemctl restart systemd-networkd
```

### NetBSD

#### Option A: DHCP (Recommended)

Edit `/etc/rc.conf` on the guest:

```
ifconfig_le0=dhcp
```

Apply the configuration:

```
# /etc/rc.d/dhcpcd restart
```

#### Option B: Static IP Address

Edit `/etc/rc.conf` on the guest:

```
ifconfig_le0="inet 192.168.1.100 netmask 255.255.255.0"
defaultroute="192.168.1.1"
```

Edit `/etc/resolv.conf`:

```
nameserver 192.168.1.1
```

Replace the addresses with values appropriate for your network.

Apply the configuration:

```
# /etc/rc.d/network restart
```

## Step 5: Verify Connectivity

1. **Check IP address:**
   ```bash
   # Linux
   ip addr show eth0

   # NetBSD
   ifconfig le0
   ```

2. **Test connectivity:**
   ```bash
   ping -c 3 8.8.8.8
   ```

3. **Test DNS resolution:**
   ```bash
   ping -c 3 google.com
   ```

## Troubleshooting

### TAP adapter shows "Network cable unplugged"

The emulator is not connected to the TAP device. Ensure:
- The emulator settings have "TAP (Bridge)" selected with the correct adapter
- A kernel image is loaded and the guest OS is running
- The TAP adapter link becomes active when the emulator opens the device during boot

### Cannot select "TAP (Bridge)" in settings

The TAP-Windows driver is not installed, or no TAP adapters were detected.
Reinstall OpenVPN with the TAP-Windows component.

### DHCP does not assign an address

- Verify the Windows bridge is configured (Step 2)
- Check that the bridge adapter has network connectivity on the host
- Ensure a DHCP server is available on your LAN (typically your router)
- Try `dhcpcd -d eth0` for debug output

### Guest can ping the gateway but not the internet

- Check DNS configuration: `cat /etc/resolv.conf`
- Try pinging an IP address directly: `ping 8.8.8.8`
- If IP works but DNS doesn't, add a nameserver:
  ```bash
  echo "nameserver 8.8.8.8" > /etc/resolv.conf
  ```

### Bridge disconnects host network

This is normal briefly during bridge creation. If the host loses connectivity
permanently, remove the bridge (right-click → "Remove from bridge") and recreate it.

### Wi-Fi adapter cannot be bridged

Some Wi-Fi drivers do not support bridge mode. Use a wired Ethernet connection,
or use NAT mode instead.

## Comparison with NAT Mode

| Feature | NAT Mode | TAP Bridge Mode |
|---------|----------|-----------------|
| Host configuration | None required | TAP driver + bridge setup |
| Guest IP address | Private (10.0.2.x) | LAN address (DHCP or static) |
| Accessible from LAN | No | Yes |
| DHCP from LAN | No | Yes |
| Protocol support | TCP, UDP, ICMP | All (L2 bridge) |
| Administrator privileges | Not required | Not required (after setup) |
| Performance | Slightly higher overhead | Lower overhead (direct bridge) |
