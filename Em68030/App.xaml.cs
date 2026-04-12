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

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Em68030.Config;

namespace Em68030;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Load config to determine theme before creating any window.
        var config = EmulatorConfig.Load();
        bool dark = ThemeHelper.ResolveDarkMode(config.Theme);

        // Enable OS-level dark/light mode for native UI elements (ContextMenu,
        // ScrollBar, title bar chrome). Must be called before any Window
        // is created so the theme applies to all popups and child windows.
        ThemeHelper.SetAppMode(dark);

        // Apply the WPF theme dictionary.
        ApplyTheme(dark);

        // Apply matching title bar to every Window when it loads.
        EventManager.RegisterClassHandler(typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is Window w) ThemeHelper.ApplyTitleBar(w, ThemeHelper.IsDarkMode);
            }));

        // Replace the default TextBox ContextMenu (which bypasses WPF's
        // implicit style system) with an explicit WPF ContextMenu that
        // picks up our dark MenuItem/ContextMenu styles from App.xaml.
        EventManager.RegisterClassHandler(typeof(TextBox),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is TextBox tb &&
                    tb.ReadLocalValue(FrameworkElement.ContextMenuProperty) == DependencyProperty.UnsetValue)
                {
                    tb.ContextMenu = new ContextMenu();
                    tb.ContextMenu.Items.Add(new MenuItem { Command = ApplicationCommands.Undo });
                    tb.ContextMenu.Items.Add(new Separator());
                    tb.ContextMenu.Items.Add(new MenuItem { Command = ApplicationCommands.Cut });
                    tb.ContextMenu.Items.Add(new MenuItem { Command = ApplicationCommands.Copy });
                    tb.ContextMenu.Items.Add(new MenuItem { Command = ApplicationCommands.Paste });
                    tb.ContextMenu.Items.Add(new MenuItem { Command = ApplicationCommands.Delete });
                    tb.ContextMenu.Items.Add(new Separator());
                    tb.ContextMenu.Items.Add(new MenuItem { Command = ApplicationCommands.SelectAll });
                }
            }));

        // --lang=xx-XX command line argument to override UI language
        foreach (var arg in e.Args)
        {
            if (arg.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
            {
                var lang = arg["--lang=".Length..];
                if (!string.IsNullOrEmpty(lang))
                {
                    try
                    {
                        var culture = new CultureInfo(lang);
                        Thread.CurrentThread.CurrentUICulture = culture;
                        Thread.CurrentThread.CurrentCulture = culture;
                        CultureInfo.DefaultThreadCurrentUICulture = culture;
                    }
                    catch (CultureNotFoundException) { /* invalid language code, ignore */ }
                }
                break;
            }
        }
        base.OnStartup(e);
    }

    /// <summary>
    /// Switch the application theme by replacing the first MergedDictionary
    /// (which is the theme dictionary) and updating native UI mode.
    /// </summary>
    public static void ApplyTheme(bool dark)
    {
        var merged = Current.Resources.MergedDictionaries;
        if (merged.Count == 0) return;

        string source = dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        merged[0] = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        ThemeHelper.IsDarkMode = dark;
    }
}
