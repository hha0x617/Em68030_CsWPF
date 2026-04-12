# Theming Guide (C# WPF)

This document explains the Dark/Light/System theme switching mechanism
used in the C# WPF version and the rules developers must follow when
adding new windows, controls, or dialogs.

## Architecture Overview

```
App.xaml
  MergedDictionaries[0] = Themes/Dark.xaml  (or Light.xaml)
    -> Contains all named SolidColorBrush keys + SystemColors overrides

XAML files use:        {DynamicResource ThemeKeyName}
Code-behind uses:      Application.Current.FindResource("ThemeKeyName")

App.xaml.cs ApplyTheme(bool dark)
  -> Swaps MergedDictionaries[0] between Dark.xaml / Light.xaml
  -> All DynamicResource bindings update automatically

ThemeHelper.cs
  -> SetAppMode(dark):  SetPreferredAppMode for native menus/scrollbars
  -> ApplyTitleBar(w):  DwmSetWindowAttribute for window title bar chrome
```

## Brush Key Reference

| Category | Key | Dark | Light | Usage |
|----------|-----|------|-------|-------|
| Background | ThemeWindowBg | #1E1E1E | #F5F5F5 | Window/pane background |
| Background | ThemePanelBg | #2D2D30 | #E8E8E8 | Toolbar, menu bar |
| Background | ThemeControlBg | #3E3E42 | #D6D6D6 | Button background |
| Background | ThemeInputBg | #2D2D30 | #FFFFFF | TextBox background (editable) |
| Background | ThemeConsoleBg | #0C0C0C | #FFFFFF | Console terminal |
| Foreground | ThemeForeground | #D4D4D4 | #1E1E1E | Primary text |
| Foreground | ThemeBrightFg | #F0F0F0 | #000000 | Menu text |
| Foreground | ThemeDimFg | #808080 | #666666 | Subtle/secondary text |
| Foreground | ThemeDisabledFg | #909090 | #999999 | Disabled controls |
| Border | ThemeBorder | #3F3F46 | #CCCCCC | Control borders |
| Border | ThemeButtonBorder | #555555 | #AAAAAA | Button borders |
| Accent | ThemeAccent | #569CD6 | #0066CC | Section headers, links |
| Accent | ThemeAccentBanner | #0E639C | #0078D4 | Primary action button |
| Status | ThemeStatusBarBg | #007ACC | #007ACC | Status bar |
| Status | ThemeRunningFg | LightGreen | #008000 | MHz display |
| Status | ThemeWarningFg | #FFA500 | #CC6600 | Pending/warning text |
| Highlight | ThemeHighlightFg | #FFFFFF | #FFFFFF | Status bar text, caret |
| Highlight | ThemeHighlightedFg | #FFFF00 | #996600 | Current PC, modified |
| Highlight | ThemeCheckedBg | #264F78 | #B8D4F0 | Current PC background |

See `Themes/Dark.xaml` and `Themes/Light.xaml` for the complete list.

## Rules for New XAML Files

### 1. Window Background and Foreground

```xml
<Window ...
    Background="{DynamicResource ThemeWindowBg}"
    Foreground="{DynamicResource ThemeForeground}">
```

Never use hardcoded hex colors like `Background="#FF1E1E1E"`.

### 2. Controls in XAML

Always use `{DynamicResource KeyName}` (not `{StaticResource}`):

```xml
<TextBlock Foreground="{DynamicResource ThemeForeground}" />
<TextBox Background="{DynamicResource ThemeInputBg}"
         Foreground="{DynamicResource ThemeForeground}"
         BorderBrush="{DynamicResource ThemeBorder}"
         CaretBrush="{DynamicResource ThemeHighlightFg}" />
<Button Background="{DynamicResource ThemeControlBg}"
        Foreground="{DynamicResource ThemeForeground}"
        BorderBrush="{DynamicResource ThemeButtonBorder}" />
```

### 3. Controls Created in Code-Behind

Use `SetResourceReference` for properties that should track theme changes:

```csharp
var button = new Button { Content = "OK" };
button.SetResourceReference(Button.BackgroundProperty, "ThemeControlBg");
button.SetResourceReference(Button.ForegroundProperty, "ThemeForeground");
```

Or use `Application.Current.FindResource` for one-time resolution:

```csharp
var brush = (Brush)Application.Current.FindResource("ThemeForeground");
textBlock.Foreground = brush;
```

### 4. DataTrigger / Style.Triggers

Trigger values must also use `DynamicResource`:

```xml
<DataTrigger Binding="{Binding IsActive}" Value="True">
    <Setter Property="Background" Value="{DynamicResource ThemeCheckedBg}" />
</DataTrigger>
```

### 5. SystemColors in ListBox/ListView

For selection highlight colors inside `Style.Resources`:

```xml
<Style.Resources>
    <SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}"
        Color="{Binding Color, Source={StaticResource ThemeCheckedBg}}" />
</Style.Resources>
```

### 6. ContextMenu and Menu

ContextMenu and Menu styles are defined at `Application.Resources` level
in `App.xaml`. They already use `{DynamicResource}` and will follow
theme changes automatically. No special handling is needed.

### 7. Common File Dialogs (Open/Save)

Win32 common file dialogs (OpenFileDialog, SaveFileDialog) follow the
**Windows system theme setting**, not the application theme. This is a
Windows platform limitation shared by VS Code, Notepad++, etc.

No workaround is available. The file dialog content area always matches
the OS dark/light mode setting.

## Adding a New Brush Key

1. Add the key to **both** `Themes/Dark.xaml` and `Themes/Light.xaml`
2. Choose colors that provide adequate contrast in both themes
3. Use a semantic name (e.g., `ThemeWarningFg`, not `ThemeOrangeText`)

## Theme Switching Flow

```
User selects theme in Settings -> OK
  -> SettingsWindow.OK_Click
    -> App.ApplyTheme(dark)          // swaps MergedDictionaries
    -> ThemeHelper.SetAppMode(dark)  // native menus/scrollbars
    -> ThemeHelper.ApplyTitleBar()   // each open window's title bar
```

The theme takes effect immediately for all open windows because
`DynamicResource` bindings automatically re-evaluate when the
merged dictionary is swapped.
