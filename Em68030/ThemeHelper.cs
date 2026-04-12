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
using Microsoft.Win32;

namespace Em68030;

/// <summary>
/// Applies Windows 10/11 dark/light mode to WPF windows and native UI elements
/// (ContextMenu, ScrollBar, title bar, etc.) via DWM and uxtheme APIs.
///
/// Two APIs are used:
/// <list type="bullet">
///   <item><c>SetPreferredAppMode</c> — uxtheme ordinal #135,
///     tells the OS to render ALL native menus (including TextBox's built-in
///     Cut/Copy/Paste ContextMenu) in the matching mode. Must be called before any
///     window is created.</item>
///   <item><c>DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)</c> —
///     sets the title bar and window border chrome. Applied per-window
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

    [DllImport("uxtheme.dll", EntryPoint = "#135", PreserveSig = true)]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136", PreserveSig = true)]
    private static extern void FlushMenuThemes();

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    // SetPreferredAppMode values
    private const int APPMODE_DEFAULT = 0;
    private const int APPMODE_ALLOWDARK = 1;
    private const int APPMODE_FORCEDARK = 2;
    private const int APPMODE_FORCELIGHT = 3;

    /// <summary>Current effective dark mode state.</summary>
    public static bool IsDarkMode { get; set; } = true;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// Detect whether the OS is in dark mode via the registry.
    /// Returns true if dark mode, false if light.
    /// </summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
                return val == 0; // 0 = dark, 1 = light
        }
        catch { }
        return true; // default to dark if unreadable
    }

    /// <summary>
    /// Resolve theme string ("Dark", "Light", "System") to effective dark mode flag.
    /// </summary>
    public static bool ResolveDarkMode(string theme)
    {
        return theme switch
        {
            "Light" => false,
            "System" => IsSystemDarkMode(),
            _ => true, // "Dark" or unknown
        };
    }

    /// <summary>
    /// Set the application-wide preferred app mode for native controls.
    /// Must be called BEFORE any window is created.
    /// </summary>
    public static void SetAppMode(bool dark)
    {
        IsDarkMode = dark;
        try
        {
            SetPreferredAppMode(dark ? APPMODE_FORCEDARK : APPMODE_FORCELIGHT);
            FlushMenuThemes();
        }
        catch
        {
            // Graceful fallback: older Windows without uxtheme ordinals.
        }
    }

    /// <summary>
    /// Apply dark/light title bar and window border chrome to a single window.
    /// </summary>
    public static void ApplyTitleBar(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int mode = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref mode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY,
                    ref mode, sizeof(int));
            }
        }
        catch
        {
            // Graceful fallback
        }
    }

    // Keep backward compat
    public static void SetAppDarkMode() => SetAppMode(true);
    public static void ApplyDarkTitleBar(Window window) => ApplyTitleBar(window, IsDarkMode);
}
