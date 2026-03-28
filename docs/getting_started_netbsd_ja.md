# はじめに: Em68030 で NetBSD をインストール・起動する

本ガイドでは、Em68030 エミュレータを使用して NetBSD/mvme68k を仮想 SCSI ハードディスクにインストールし、インストール済みディスクから NetBSD を起動するまでの手順を説明します。

## 概要

インストール手順は実機の MVME147 と同じ 3 つのフェーズで構成されます:

1. **RAMDISK カーネルの起動** — ディスクのパーティション作成とミニルートファイルシステムの書き込み
2. **ミニルートの起動** — sysinst インストーラで NetBSD を CD-ROM またはネットワークからインストール
3. **インストール済みシステムの起動** — インストール済みディスクから NetBSD を起動

## 前提条件

- Em68030 がビルド済みで実行可能な状態 (ビルド手順は [README](../README_ja.md) を参照)
- NetBSD/mvme68k ファイルをダウンロードするためのインターネット接続
- 約 600 MB の空きディスク容量 (ディスクイメージ + ダウンロードファイル用)

## NetBSD/mvme68k ファイルのダウンロード

NetBSD CDN から以下のファイルをダウンロードします。NetBSD 10.1 の場合:

| ファイル | URL | 説明 |
|---|---|---|
| `netbsd-RAMDISK.gz` | `.../installation/tapeimage/netbsd-RAMDISK.gz` | ディスクセットアップ用 RAMDISK カーネル |
| `netbsd-GENERIC.gz` | `.../binary/kernel/netbsd-GENERIC.gz` | 通常運用用の標準カーネル |
| CD-ROM ISO | `.../images/NetBSD-10.1-mvme68k.iso` | インストールセットおよびミニルート |

ベース URL: `https://cdn.netbsd.org/pub/NetBSD/NetBSD-10.1/mvme68k/`

ダウンロード後、`.gz` ファイルを展開します:

```bash
gzip -d netbsd-RAMDISK.gz
gzip -d netbsd-GENERIC.gz
```

> **注意**: Windows では 7-Zip などのツールで `.gz` ファイルを展開できます。

## 仮想 SCSI ディスクイメージの作成

### 方法 A: エミュレータ GUI を使用

1. Em68030 を起動します
2. メニューバーから **Settings** を開きます
3. **Board Type** を `MVME147` に設定します
4. **SCSI Disks** セクションの **New Image Size (MB)** フィールドにサイズを入力します (例: `500` で 500 MB)
5. **Create** をクリックし、ディスクイメージファイルの保存先を選択します (例: `netbsd.img`)
6. 作成したディスクイメージを SCSI Disks リストに **SCSI ID 0** で追加します
7. **Memory Size** を 64 MB (67108864 バイト) に設定します (推奨)
8. **OK** をクリックして保存します

エミュレータは有効な NetBSD `cpu_disklabel` が書き込まれた空のディスクイメージを作成します。パーティション `a` (ルートファイルシステム) と `b` (スワップ、インストール時のミニルート用にも使用) が定義されています。

### 方法 B: コマンドラインスクリプトを使用

`tools/create-netbsd-disk.sh` スクリプトでコマンドラインからディスクイメージを作成できます。miniroot イメージを指定すると sd0b に配置されます:

```bash
./tools/create-netbsd-disk.sh -s 2G -m miniroot.fs -o netbsd.img
```

全オプションと Windows (PowerShell) での使用方法は [Disk Image and Utility Tools](tools.md#create-netbsd-disk-image) を参照してください。

---

## フェーズ 1: RAMDISK 起動 — ミニルートをパーティション b に書き込み

RAMDISK カーネルはメモリ上にミニマルなルートファイルシステムを内蔵しており、ディスクセットアップ用の基本ユーティリティを含んでいます。ルートは `md0` (メモリディスク) 上にあるため、SCSI ディスクへの書き込みが可能です。

### 1.1 CD-ROM の設定

RAMDISK カーネルを起動する前に CD-ROM ISO を設定します:

1. **Settings** を開きます
2. **SCSI CD-ROM** に `NetBSD-10.1-mvme68k.iso` のパスを設定します
3. **OK** をクリックします

### 1.2 RAMDISK カーネルの読み込み

1. メニューバーから **File > Open ELF...** を選択します
2. `netbsd-RAMDISK` ファイルを選択します
3. **F5** を押して実行を開始します
4. **View > Console Window** からコンソールウィンドウを開きます

RAMDISK カーネルが起動し、シェルプロンプトが表示されます。

### 1.3 ディスクラベルの確認

エミュレータの "Create" 機能はパーティション `a` と `b` を持つディスクラベルを事前に書き込んでいます。確認するには:

```
# /sbin/disklabel sd0
```

パーティション `a` (4.2BSD) と `b` (swap) が表示されるはずです。

### 1.4 ミニルートをパーティション b に書き込み

CD-ROM をマウントし、ミニルートをパーティション `b` に書き込みます:

```
# /sbin/mount -t cd9660 /dev/cd0a /mnt2
# /usr/bin/gunzip < /mnt2/mvme68k/installation/miniroot/miniroot.fs.gz | /bin/dd of=/dev/rsd0b obs=8k
# /sbin/umount /mnt2
```

> **注意**: ディスク全体ではなく `/dev/rsd0b` (パーティション b) に書き込みます。これによりセクタ 0 のディスクラベルが保持され、パーティション `a` がインストーラで使用可能になります。

### 1.5 停止

```
# /sbin/halt
```

または **Shift+F5** でエミュレータを停止します。

---

## フェーズ 2: ミニルート起動 — sysinst で NetBSD をインストール

ミニルートには `sysinst` インストーラが含まれており、ディスクのパーティション作成、ファイルシステム構築、インストールセットの展開を行います。

### 2.1 ブートパーティションを b に設定

カーネルがパーティション `b` (ミニルートを書き込んだ場所) から起動するよう設定します。これにより `sysinst` が `sd0` をインストール先として検出できるようになります (ルートパーティションはインストール候補から除外されるため)。

1. **Settings** を開きます
2. **Boot Partition** を `b` に設定します
3. **OK** をクリックします

### 2.2 ミニルートの起動

1. **Run > Full Reset** を選択して CPU 状態をリセットします
2. **File > Open ELF...** から `netbsd-GENERIC` を読み込みます
3. **F5** を押して実行を開始します

カーネルが起動し、パーティション `b` 上のミニルートをルートファイルシステムとしてマウントします。`sysinst` インストーラが自動的に開始されます。

### 2.3 sysinst の実行

インストーラのプロンプトに従います:

1. **Install NetBSD to hard disk** を選択します
2. インストーラが SCSI ディスク `sd0` を検出します
3. パーティションレイアウトを聞かれたら **Manually define partitions** を選択します
4. パーティション `a` を以下のように編集します:
   - **type**: `FFS` (FFSv2 は不可)
   - **install**: `Yes`
   - **newfs**: `Yes`
   - **mount**: `Yes`
   - **mount point**: `/`
5. パーティション `b` (swap) はそのまま変更不要です
6. **Partition sizes ok** を選択し、レイアウトを確認します
7. インストールソースを聞かれたら:
   - CD-ROM ISO を設定済みの場合: **CD-ROM** (`cd0`) を選択
   - それ以外: **FTP** を選択し、NetBSD ミラーの URL を入力 (例: `cdn.netbsd.org`、ディレクトリ `/pub/NetBSD/NetBSD-10.1`)
8. インストールセットを選択します (最低限: `base`, `etc`)
9. 展開の完了を待ちます
10. タイムゾーン、root パスワード等の設定を行います

> **重要**: エミュレータのディスクイメージではパーティション `a` がディスクラベル上で `unused` となっています。sysinst がファイルシステムを作成して `/` としてマウントできるよう、手動で `FFS` に設定し install/newfs/mount フラグを有効にする必要があります。

> **ヒント**: コンソールウィンドウにはミラー URL などの長い入力をペーストできます。

### 2.4 インストールの完了

sysinst が完了したら:

1. インストーラメニューから **Reboot** を選択するか、**Shift+F5** でエミュレータを停止します

---

## フェーズ 3: インストール済みシステムの起動

### 3.1 ブートパーティションを a に戻す

1. **Settings** を開きます
2. **Boot Partition** を `a` に設定します
3. **OK** をクリックします

### 3.2 カーネルの読み込み

1. エミュレータが実行中の場合は停止します (**Shift+F5**)
2. **Run > Full Reset** を選択します
3. **File > Open ELF...** から `netbsd-GENERIC` を読み込みます

### 3.3 設定の確認

**Settings** で以下を確認します:

- **Board Type**: `MVME147`
- **Boot Partition**: `a`
- **SCSI Disks**: インストール済みディスクイメージが SCSI ID 0
- CD-ROM ISO のパスは不要であればクリアできます

### 3.4 起動

1. **F5** を押して実行を開始します
2. NetBSD がインストール済みディスクから起動し、コンソールウィンドウに起動メッセージが表示されます

初回起動時は `/etc/rc.conf` が未設定のため、**シングルユーザーモード** で起動します:

```
/etc/rc.conf is not configured.  Multiuser boot aborted.
Enter pathname of shell or RETURN for /bin/sh:
```

**Enter** を押してシェルに入ります。以下の 2 つのコマンドを実行して、ルートファイルシステムを書き込み可能にし、ターミナルタイプを設定します:

```
# mount -u -o rw /
# export TERM=vt100
```

### 3.5 おすすめの初期設定

`/etc/profile` に `TERM=vt100` を設定しておくと、ログイン時に自動で適用されます:

```
# cat /etc/profile
#       $NetBSD: profile,v 1.1 1997/06/21 06:07:39 mikel Exp $
#
# System-wide .profile file for sh(1).
export TERM=vt100
#
```

ひと通りの設定が済んだら、`/etc/rc.conf` 内の `rc_configured` を `YES` に変更してマルチユーザーモードを有効にします。

`/etc/rc.conf` 内の `rc_configured` が `YES` になっていることを確認の上:

```
# grep 'rc_configured' /etc/rc.conf
rc_configured=YES
#
```

**Ctrl+D** でシングルユーザーモードを抜けて、マルチユーザーモードに移行します。次回以降はマルチユーザーモードで自動的に起動し、ログインプロンプトが表示されます。

新規インストールの NetBSD では `root` でログインします (インストール時に指定したパスワード)。

---

## クイックリファレンス

### キーボードショートカット

| キー | 動作 |
|---|---|
| F5 | 実行 |
| Shift+F5 | 停止 |
| F10 | ステップ実行 (1 命令) |
| F4 | カーソル位置まで実行 |

### メニュー構成

| メニュー | 項目 |
|---|---|
| File | Open Binary, Open S-Record, Open ELF, Exit |
| Run | Run, Stop, Step, Run to Cursor, Set PC to Cursor, Reset, Full Reset |
| View | Console Window, Breakpoints Window, Toggle LST View |
| Settings | Emulator Settings |

### 設定ファイル

設定はアプリケーションディレクトリの `appsettings.json` に保存されます。設定項目の詳細は [README](../README_ja.md) を参照してください。

---

## ディスクイメージの拡張

ディスクイメージの容量が不足した場合（例: X Window System パッケージのインストール用）、
拡張スクリプトでサイズを変更できます:

```bash
./tools/expand-netbsd-disk.sh -s 2G netbsd.img
```

拡張後、NetBSD を起動してファイルシステムをリサイズしてください: `resize_ffs /dev/sd0a`

全オプション、Windows での使用方法、トラブルシューティングは [Disk Image and Utility Tools](tools_ja.md#netbsd-ディスクイメージの拡張) を参照してください。

---

## X Window System（オプション）

Em68030 エミュレータは `wsfb` フレームバッファドライバを使用して NetBSD 上で X Window System をサポートしています。
MVME147_FB カーネル（genfb, wskbd, wsmouse ドライバ付き）とカスタムビルドの Xorg サーバーが必要です。
公式の NetBSD/mvme68k リリースには Xorg が含まれていないためです。

### 前提条件

- **カーネル**: MVME147_FB（本プロジェクトのリリースまたはソースからビルド）
- **ディスク容量**: 2 GB 以上（必要に応じて `expand-netbsd-disk` で拡張）
- **X11 ベースセット**: NetBSD リリースの xbase, xcomp, xetc, xfont, xserver
- **Xorg サーバー**: [Em68030-Guest-NetBSD リリース](https://github.com/hha0x617/Em68030-Guest-NetBSD/releases)の `xserver-wsfb-mvme68k.tgz`

### 手順 1: X11 ベースセットのインストール

CD-ROM 経由でセットをダウンロード（[create-iso](tools_ja.md#iso-イメージの作成ファイル転送) 参照）:

```sh
mount -t cd9660 /dev/cd0a /mnt
cd /
for set in xbase xcomp xetc xfont xserver; do
    tar xpzf /mnt/${set}.tgz
    echo "${set} done"
done
umount /mnt
```

### 手順 2: wsfb ドライバ付き Xorg サーバーのインストール

`xserver-wsfb-mvme68k.tgz` を CD-ROM 経由で転送して展開:

```sh
mount -t cd9660 /dev/cd0a /mnt
cd /
tar xpzf /mnt/xserver-wsfb-mvme68k.tgz
umount /mnt
ln -s /usr/X11R7/bin/Xorg /usr/X11R7/bin/X
```

### 手順 3: xorg.conf の作成

```sh
cat > /etc/X11/xorg.conf << 'EOF'
Section "ServerFlags"
    Option "AutoAddDevices" "false"
EndSection

Section "ServerLayout"
    Identifier   "Layout0"
    Screen       "Screen0"
    InputDevice  "Keyboard0" "CoreKeyboard"
    InputDevice  "Mouse0"    "CorePointer"
EndSection

Section "InputDevice"
    Identifier  "Keyboard0"
    Driver      "kbd"
    Option      "Protocol" "wskbd"
    Option      "Device"   "/dev/wskbd0"
EndSection

Section "InputDevice"
    Identifier  "Mouse0"
    Driver      "mouse"
    Option      "Protocol" "wsmouse"
    Option      "Device"   "/dev/wsmouse0"
EndSection

Section "Device"
    Identifier  "Card0"
    Driver      "wsfb"
    Option      "device"   "/dev/ttyE0"
    Option      "HWCursor" "false"
EndSection

Section "Screen"
    Identifier  "Screen0"
    Device      "Card0"
    DefaultDepth 16
    SubSection "Display"
        Depth   16
        Modes   "1024x768"
    EndSubSection
EndSection
EOF
```

主要な設定:
- **`AutoAddDevices false`** — ホットプラグによる kbd/mouse ドライバの無効化を防止
- **`HWCursor false`** — Em68030 の仮想フレームバッファに必要
- **`/dev/ttyE0`** — wsdisplay デバイスパスを明示指定

### 手順 4: X の起動

```sh
startx
```

フレームバッファウィンドウに X カーソルが表示されれば成功です。

---

## トラブルシューティング

### "boot device: \<unknown\>" と表示される
ブートスタブが SCSI コントローラを認識できません。**Board Type** が `MVME147` に設定されていること、少なくとも 1 つの SCSI ディスクが設定されていることを確認してください。

### カーネルが起動直後にパニックする
- 新しいカーネルを読み込む前に **Run > Full Reset** でメモリ状態をクリアしてください
- ディスクイメージに有効なミニルートまたはインストールがあるか確認してください
- 正しいカーネルを使用しているか確認してください: フェーズ 1 は `netbsd-RAMDISK`、フェーズ 2・3 は `netbsd-GENERIC`

### RAMDISK カーネルが SCSI ディスクを検出できない
- Settings でディスクイメージが SCSI ID 0 に設定されているか確認してください
- ディスクイメージファイルが存在し、空でないことを確認してください

### コンソールウィンドウに何も表示されない
- **View > Console Window** からコンソールウィンドウを開いてください
- エミュレータが実行中であることを確認してください (**F5** で開始)

### sysinst が CD-ROM のインストールセットを見つけられない
- **Settings > SCSI CD-ROM** に ISO のパスが正しく設定されているか確認してください
- ISO は NetBSD/mvme68k の標準リリース ISO である必要があります

### Windows Defender SmartScreen にブロックされる
実行ファイルにコード署名がないため、初回実行時に SmartScreen によりブロックされることがあります。「詳細情報」をクリックし「実行」を選択するか、exe ファイルを右クリックしてプロパティの「全般」タブで「許可する」にチェックを入れてください。
