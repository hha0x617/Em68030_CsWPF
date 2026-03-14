# フレームバッファディスプレイ セットアップガイド

本ガイドでは、Em68030 エミュレータの Linux ゲスト上でフレームバッファディスプレイ、
フレームバッファコンソール (fbcon)、および X Window System を設定する方法を説明します。

## 前提条件

- エミュレータのフレームバッファが設定 → Framebuffer で有効化されていること
- `em68030fb` カーネルモジュールがインストール・ロード済みであること（[em68030-guest-linux](https://github.com/hha0x617/em68030-guest-linux) を参照）
- `em68030input` カーネルモジュールがインストール・ロード済みであること（キーボード/マウス入力用）

## パート 1: フレームバッファデバイス (`/dev/fb0`)

### カーネル設定

以下のオプションがカーネル設定で有効になっていることを確認してください:

- `CONFIG_FB=y`
- `CONFIG_FB_SIMPLE=y`（または `=m`）
- `CONFIG_TRIM_UNUSED_KSYMS` — **無効** にすること（外部モジュールのロードに必要）

### モジュールのロード

```bash
insmod /path/to/em68030fb.ko
```

確認:

```bash
ls -la /dev/fb0
```

### 起動時の自動ロード

```bash
mkdir -p /lib/modules/$(uname -r)/extra
cp em68030fb.ko /lib/modules/$(uname -r)/extra/
depmod -a

# systemd:
echo em68030fb > /etc/modules-load.d/em68030fb.conf
```

## パート 2: フレームバッファコンソール (fbcon)

fbcon はフレームバッファ上にテキストコンソールを表示し、シリアル専用コンソールの代わりに
エミュレータのフレームバッファウィンドウにグラフィカルなテキスト表示を提供します。

### カーネル設定

以下のオプションを有効にしてください:

- `CONFIG_FRAMEBUFFER_CONSOLE=y`
- `CONFIG_INPUT=y`
- `CONFIG_INPUT_EVDEV=y`（または `=m`）

オプション（フォント選択用）:
- `CONFIG_FONTS=y`
- `CONFIG_FONT_8x16=y`

### カーネルコマンドライン

エミュレータの設定でカーネルコマンドラインに `console=tty0` を追加します:

```
root=/dev/sda1 console=tty0 console=ttyS0 earlyprintk
```

> **注意:** 最後の `console=` 引数が `/dev/console`（プライマリコンソール）になります。
> `console=ttyS0` を最後にすると、systemd の出力はシリアルコンソールウィンドウに表示されます。
> `console=tty0` を最後にすると、systemd の出力はフレームバッファに表示されます。
> カーネルメッセージ (`printk`) は順序に関係なく、登録された**すべて**のコンソールに出力されます。

### 入力デバイス

fbcon でキーボード入力を使用するには `em68030input` モジュールをロードする必要があります:

```bash
insmod /path/to/em68030input.ko
echo em68030input > /etc/modules-load.d/em68030input.conf
```

### コンソールサイズ

コンソールサイズはフレームバッファの解像度とフォントから自動的に計算されます:

| 解像度 | フォント | 桁数 × 行数 |
|--------|----------|-------------|
| 640×480 | 8×16 | 80×30 |
| 800×600 | 8×16 | 100×37 |
| 1024×768 | 8×16 | 128×48 |

## パート 3: X Window System

X Window System はフレームバッファ上にグラフィカルデスクトップ環境を提供します。

### X.org のインストール

```bash
apt install xorg
```

これにより X サーバ、`xf86-video-fbdev`（フレームバッファビデオドライバ）、
`xf86-input-libinput`（入力ドライバ）がインストールされます。em68030input モジュールの
絶対座標モードは libinput と互換性があります。

### ウィンドウマネージャのインストール

エミュレートされた m68k システムには軽量なウィンドウマネージャを推奨します:

```bash
apt install twm
```

`~/.xinitrc` を作成します:

```bash
cat > ~/.xinitrc << 'EOF'
twm &
xterm
EOF
```

その他の軽量な選択肢: `fvwm`、`icewm`、`openbox`（m68k で利用可能な場合）。

### X の起動

```bash
startx
```

X サーバはディスプレイに `/dev/fb0` を、キーボード/マウスに `/dev/input/event*` を使用します。

### マウス入力

エミュレータは絶対座標のポインティングデバイスを提供します。フレームバッファウィンドウ内の
マウス位置がゲスト画面の座標に直接マッピングされます。ポインタのグラブやキャプチャは不要です。

### キーボード入力

フレームバッファウィンドウがフォーカスされているとき、キーボードイベントがキャプチャされます。
すべての標準キーがサポートされています（US キーボードレイアウト）。特殊なショートカット:

- **Ctrl+Shift+V** — クリップボードのテキストをキーイベントとして貼り付け

### パフォーマンスに関する注意事項

エミュレートされた MC68030（約 44 MIPS）上の X Window System は動作しますが低速です。
複雑な UI 要素のレンダリングでは大きな遅延が発生します。軽量なウィンドウマネージャと
シンプルなアプリケーションが最適です。

## トラブルシューティング

### fbcon に出力が表示されない

- カーネル設定で `CONFIG_FRAMEBUFFER_CONSOLE=y` を確認:
  `zcat /proc/config.gz | grep FRAMEBUFFER_CONSOLE`
- カーネルコマンドラインに `console=tty0` があることを確認:
  `cat /proc/cmdline`
- `/dev/fb0` が存在することを確認: `ls -la /dev/fb0`

### fbcon でキーボード入力が動作しない

- `em68030input` モジュールがロードされていることを確認: `lsmod | grep em68030input`
- 入力デバイスを確認: `cat /proc/bus/input/devices`
- エミュレータのフレームバッファウィンドウにフォーカスがあることを確認

### X サーバが起動しない

- ログを確認: `cat /var/log/Xorg.0.log | grep EE`
- `xf86-video-fbdev` がインストールされていることを確認: `dpkg -l | grep xf86-video-fbdev`
- `/dev/fb0` が存在することを確認

### X でマウスカーソルが表示されない

- `em68030input` モジュールがロードされていることを確認
- X ログで入力デバイスの認識を確認:
  `grep -i "mouse\|keyboard" /var/log/Xorg.0.log`

### systemd の出力がフレームバッファにのみ表示され、シリアルコンソールに表示されない

カーネルコマンドラインの最後の `console=` 引数がプライマリコンソールになります。
systemd の出力をシリアルコンソールに向けるには `console=ttyS0` を最後に配置してください:
```
console=tty0 console=ttyS0
```
