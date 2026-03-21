# Internationalization (i18n)

Em68030 (C#/WPF) supports multiple UI languages via .NET resource files (`.resx`).
The application automatically selects the language based on the OS locale at startup.

## Supported Languages

| Language | Culture | Resource File |
|----------|---------|---------------|
| English (default) | `en` | `Properties/Strings.resx` |
| Japanese | `ja` | `Properties/Strings.ja.resx` |

## Architecture

- **`Properties/Strings.resx`** — Default (English) resource file. Also the source for the auto-generated `Strings.Designer.cs` class.
- **`Properties/Strings.ja.resx`** — Japanese translations. Keys must match the default file exactly.
- **`Properties/Strings.Designer.cs`** — Strongly-typed accessor class. Each property returns the localized string for the current culture.
- **XAML binding** — Static strings use `{x:Static p:Strings.KeyName}` with namespace `xmlns:p="clr-namespace:Em68030.Properties"`.
- **Code-behind** — Dynamic strings use `Strings.PropertyName` or `string.Format(Strings.FormatKey, args)`.

## Strings NOT Localized

The following are kept in their original form regardless of locale:

- Register names: D0–D7, A0–A7, PC, SR, SSP, VBR, FP0–FP7, CR, IAR
- Flag names: X, N, Z, V, C, S, T
- Board/OS identifiers: MVME147, Generic, NetBSD, Linux
- Technical terms in status bar: JIT, MIPS, MHz
- Diagnostic/trace messages (`[EMU] ...`) — developer-facing
- URLs, version numbers

## Adding a New String

1. **Add the key/value to `Properties/Strings.resx`** (English default).

2. **Add the same key with a translated value to each locale file** (e.g., `Strings.ja.resx`).

3. **Add a property to `Properties/Strings.Designer.cs`**:
   ```csharp
   public static string MyNewKey => ResourceManager.GetString("MyNewKey", resourceCulture) ?? "";
   ```

4. **Reference in XAML** (for static text):
   ```xml
   <TextBlock Text="{x:Static p:Strings.MyNewKey}" />
   ```
   Ensure the XAML file has: `xmlns:p="clr-namespace:Em68030.Properties"`

5. **Reference in code-behind** (for dynamic text):
   ```csharp
   using Em68030.Properties;
   // Simple string
   myTextBlock.Text = Strings.MyNewKey;
   // Format string with placeholders {0}, {1}, ...
   var text = string.Format(Strings.MyFormatKey, value1, value2);
   ```

6. **Build and verify** — The application should display the correct string for each locale.

## Adding a New Language

1. **Create a new resource file** named `Properties/Strings.{culture}.resx` where `{culture}` is the [BCP 47 language tag](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-lcid/a9eac961-e77d-41a6-90a5-ce1a8b0cdb9c):
   - `Strings.de.resx` for German
   - `Strings.zh-Hans.resx` for Simplified Chinese
   - `Strings.ko.resx` for Korean

2. **Copy all `<data>` entries from `Strings.resx`** and translate the `<value>` elements. All keys must match exactly.

3. **Build** — The .NET SDK automatically discovers and includes satellite assemblies for new `.resx` files. No `csproj` changes are needed.

4. **Verify** — Set your OS display language to the new locale (or use the forced locale method below) and launch the application.

## Forcing a Specific Locale (Debugging / Layout Testing)

To override the OS locale and force the application to display in a specific language, add the following code at the **very beginning of `App.xaml.cs`** (in the `App` constructor, before `InitializeComponent()`):

```csharp
using System.Globalization;

public App()
{
    // Force English (US) locale for debugging
    var culture = new CultureInfo("en-US");
    // var culture = new CultureInfo("ja-JP");  // Force Japanese

    Thread.CurrentThread.CurrentCulture = culture;
    Thread.CurrentThread.CurrentUICulture = culture;
    CultureInfo.DefaultThreadCurrentCulture = culture;
    CultureInfo.DefaultThreadCurrentUICulture = culture;

    // Also update the WPF resource culture
    Em68030.Properties.Strings.Culture = culture;

    InitializeComponent();
}
```

To force the invariant (C) locale:
```csharp
var culture = CultureInfo.InvariantCulture;
```

This displays the default (English) strings from `Strings.resx`, bypassing all satellite assemblies.

**Important:** Remove or comment out this code before committing. It is intended for debugging only.

### Alternative: Environment Variable

You can also force the locale without modifying code by setting environment variables before launching:

```powershell
$env:DOTNET_SYSTEM_GLOBALIZATION_UICULTURE = "en-US"
.\Em68030.exe
```

Or in `launchSettings.json` for Visual Studio:
```json
{
  "profiles": {
    "Em68030": {
      "environmentVariables": {
        "DOTNET_SYSTEM_GLOBALIZATION_UICULTURE": "en-US"
      }
    }
  }
}
```

## Resource Key Naming Convention

| Prefix | Usage | Example |
|--------|-------|---------|
| `Menu_` | Menu items | `Menu_File`, `Menu_OpenBinary` |
| `Toolbar_` | Toolbar buttons | `Toolbar_Run`, `Toolbar_Stop` |
| `Disasm_` | Disassembly pane | `Disasm_Title`, `Disasm_Go` |
| `Regs_` | Register pane | `Regs_Title`, `Regs_Edit` |
| `MemDump_` | Memory dump pane | `MemDump_Title`, `MemDump_Go` |
| `Context_` | Context menu items | `Context_Copy`, `Context_Paste` |
| `Status_` | Status bar text | `Status_Running`, `Status_MhzFormat` |
| `About_` | About dialog | `About_Title`, `About_VersionFormat` |
| `Window_` | Window titles | `Window_Console`, `Window_Breakpoints` |
| `Breakpoints_` | Breakpoints window | `Breakpoints_ClearAll` |
| `Console_` | Console window | `Console_Log`, `Console_Live` |
| `Dialog_` | Common dialog buttons | `Dialog_OK`, `Dialog_Cancel` |
| `FileDialog_` | File open dialogs | `FileDialog_OpenElf` |
| `Msg_` | Message boxes | `Msg_Error`, `Msg_ElfLoaded` |
| `Settings_` | Settings window | `Settings_BoardType`, `Settings_Network` |

Format strings use `{0}`, `{1}`, etc. as placeholders (standard `string.Format` syntax).
