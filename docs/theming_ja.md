# テーマガイド (C# WPF)

C# WPF 版で採用しているダーク/ライト/システムテーマ切替の仕組みと、
ウィンドウやコントロールを追加する際に準拠すべき規約を解説します。

## アーキテクチャ概要

```
App.xaml
  MergedDictionaries[0] = Themes/Dark.xaml (または Light.xaml)
    -> すべてのブラシキー + SystemColors オーバーライドを定義

XAML:          {DynamicResource ThemeKeyName}
コード behind:  Application.Current.FindResource("ThemeKeyName")

App.xaml.cs ApplyTheme(bool dark)
  -> MergedDictionaries[0] を Dark.xaml / Light.xaml で差し替え
  -> DynamicResource バインディングが自動的に更新される

ThemeHelper.cs
  -> SetAppMode(dark):  ネイティブメニュー/スクロールバーのテーマ設定
  -> ApplyTitleBar(w):  DWM API でタイトルバーのダーク/ライト切替
```

## ブラシキー一覧

| 分類 | キー名 | ダーク | ライト | 用途 |
|------|--------|--------|--------|------|
| 背景 | ThemeWindowBg | #1E1E1E | #F5F5F5 | ウィンドウ/ペイン背景 |
| 背景 | ThemePanelBg | #2D2D30 | #E8E8E8 | ツールバー、メニューバー |
| 背景 | ThemeControlBg | #3E3E42 | #D6D6D6 | ボタン背景 |
| 背景 | ThemeInputBg | #2D2D30 | #FFFFFF | TextBox 背景 (編集可能時) |
| 背景 | ThemeConsoleBg | #0C0C0C | #FFFFFF | コンソール端末 |
| 前景 | ThemeForeground | #D4D4D4 | #1E1E1E | 通常テキスト |
| 前景 | ThemeBrightFg | #F0F0F0 | #000000 | メニューテキスト |
| 前景 | ThemeDimFg | #808080 | #666666 | 補助テキスト |
| 前景 | ThemeDisabledFg | #909090 | #999999 | 無効化コントロール |
| 枠線 | ThemeBorder | #3F3F46 | #CCCCCC | コントロール枠線 |
| 枠線 | ThemeButtonBorder | #555555 | #AAAAAA | ボタン枠線 |
| アクセント | ThemeAccent | #569CD6 | #0066CC | セクションヘッダー |
| アクセント | ThemeAccentBanner | #0E639C | #0078D4 | 主要アクションボタン |
| ステータス | ThemeStatusBarBg | #007ACC | #007ACC | ステータスバー |
| ステータス | ThemeRunningFg | LightGreen | #008000 | MHz 表示 |
| ステータス | ThemeWarningFg | #FFA500 | #CC6600 | 保留中/警告テキスト |
| ハイライト | ThemeHighlightFg | #FFFFFF | #FFFFFF | ステータスバー文字 |
| ハイライト | ThemeHighlightedFg | #FFFF00 | #996600 | 現在 PC、変更済み |
| ハイライト | ThemeCheckedBg | #264F78 | #B8D4F0 | 現在 PC 行の背景 |

全キーの一覧は `Themes/Dark.xaml` と `Themes/Light.xaml` を参照してください。

## XAML ファイル追加時の規約

### 1. ウィンドウの Background / Foreground

```xml
<Window ...
    Background="{DynamicResource ThemeWindowBg}"
    Foreground="{DynamicResource ThemeForeground}">
```

`Background="#FF1E1E1E"` のようなハードコード色は禁止です。

### 2. XAML 内のコントロール

必ず `{DynamicResource}` を使用します (`{StaticResource}` は不可):

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

### 3. コード behind で動的生成するコントロール

`SetResourceReference` を使うとテーマ切替に自動追従します:

```csharp
var button = new Button { Content = "OK" };
button.SetResourceReference(Button.BackgroundProperty, "ThemeControlBg");
button.SetResourceReference(Button.ForegroundProperty, "ThemeForeground");
```

一度だけ解決する場合は `FindResource` を使用:

```csharp
var brush = (Brush)Application.Current.FindResource("ThemeForeground");
textBlock.Foreground = brush;
```

### 4. DataTrigger / Style.Triggers

トリガー内の値にも `DynamicResource` を使用:

```xml
<DataTrigger Binding="{Binding IsActive}" Value="True">
    <Setter Property="Background" Value="{DynamicResource ThemeCheckedBg}" />
</DataTrigger>
```

### 5. ListBox/ListView の選択色

`Style.Resources` 内で SystemColors をオーバーライド:

```xml
<Style.Resources>
    <SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}"
        Color="{Binding Color, Source={StaticResource ThemeCheckedBg}}" />
</Style.Resources>
```

### 6. ContextMenu / Menu

App.xaml の `Application.Resources` で定義済みで、すべて
`{DynamicResource}` を使用しています。新しいウィンドウに
ContextMenu を追加する際、特別な対応は不要です。

### 7. コモンファイルダイアログ (Open/Save)

Win32 コモンファイルダイアログ (OpenFileDialog, SaveFileDialog) は
**Windows のシステムテーマ設定**に従います。アプリ側のテーマ設定は
反映されません。これは VS Code や Notepad++ と同じ Windows の制限です。

## 新しいブラシキーの追加方法

1. `Themes/Dark.xaml` と `Themes/Light.xaml` の**両方**にキーを追加
2. 両テーマで十分なコントラストが確保される色を選択
3. セマンティックな名前を使用 (例: `ThemeWarningFg`、`ThemeOrangeText` は不可)

## テーマ切替フロー

```
ユーザーが設定ダイアログでテーマを選択 -> OK
  -> SettingsWindow.OK_Click
    -> App.ApplyTheme(dark)          // MergedDictionaries を差し替え
    -> ThemeHelper.SetAppMode(dark)  // ネイティブ UI のテーマ設定
    -> ThemeHelper.ApplyTitleBar()   // 各ウィンドウのタイトルバー更新
```

`DynamicResource` バインディングは辞書差し替え時に自動再評価
されるため、テーマはすべてのウィンドウに即座に反映されます。
