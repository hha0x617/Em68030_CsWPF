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

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Em68030;

/// <summary>
/// Applies Windows 10/11 dark mode to WPF windows and native UI elements
/// (ContextMenu, ScrollBar, title bar, etc.) via DWM and uxtheme APIs.
///
/// Current implementation: always-dark. Future: support "Light", "System"
/// modes selectable via Settings dialog.
///
/// Two APIs are used:
/// <list type="bullet">
///   <item><c>SetPreferredAppMode(ForceDark)</c> — uxtheme ordinal #135,
///     tells the OS to render ALL native menus (including TextBox's built-in
///     Cut/Copy/Paste ContextMenu) in dark mode. Must be called before any
///     window is created.</item>
///   <item><c>DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)</c> —
///     sets the title bar and window border to dark chrome. Applied per-window
///     after the HWND is created.</item>
/// </list>
/// </summary>
public static class ThemeHelper
{
    // ================================================================
    // P/Invoke declarations
    // ================================================================

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    // Undocumented uxtheme ordinals used by Windows 10 1903+ / Windows 11.
    // Firefox, VS Code (Electron), and many other apps use these.
    [DllImport("uxtheme.dll", EntryPoint = "#135", PreserveSig = true)]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136", PreserveSig = true)]
    private static extern void FlushMenuThemes();

    // DWMWA_USE_IMMERSIVE_DARK_MODE: attribute 20 on Win10 20H1+ / Win11,
    // attribute 19 on Win10 1809–1903 (undocumented preview).
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    // SetPreferredAppMode values
    private const int APPMODE_FORCEDARK = 2;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// Set the application-wide preferred dark mode. Must be called in
    /// App.OnStartup BEFORE any window is created. This makes native
    /// Win32 menus (TextBox ContextMenu, etc.) render in dark mode.
    /// </summary>
    public static void SetAppDarkMode()
    {
        try
        {
            SetPreferredAppMode(APPMODE_FORCEDARK);
            FlushMenuThemes();
        }
        catch
        {
            // Graceful fallback: older Windows without uxtheme ordinals.
            // Native menus will render in the OS default (light) theme.
        }
    }

    /// <summary>
    /// Apply dark title bar and window border chrome to a single window.
    /// Must be called after the HWND is available (SourceInitialized or
    /// later). Safe to call from the Window.Loaded class handler.
    /// </summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int darkMode = 1;
            // Try the stable attribute (20) first; fall back to legacy (19).
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref darkMode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY,
                    ref darkMode, sizeof(int));
            }
        }
        catch
        {
            // Graceful fallback: title bar stays in OS default chrome.
        }
    }
}
