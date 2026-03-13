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

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Em68030.Core;
using Em68030.IO;

namespace Em68030.Views;

public partial class FramebufferWindow : Window
{
    private readonly Memory _memory;
    private readonly FramebufferDevice _device;
    private readonly WriteableBitmap _bitmap;
    private readonly DispatcherTimer _renderTimer;
    private readonly byte[] _pixelBuffer;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bpp;
    private readonly uint _vramOffset;
    private readonly int _vramBytes;

    public FramebufferWindow(Memory memory, FramebufferDevice device)
    {
        InitializeComponent();

        _memory = memory;
        _device = device;
        _width = device.Width;
        _height = device.Height;
        _bpp = device.Bpp;
        _vramOffset = device.VramBase;
        _vramBytes = device.Stride * _height;

        Title = $"Em68030 Framebuffer - {_width}x{_height}x{_bpp}bpp";

        // Create bitmap — always render to Bgra32 for WPF compatibility
        _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
        _pixelBuffer = new byte[_width * _height * 4]; // BGRA32
        DisplayImage.Source = _bitmap;

        // 30fps render timer
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _renderTimer.Tick += (_, _) => RenderFrame();
        _renderTimer.Start();
    }

    private void RenderFrame()
    {
        if (!_device.Enabled) return;

        var ram = _memory.FastRam;
        if (ram == null) return;

        uint vramEnd = _vramOffset + (uint)_vramBytes;
        if (vramEnd > _memory.FastRamSize) return;

        switch (_bpp)
        {
            case 16:
                RenderFrame16bpp(ram);
                break;
            case 8:
                RenderFrame8bpp(ram);
                break;
            case 32:
                RenderFrame32bpp(ram);
                break;
            default:
                return;
        }

        _bitmap.Lock();
        _bitmap.WritePixels(
            new Int32Rect(0, 0, _width, _height),
            _pixelBuffer, _width * 4, 0);
        _bitmap.Unlock();
    }

    /// <summary>16bpp r5g6b5 big-endian → BGRA32</summary>
    private void RenderFrame16bpp(byte[] ram)
    {
        int srcOffset = (int)_vramOffset;
        int dstOffset = 0;

        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                // Big-endian: high byte first
                int idx = srcOffset + (y * _width + x) * 2;
                ushort pixel = (ushort)((ram[idx] << 8) | ram[idx + 1]);

                // r5g6b5 → BGRA32
                int r = (pixel >> 11) & 0x1F;
                int g = (pixel >> 5) & 0x3F;
                int b = pixel & 0x1F;

                _pixelBuffer[dstOffset]     = (byte)((b << 3) | (b >> 2)); // B
                _pixelBuffer[dstOffset + 1] = (byte)((g << 2) | (g >> 4)); // G
                _pixelBuffer[dstOffset + 2] = (byte)((r << 3) | (r >> 2)); // R
                _pixelBuffer[dstOffset + 3] = 0xFF;                        // A
                dstOffset += 4;
            }
        }
    }

    /// <summary>8bpp palette index → BGRA32</summary>
    private void RenderFrame8bpp(byte[] ram)
    {
        int srcOffset = (int)_vramOffset;
        int dstOffset = 0;

        for (int i = 0; i < _width * _height; i++)
        {
            byte index = ram[srcOffset + i];
            var (r, g, b) = _device.GetPaletteEntry(index);

            _pixelBuffer[dstOffset]     = b; // B
            _pixelBuffer[dstOffset + 1] = g; // G
            _pixelBuffer[dstOffset + 2] = r; // R
            _pixelBuffer[dstOffset + 3] = 0xFF; // A
            dstOffset += 4;
        }
    }

    /// <summary>32bpp ARGB big-endian → BGRA32</summary>
    private void RenderFrame32bpp(byte[] ram)
    {
        int srcOffset = (int)_vramOffset;
        int dstOffset = 0;

        for (int i = 0; i < _width * _height; i++)
        {
            int idx = srcOffset + i * 4;
            // Big-endian ARGB: [A, R, G, B]
            byte a = ram[idx];
            byte r = ram[idx + 1];
            byte g = ram[idx + 2];
            byte b = ram[idx + 3];

            _pixelBuffer[dstOffset]     = b; // B
            _pixelBuffer[dstOffset + 1] = g; // G
            _pixelBuffer[dstOffset + 2] = r; // R
            _pixelBuffer[dstOffset + 3] = a; // A
            dstOffset += 4;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _renderTimer.Stop();
        base.OnClosed(e);
    }
}
