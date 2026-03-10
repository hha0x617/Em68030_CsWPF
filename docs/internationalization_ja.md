# 国際化 (i18n)

Em68030 (C#/WPF) は .NET リソースファイル (`.resx`) を使用した多言語 UI に対応しています。
アプリケーション起動時に OS のロケール設定に基づいて自動的に言語が選択されます。

## 対応言語

| 言語 | カルチャ | リソースファイル |
|------|---------|----------------|
| 英語 (デフォルト) | `en` | `Properties/Strings.resx` |
| 日本語 | `ja` | `Properties/Strings.ja.resx` |

## アーキテクチャ

- **`Properties/Strings.resx`** — デフォルト (英語) リソースファイル。自動生成される `Strings.Designer.cs` クラスのソース。
- **`Properties/Strings.ja.resx`** — 日本語翻訳。キーはデフォルトファイルと完全に一致する必要があります。
- **`Properties/Strings.Designer.cs`** — 強い型付けのアクセサクラス。各プロパティは現在のカルチャに応じたローカライズ済み文字列を返します。
- **XAML バインディング** — 静的文字列は名前空間 `xmlns:p="clr-namespace:Em68030.Properties"` と `{x:Static p:Strings.KeyName}` を使用。
- **コードビハインド** — 動的文字列は `Strings.PropertyName` または `string.Format(Strings.FormatKey, args)` を使用。

## ローカライズしない文字列

ロケールに関係なく原文のまま表示される文字列:

- レジスタ名: D0–D7, A0–A7, PC, SR, SSP, VBR, FP0–FP7, CR, IAR
- フラグ名: X, N, Z, V, C, S, T
- ボード/OS 識別子: MVME147, Generic, NetBSD, Linux
- ステータスバーの技術用語: JIT, MIPS, MHz
- 診断/トレースメッセージ (`[EMU] ...`) — 開発者向け
- URL、バージョン番号

## 新しい文字列の追加手順

1. **`Properties/Strings.resx`** (英語デフォルト) にキーと値を追加する。

2. **各ロケールファイル** (例: `Strings.ja.resx`) に同じキーで翻訳済みの値を追加する。

3. **`Properties/Strings.Designer.cs`** にプロパティを追加する:
   ```csharp
   public static string MyNewKey => ResourceManager.GetString("MyNewKey", resourceCulture) ?? "";
   ```

4. **XAML で参照する** (静的テキストの場合):
   ```xml
   <TextBlock Text="{x:Static p:Strings.MyNewKey}" />
   ```
   XAML ファイルに `xmlns:p="clr-namespace:Em68030.Properties"` が宣言されていることを確認。

5. **コードビハインドで参照する** (動的テキストの場合):
   ```csharp
   using Em68030.Properties;
   // 単純な文字列
   myTextBlock.Text = Strings.MyNewKey;
   // プレースホルダー {0}, {1}, ... を含むフォーマット文字列
   var text = string.Format(Strings.MyFormatKey, value1, value2);
   ```

6. **ビルドして検証** — 各ロケールで正しい文字列が表示されることを確認する。

## 新しい言語の追加方法

1. **新しいリソースファイルを作成する。** ファイル名は `Properties/Strings.{culture}.resx` とする ([BCP 47 言語タグ](https://learn.microsoft.com/ja-jp/openspecs/windows_protocols/ms-lcid/a9eac961-e77d-41a6-90a5-ce1a8b0cdb9c)):
   - `Strings.de.resx` — ドイツ語
   - `Strings.zh-Hans.resx` — 簡体字中国語
   - `Strings.ko.resx` — 韓国語

2. **`Strings.resx` からすべての `<data>` エントリをコピーし、** `<value>` 要素を翻訳する。すべてのキーが完全に一致する必要がある。

3. **ビルドする。** .NET SDK が新しい `.resx` ファイルを自動的に検出し、サテライトアセンブリを生成する。`csproj` の変更は不要。

4. **検証する。** OS の表示言語を新しいロケールに設定する (または下記の強制ロケール方法を使用) してアプリケーションを起動する。

## ロケールの強制指定 (デバッグ / レイアウト確認)

OS のロケールを上書きして特定の言語で表示するには、**`App.xaml.cs`** の先頭 (`App` コンストラクタ内、`InitializeComponent()` の前) に以下のコードを追加します:

```csharp
using System.Globalization;

public App()
{
    // 英語 (US) を強制
    var culture = new CultureInfo("en-US");
    // var culture = new CultureInfo("ja-JP");  // 日本語を強制

    Thread.CurrentThread.CurrentCulture = culture;
    Thread.CurrentThread.CurrentUICulture = culture;
    CultureInfo.DefaultThreadCurrentCulture = culture;
    CultureInfo.DefaultThreadCurrentUICulture = culture;

    // WPF リソースのカルチャも更新
    Em68030.Properties.Strings.Culture = culture;

    InitializeComponent();
}
```

インバリアント (C) ロケールを強制するには:
```csharp
var culture = CultureInfo.InvariantCulture;
```

これにより `Strings.resx` のデフォルト (英語) 文字列が表示され、すべてのサテライトアセンブリがバイパスされます。

**重要:** コミット前にこのコードを削除またはコメントアウトしてください。デバッグ専用です。

### 代替方法: 環境変数

コードを変更せずにロケールを強制する方法:

```powershell
$env:DOTNET_SYSTEM_GLOBALIZATION_UICULTURE = "en-US"
.\Em68030.exe
```

Visual Studio の `launchSettings.json` で設定する場合:
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

## リソースキーの命名規則

| プレフィックス | 用途 | 例 |
|--------------|------|-----|
| `Menu_` | メニュー項目 | `Menu_File`, `Menu_OpenBinary` |
| `Toolbar_` | ツールバー ボタン | `Toolbar_Run`, `Toolbar_Stop` |
| `Disasm_` | 逆アセンブリ ペイン | `Disasm_Title`, `Disasm_Go` |
| `Regs_` | レジスタ ペイン | `Regs_Title`, `Regs_Edit` |
| `MemDump_` | メモリダンプ ペイン | `MemDump_Title`, `MemDump_Go` |
| `Context_` | コンテキスト メニュー | `Context_Copy`, `Context_Paste` |
| `Status_` | ステータスバー テキスト | `Status_Running`, `Status_MhzFormat` |
| `About_` | バージョン情報ダイアログ | `About_Title`, `About_Version` |
| `Window_` | ウィンドウ タイトル | `Window_Console`, `Window_Breakpoints` |
| `Breakpoints_` | ブレークポイント ウィンドウ | `Breakpoints_ClearAll` |
| `Console_` | コンソール ウィンドウ | `Console_Log`, `Console_Live` |
| `Dialog_` | 共通ダイアログ ボタン | `Dialog_OK`, `Dialog_Cancel` |
| `FileDialog_` | ファイルを開くダイアログ | `FileDialog_OpenElf` |
| `Msg_` | メッセージ ボックス | `Msg_Error`, `Msg_ElfLoaded` |
| `Settings_` | 設定ウィンドウ | `Settings_BoardType`, `Settings_Network` |

フォーマット文字列はプレースホルダーとして `{0}`, `{1}` 等を使用します (標準の `string.Format` 構文)。
