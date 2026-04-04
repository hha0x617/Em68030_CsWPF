# NAT Network Setup Guide

This guide explains how to configure NAT (Host Network) mode for the Em68030 emulator.
NAT mode provides the guest with internet access through the host's network without
any host-side configuration.

## Overview

| Mode | Description | Use Case |
|------|-------------|----------|
| Virtual (Echo Server) | Built-in echo server, no host network | Testing network stack (no guest config needed) |
| **NAT (Host Network)** | **User-mode NAT via emulator** | **Internet access without host configuration** |
| TAP (Bridge) | Direct L2 bridge to host LAN | Full network participation, DHCP, server hosting ([setup guide](setup_tap_bridge.md)) |

## How NAT Mode Works

The emulator's NAT implementation forwards UDP/TCP packets to the destination IP
as-is via the host OS network stack. The guest communicates through a virtual gateway.

- Default gateway IP: `10.0.2.2`
- Default guest IP: `10.0.2.15/24`
- These values can be changed in Settings → Network

> **Note:** There is no built-in DNS forwarder. The guest's `/etc/resolv.conf` (or
> equivalent) must point to a DNS server reachable from the host (e.g., `8.8.8.8`,
> or your LAN's DNS server).

## Step 1: Configure the Emulator

1. Open the emulator settings (Settings menu or toolbar button)
2. In the **Network Mode** dropdown, select **"NAT (Host Network)"**
3. Optionally adjust **Gateway IP** (default: `10.0.2.2`)
4. Click OK to save
5. Reload the kernel image for the change to take effect

## Step 2: Configure the Guest OS

### Linux (systemd — Debian, Gentoo with systemd)

```ini
# /etc/systemd/network/10-eth0.network
[Match]
Name=eth0

[Network]
Address=10.0.2.15/24
Gateway=10.0.2.2
```

```bash
# /etc/resolv.conf
nameserver 8.8.8.8
```

Apply:

```bash
systemctl enable systemd-networkd
systemctl restart systemd-networkd
```

### Linux (OpenRC — Gentoo with OpenRC)

```bash
# /etc/conf.d/net
config_eth0="10.0.2.15/24"
routes_eth0="default via 10.0.2.2"
```

```bash
# /etc/resolv.conf
nameserver 8.8.8.8
```

Create the service link and start:

```bash
cd /etc/init.d && ln -s net.lo net.eth0
rc-service net.eth0 start
```

### NetBSD

Edit `/etc/rc.conf`:

```
ifconfig_le0="inet 10.0.2.15 netmask 255.255.255.0"
defaultroute="10.0.2.2"
```

Edit `/etc/resolv.conf`:

```
nameserver 8.8.8.8
```

Apply:

```
# /etc/rc.d/network restart
```

## Step 3: Verify Connectivity

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

### Guest cannot reach the gateway (10.0.2.2)

- Verify the emulator's network mode is set to "NAT (Host Network)" in Settings
- Reload the kernel image after changing the network mode
- Verify the guest IP and gateway match the emulator settings

### IP connectivity works but DNS does not

- Check `/etc/resolv.conf` points to a working DNS server
- Try a public DNS server: `nameserver 8.8.8.8`
- Test with IP address directly: `ping 8.8.8.8`

### Connection is slow or packets are dropped

- NAT mode has slightly higher overhead than TAP bridge mode due to user-space
  packet processing
- For better performance, consider [TAP Bridge mode](setup_tap_bridge.md)

## Comparison with Other Modes

| Feature | Virtual (Echo) | NAT | TAP (Bridge) |
|---------|---------------|-----|--------------|
| Host configuration | None | None | TAP driver + bridge setup |
| Guest IP address | N/A | Private (10.0.2.x) | LAN address (DHCP or static) |
| Internet access | No | Yes | Yes |
| Accessible from LAN | No | No | Yes |
| Protocol support | ICMP, TCP, UDP echo only | TCP, UDP, ICMP | All (L2 bridge) |
