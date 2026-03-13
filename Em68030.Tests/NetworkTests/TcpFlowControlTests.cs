// Copyright 2026 hha0x617
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Em68030.Tests.NetworkTests;

using System.Net;
using System.Net.Sockets;
using Em68030.IO;
using Xunit;

/// <summary>
/// Tests for TCP flow control: the proxy must respect the guest's advertised
/// TCP window and not send more data than the guest can accept. Without this,
/// large downloads (e.g. curl) stall after ~140 KB because the guest drops
/// packets and the proxy has no retransmission mechanism.
/// </summary>
public class TcpFlowControlTests : IDisposable
{
    private static readonly byte[] GuestMac = { 0x08, 0x00, 0x3E, 0x21, 0x00, 0x00 };
    private static readonly byte[] GuestIp = { 10, 0, 2, 15 };
    private static readonly byte[] LoopbackIp = { 127, 0, 0, 1 };
    private const ushort GuestPort = 40000;

    private readonly SlirpNetworkHandler _handler;
    private readonly TcpListener _listener;
    private readonly int _serverPort;

    public TcpFlowControlTests()
    {
        _handler = new SlirpNetworkHandler();
        _handler.SetGuestMac(GuestMac);
        LearnGuestIp();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        _serverPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _handler.Reset();
        _handler.Dispose();
        _listener.Stop();
    }

    private void LearnGuestIp()
    {
        var arp = new byte[60];
        for (int i = 0; i < 6; i++) arp[i] = 0xFF;
        Array.Copy(GuestMac, 0, arp, 6, 6);
        arp[12] = 0x08; arp[13] = 0x06;
        arp[14] = 0x00; arp[15] = 0x01;
        arp[16] = 0x08; arp[17] = 0x00;
        arp[18] = 6; arp[19] = 4;
        arp[20] = 0x00; arp[21] = 0x01;
        Array.Copy(GuestMac, 0, arp, 22, 6);
        Array.Copy(GuestIp, 0, arp, 28, 4);
        Array.Copy(LoopbackIp, 0, arp, 38, 4);
        _handler.ProcessPacket(arp, 60);
        while (_handler.HasPendingPacket()) _handler.DequeuePacket();
    }

    private byte[] BuildGuestTcp(uint seq, uint ack, byte flags, ushort window)
    {
        var f = new byte[60];
        // Ethernet
        f[0] = 0x52; f[1] = 0x54; f[2] = 0x00;
        f[3] = 0x12; f[4] = 0x34; f[5] = 0x56;
        Array.Copy(GuestMac, 0, f, 6, 6);
        f[12] = 0x08; f[13] = 0x00;
        // IP (20 bytes)
        f[14] = 0x45;
        NetworkUtils.WriteBE16(f, 16, 40); // total len = 20 IP + 20 TCP
        f[20] = 0x40; // DF
        f[22] = 64; // TTL
        f[23] = 6;  // TCP
        Array.Copy(GuestIp, 0, f, 26, 4);
        Array.Copy(LoopbackIp, 0, f, 30, 4);
        ushort ipCsum = NetworkUtils.ComputeChecksum(f, 14, 20);
        NetworkUtils.WriteBE16(f, 24, ipCsum);
        // TCP (20 bytes at offset 34)
        NetworkUtils.WriteBE16(f, 34, GuestPort);
        NetworkUtils.WriteBE16(f, 36, (ushort)_serverPort);
        NetworkUtils.WriteBE32(f, 38, seq);
        NetworkUtils.WriteBE32(f, 42, ack);
        f[46] = 0x50; // data offset = 5
        f[47] = flags;
        NetworkUtils.WriteBE16(f, 48, window);
        ushort tcpCsum = NetworkUtils.ComputeTransportChecksum(
            GuestIp, LoopbackIp, 6, f, 34, 20);
        NetworkUtils.WriteBE16(f, 50, tcpCsum);
        return f;
    }

    private bool WaitForPacket(int timeoutMs = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (_handler.HasPendingPacket()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    // TCP 3-way handshake; returns proxy's seq after SYN and the accepted client
    private (uint proxySeq, TcpClient client) Handshake(ushort window)
    {
        // SYN
        var syn = BuildGuestTcp(1000, 0, 0x02, window);
        _handler.ProcessPacket(syn, 60);

        // Wait for SYN+ACK
        Assert.True(WaitForPacket(5000), "SYN+ACK not received");
        var synAck = _handler.DequeuePacket();
        uint proxySeq = NetworkUtils.ReadBE32(synAck, 38);

        // Accept the server-side connection
        var client = _listener.AcceptTcpClient();

        // ACK to complete handshake
        var ack = BuildGuestTcp(1001, proxySeq + 1, 0x10, window);
        _handler.ProcessPacket(ack, 60);

        Thread.Sleep(200);
        return (proxySeq + 1, client);
    }

    // Drain all data packets within timeout, return total payload bytes
    private int DrainDataBytes(int timeoutMs)
    {
        int totalBytes = 0;
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (_handler.HasPendingPacket())
            {
                var pkt = _handler.DequeuePacket();
                if (pkt.Length >= 54)
                {
                    ushort ipTotal = NetworkUtils.ReadBE16(pkt, 16);
                    int dataLen = ipTotal - 40; // 20 IP + 20 TCP header
                    if (dataLen > 0)
                        totalBytes += dataLen;
                }
            }
            else
            {
                Thread.Sleep(5);
            }
        }
        return totalBytes;
    }

    /// <summary>
    /// When the guest advertises a small TCP window (2920 = 2 MSS), the proxy
    /// must not send more than that many bytes without receiving an ACK first.
    /// Without flow control, 50 KB of server data would all be forwarded immediately.
    /// </summary>
    [Fact]
    public void SmallWindowLimitsDataSent()
    {
        var (proxySeq, client) = Handshake(2920);
        using (client)
        {
            var data = new byte[50000];
            Array.Fill(data, (byte)'X');
            client.GetStream().Write(data, 0, data.Length);

            int received = DrainDataBytes(3000);

            Assert.True(received <= 2920, $"Proxy sent {received} bytes but window is 2920");
            Assert.True(received > 0, "Proxy should send at least some data");
        }
    }

    /// <summary>
    /// After the window fills, an ACK from the guest should open the window
    /// and allow the proxy to resume sending data.
    /// </summary>
    [Fact]
    public void AckOpensWindowForMoreData()
    {
        var (proxySeq, client) = Handshake(1460);
        using (client)
        {
            var data = new byte[50000];
            Array.Fill(data, (byte)'Y');
            client.GetStream().Write(data, 0, data.Length);

            int first = DrainDataBytes(2000);
            Assert.True(first <= 1460, $"First batch {first} should be <= 1460");
            Assert.True(first > 0, "Should receive at least some data");

            // ACK the received data → opens window for more
            var ack = BuildGuestTcp(1001, proxySeq + (uint)first, 0x10, 1460);
            _handler.ProcessPacket(ack, 60);

            int second = DrainDataBytes(2000);
            Assert.True(second > 0, "Proxy should resume after ACK opens window");
        }
    }

    /// <summary>
    /// A zero window means the guest cannot accept any data. The proxy must
    /// not send any data packets until the guest opens the window.
    /// </summary>
    [Fact]
    public void ZeroWindowPreventsDataSending()
    {
        var (proxySeq, client) = Handshake(0);
        using (client)
        {
            var data = new byte[10000];
            Array.Fill(data, (byte)'Z');
            client.GetStream().Write(data, 0, data.Length);

            int received = DrainDataBytes(2000);
            Assert.Equal(0, received);
        }
    }
}
