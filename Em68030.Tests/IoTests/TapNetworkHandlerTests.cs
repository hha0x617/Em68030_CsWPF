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

using Em68030.IO;
using Xunit;

namespace Em68030.Tests.IoTests;

public class TapNetworkHandlerTests
{
    // ========================================================================
    // Construction with empty/invalid GUID (no TAP device)
    // ========================================================================

    [Fact]
    public void EmptyGuid_IsNotConnected()
    {
        using var handler = new TapNetworkHandler("");
        Assert.False(handler.IsConnected);
    }

    [Fact]
    public void InvalidGuid_IsNotConnected()
    {
        using var handler = new TapNetworkHandler("{00000000-0000-0000-0000-000000000000}");
        Assert.False(handler.IsConnected);
    }

    // ========================================================================
    // Safe operations when not connected
    // ========================================================================

    [Fact]
    public void ProcessPacket_WhenNotConnected_DoesNotCrash()
    {
        using var handler = new TapNetworkHandler("");
        byte[] frame = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                         0x08, 0x00, 0x3E, 0x21, 0x00, 0x00,
                         0x08, 0x06 };
        handler.ProcessPacket(frame, frame.Length);
        // No crash expected
    }

    [Fact]
    public void HasPendingPacket_WhenNotConnected_ReturnsFalse()
    {
        using var handler = new TapNetworkHandler("");
        Assert.False(handler.HasPendingPacket());
    }

    [Fact]
    public void DequeuePacket_WhenNotConnected_DoesNotCrash()
    {
        using var handler = new TapNetworkHandler("");
        var pkt = handler.DequeuePacket();
        // Returns null when queue is empty (ConcurrentQueue.TryDequeue)
    }

    [Fact]
    public void Reset_WhenNotConnected_DoesNotCrash()
    {
        using var handler = new TapNetworkHandler("");
        handler.Reset();
    }

    [Fact]
    public void SetGuestMac_WhenNotConnected_DoesNotCrash()
    {
        using var handler = new TapNetworkHandler("");
        byte[] mac = { 0x08, 0x00, 0x3E, 0x21, 0x00, 0x00 };
        handler.SetGuestMac(mac);
    }

    // ========================================================================
    // Adapter enumeration
    // ========================================================================

    [Fact]
    public void EnumerateAdapters_DoesNotCrash()
    {
        // Should return a list (possibly empty if TAP is not installed)
        var adapters = TapNetworkHandler.EnumerateAdapters();
        // Each adapter should have a non-empty GUID
        foreach (var adapter in adapters)
        {
            Assert.False(string.IsNullOrEmpty(adapter.Guid));
        }
    }

    [Fact]
    public void EnumerateAdapters_GuidFormat()
    {
        var adapters = TapNetworkHandler.EnumerateAdapters();
        foreach (var adapter in adapters)
        {
            // GUID should start with '{' and end with '}'
            if (!string.IsNullOrEmpty(adapter.Guid))
            {
                Assert.Equal('{', adapter.Guid[0]);
                Assert.Equal('}', adapter.Guid[^1]);
            }
        }
    }
}
