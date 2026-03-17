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

namespace Em68030;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
}

