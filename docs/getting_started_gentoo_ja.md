# はじめに: Em68030 で Gentoo Linux をインストール・起動する

本ガイドでは、Em68030 エミュレータ (MVME147 ボード) を使用して Gentoo Linux/m68k を仮想 SCSI ハードディスクにインストールし、起動するまでの手順を説明します。

## 概要

インストールは以下のフェーズで構成されます:

1. **ルートファイルシステムの準備** — ディスクイメージの作成、パーティション作成、Gentoo stage3 tarball の展開 (WSL または Linux ホストを使用)
2. **カーネルのビルド** — MVME147 用 Linux カーネルのクロスコンパイル (ソースパッチ適用を含む)
3. **システムの起動** — Em68030 でカーネルを読み込み、Gentoo を起動

## 前提条件

- Em68030 エミュレータ
- ファイルシステム準備・カーネルビルド用の Linux 環境 (Windows 上の WSL2 または Debian/Ubuntu ホスト)
- m68k 用クロスコンパイラツールチェイン (Debian パッケージの `m68k-linux-gnu`、または Gentoo crossdev の `m68k-unknown-linux-gnu`)
- Gentoo ファイル・カーネルソースをダウンロードするためのインターネット接続
- 約 1 GB の空きディスク容量

## エミュレータ側の Linux 対応

MVME147 上の Linux/m68k には、以下のエミュレータ機能が必要です。いずれも実装済みです。

### Linux Bootinfo フォーマット

Linux/m68k は NetBSD とは異なるブートプロトコルを使用します。設定の **Target OS** を `Linux` に設定すると、エミュレータのブートスタブはメモリ上に `bi_record` 構造体チェインを構築します:

| タグ | 値 | 説明 |
|---|---|---|
| `BI_MACHTYPE` | 0x0001 | マシンタイプ: `MACH_MVME147` (5) + `MMU_68030` + `FPU_68882` |
| `BI_MEMCHUNK` | 0x0004 | メモリ領域: 開始アドレス + サイズ |
| `BI_COMMAND_LINE` | 0x0007 | カーネルコマンドライン (例: `root=/dev/sda1 console=ttyS0,9600`) |
| `BI_VME_TYPE` | 0x8000 | VME ボードタイプ: `VME_TYPE_MVME147` (0x0147) |
| `BI_VME_BRDINFO` | 0x8001 | ボード情報 (クロック速度等) |
| `BI_LAST` | 0x0000 | レコードチェインの終端 |

### 仮想 16550A UART

エミュレータは **0xFFFE2000** (8 バイト) にメモリマップされた仮想 16550A UART デバイスを搭載しています。このデバイスは実際の MVME147 ハードウェアには存在しませんが、Linux のユーザー空間コンソール I/O に必要です。

**なぜ必要か**: 実際の MVME147 はシリアル通信に Z8530 SCC を使用します。Linux の初期ブートコンソール (`earlyprintk`) は SCC 経由で出力できますが、ユーザー空間プログラム (init、シェル、systemd) には適切なシリアルドライバに裏打ちされた tty デバイスが必要です。上流 Linux カーネルには MVME147 用の Z8530 ベース tty ドライバが含まれていません。16550 互換の UART を追加することで、Linux はサポートの充実した `8250/16550` シリアルドライバをユーザー空間コンソール (`/dev/ttyS0`) に使用できます。

仮想 UART がサポートする機能:
- 16550A レジスタセット一式 (RBR/THR, IER, IIR/FCR, LCR, MCR, LSR, MSR, SCR, DLAB)
- MCR ループバックモード (ビット 4): 8250 ドライバの自動検出に使用
- 64 バイト受信 FIFO
- 割り込み出力 (アクティブハイ)

### RTC の年エンコーディング

M48T02 RTC の年エンコーディングは NetBSD と Linux で異なります:
- **NetBSD**: 年基準 1968 (`year - 1968` を格納)
- **Linux**: 生の 2 桁年 (`year % 100`)

エミュレータは **Target OS** 設定に基づいて適切なエンコーディングを自動選択します。

---

## フェーズ 1: ルートファイルシステムの準備

ファイルシステムの準備はすべて Linux ホストまたは WSL2 上で行います。Windows では ext2/ext4 ファイルシステムをネイティブに扱えないためです。

### 1.1 Stage3 Tarball のダウンロード

Gentoo m68k の stage3 tarball をダウンロードします。エミュレータ環境では軽量な **OpenRC** 版を推奨します:

```bash
wget https://distfiles.gentoo.org/releases/m68k/autobuilds/current-stage3-m68k-openrc/stage3-m68k-openrc-<DATE>.tar.xz
```

最新の日付は以下で確認できます:
`https://distfiles.gentoo.org/releases/m68k/autobuilds/current-stage3-m68k-openrc/`

> **注意**: **systemd** 版 (`stage3-m68k-systemd-<DATE>.tar.xz`) も利用可能です。systemd を使用したい場合は `current-stage3-m68k-systemd/` からダウンロードし、[セクション 1.5](#15-ルートファイルシステムの設定) と[セクション 2.4](#24-カーネルの設定) の追加設定に関する注記を参照してください。

tarball のサイズは約 200 MB です。

### 1.2 ディスクイメージの作成とパーティション分割

2048 MB のディスクイメージを作成し、`fdisk` でパーティションを分割します。
Gentoo の stage3 tarball（圧縮時約 197 MB）は展開すると 1 GB 以上になるため、
2 GB のイメージを推奨します。

```bash
dd if=/dev/zero of=gentoo.img bs=1M count=2048

# イメージのパーティション分割
fdisk gentoo.img
```

2 つのパーティションを作成します:
- **パーティション 1** (Linux、約 1984 MB): ルートファイルシステム
- **パーティション 2** (Linux swap、約 64 MB): スワップ領域

`fdisk` コマンドの例:
```
n p 1 <enter> +1984M
n p 2 <enter> <enter>
t 2 82
w
```

### 1.3 フォーマットとマウント

ループデバイスを設定し、フォーマットしてマウントします:

```bash
sudo losetup -fP gentoo.img
# 割り当てられたループデバイスを確認 (例: /dev/loop0)
LOOPDEV=/dev/loop0

sudo mkfs.ext2 ${LOOPDEV}p1
sudo mkswap ${LOOPDEV}p2

sudo mkdir -p /mnt/gentoo
sudo mount ${LOOPDEV}p1 /mnt/gentoo
```

> **注意**: ext4 ではなく ext2 を使用してください。m68k カーネルでは ext4 サポートが限定的な場合があり、ext2 のほうがシンプルで m68k での実績があります。

### 1.4 Stage3 Tarball の展開

```bash
# OpenRC 版:
sudo tar xpf stage3-m68k-openrc-*.tar.xz -C /mnt/gentoo --xattrs-include='*.*' --numeric-owner
# systemd 版:
# sudo tar xpf stage3-m68k-systemd-*.tar.xz -C /mnt/gentoo --xattrs-include='*.*' --numeric-owner
```

### 1.5 ルートファイルシステムの設定

#### fstab

```bash
cat << 'EOF' | sudo tee /mnt/gentoo/etc/fstab
/dev/sda1    /        ext2    defaults,noatime    0 1
/dev/sda2    none     swap    sw                  0 0
proc         /proc    proc    defaults            0 0
sysfs        /sys     sysfs   defaults            0 0
devtmpfs     /dev     devtmpfs defaults           0 0
EOF
```

#### root パスワード

```bash
# root パスワードの設定 (クロス環境用の openssl 方式)
HASHED=$(openssl passwd -6 "your_password_here")
sudo sed -i "s|root:[^:]*|root:${HASHED}|" /mnt/gentoo/etc/shadow
```

#### ホスト名

```bash
echo "mvme147" | sudo tee /mnt/gentoo/etc/hostname
```

#### シリアルコンソール

使用する stage3 の init システムに応じて、以下の**いずれか一方**を実行してください:

> **選択肢 A — OpenRC** (SysVinit 形式の inittab):
>
> ```bash
> # シリアルコンソールログインを有効化
> echo "s0:12345:respawn:/sbin/agetty 9600 ttyS0 vt100" | sudo tee -a /mnt/gentoo/etc/inittab
> ```

> **選択肢 B — systemd**:
>
> ```bash
> sudo mkdir -p /mnt/gentoo/etc/systemd/system/getty.target.wants
> sudo ln -s /usr/lib/systemd/system/serial-getty@.service \
>     /mnt/gentoo/etc/systemd/system/getty.target.wants/serial-getty@ttyS0.service
> ```

#### ネットワーク (オプション)

エミュレータのネットワークモードが **Virtual (Echo Server)**（デフォルト）の場合、
ゲスト側のネットワーク設定は不要です。以下の設定は **NAT (Host Network)** モードで
libslirp 経由でゲストからホストネットワークにアクセスする場合のみ必要です。

デフォルトの NAT ゲートウェイアドレスは `10.0.2.2`、ゲスト IP は `10.0.2.15` です。
これらはエミュレータのデフォルト設定（設定 → ネットワーク）と一致します。

エミュレータの NAT 実装は UDP/TCP パケットを宛先 IP のままホスト OS のネットワーク
スタック経由で転送します。組み込みの DNS フォワーダは存在しないため、
`/etc/resolv.conf` にはホストから到達可能な DNS サーバ（例: `8.8.8.8` や
LAN の DNS サーバ）を指定してください。

使用する stage3 の init システムに応じて、以下の**いずれか一方**を実行してください:

> **選択肢 A — OpenRC** (`/etc/conf.d/net`):
>
> ```bash
> cat << 'EOF' | sudo tee /mnt/gentoo/etc/conf.d/net
> config_eth0="10.0.2.15/24"
> routes_eth0="default via 10.0.2.2"
> EOF
> cd /mnt/gentoo/etc/init.d && sudo ln -s net.lo net.eth0
> ```

> **選択肢 B — systemd** (systemd-networkd):
>
> ```bash
> cat << 'EOF' | sudo tee /mnt/gentoo/etc/systemd/network/10-eth0.network
> [Match]
> Name=eth0
>
> [Network]
> Address=10.0.2.15/24
> Gateway=10.0.2.2
> EOF
> ```

続けて、いずれの init システムでも、ホストから到達可能な DNS サーバで名前解決を設定します:

```bash
cat << 'EOF' | sudo tee /mnt/gentoo/etc/resolv.conf
nameserver 8.8.8.8
EOF
```

#### TAP ブリッジモード（オプション）

TAP ブリッジモードはゲストをホスト LAN に直接接続し、DHCP や完全なネットワーク参加を
可能にします。TAP-Windows ドライバのインストールと Windows ブリッジ設定が必要です。
詳細は [TAP ブリッジ セットアップガイド](setup_tap_bridge_ja.md) を参照してください。

### 1.6 アンマウント

アンマウント前にカレントディレクトリがマウントポイントの外にあることを確認してください。
`/mnt/gentoo/...` 内にいると `umount` は "target is busy" で失敗します。

```bash
cd ~
sudo umount /mnt/gentoo
sudo losetup -d ${LOOPDEV}
```

`gentoo.img` ファイルの準備が完了しました。Windows 上の Em68030 ディレクトリにコピーしてください。

---

## フェーズ 2: Linux カーネルのビルド

### 2.1 クロスコンパイラのインストール

Debian/Ubuntu では m68k クロスコンパイラがパッケージとして利用可能です:

```bash
sudo apt install build-essential gcc-m68k-linux-gnu flex bison libncurses-dev libssl-dev
```

これによりクロスコンパイラ (`m68k-linux-gnu-gcc`)、ビルドツール (`make`, `gcc`)、カーネル設定・コンパイルに必要な依存パッケージがインストールされます。

Gentoo Linux では `crossdev` を使用します:

```bash
sudo crossdev -t m68k-unknown-linux-gnu
```

> **注意**: 以下の例では Debian のクロスコンパイラプレフィックス `m68k-linux-gnu-` を使用しています。Gentoo の crossdev を使用する場合は `m68k-unknown-linux-gnu-` に置き換えてください。

### 2.2 カーネルソースのダウンロード

```bash
wget https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.12.17.tar.xz
tar xf linux-6.12.17.tar.xz
cd linux-6.12.17
```

### 2.3 カーネルソースのパッチ適用

エミュレータの仮想ハードウェアをサポートするため、2 つのソースファイルを修正する必要があります。

#### パッチ 1: ブートコンソールの登録解除を防止

`arch/m68k/kernel/early_printk.c` には、初期ブートコンソールを明示的に登録解除する `late_initcall` が含まれています。実機の MVME16x では SCC ベースの tty ドライバがあるため正しい動作ですが、MVME147 では初期コンソールが唯一のカーネルレベル SCC 出力パスです。ただし、パッチ 2 で 16550 UART を追加するため、8250 ドライバが `ttyS0` を実コンソールとして登録し、初期ブートコンソール (`debug0`) は不要になります。`keep_bootcon` カーネルパラメータで通常は保持できますが、`unregister_early_console()` はこのパラメータをバイパスします。このパッチは MVME147 での明示的な登録解除を防止し、`keep_bootcon` 指定時に `debug0` と `ttyS0` を共存させます (デバッグに有用)。

> **注意**: カーネルコマンドラインで `earlyprintk` や `keep_bootcon` を使用しない場合、このパッチはオプションです。`console=ttyS0` のみの場合、8250 ドライバがすべての出力を処理するため、このパッチは影響しません。

`arch/m68k/kernel/early_printk.c` を編集し、スキップ条件に `MACH_IS_MVME147` を追加します:

```c
static int __init unregister_early_console(void)
{
    /* 初期コンソールに依存するプラットフォームでは登録解除をスキップ */
    if (!early_console || MACH_IS_MVME16x || MACH_IS_MVME147)
        return 0;
    return unregister_console(early_console);
}
late_initcall(unregister_early_console);
```

#### パッチ 2: 仮想 16550 UART をプラットフォームデバイスとして登録

エミュレータはアドレス 0xFFFE2000 に仮想 16550A UART を提供しています。8250/16550 シリアルドライバがこのデバイスを `/dev/ttyS0` として認識するよう、カーネルにデバイス情報を伝える必要があります。

`arch/m68k/mvme147/config.c` を編集し、先頭部分 (既存の include と共に) に以下を追加します:

```c
#include <linux/platform_device.h>
#include <linux/serial_8250.h>
```

次に、ファイル末尾にプラットフォームデバイス登録コードを追加します:

```c
/*
 * Em68030 エミュレータが提供する仮想 16550A UART。
 * 実際の MVME147 ハードウェアには存在しない。
 */
static struct plat_serial8250_port mvme147_uart_data[] = {
    {
        .mapbase  = 0xFFFE2000,
        .irq      = 0,
        .uartclk  = 1843200,
        .iotype   = UPIO_MEM,
        .flags    = UPF_BOOT_AUTOCONF | UPF_SKIP_TEST | UPF_IOREMAP,
        .regshift = 0,
    },
    { },  /* 終端 */
};

static struct platform_device mvme147_uart_device = {
    .name = "serial8250",
    .id   = PLAT8250_DEV_PLATFORM,
    .dev  = {
        .platform_data = mvme147_uart_data,
    },
};

static int __init mvme147_uart_init(void)
{
    if (!MACH_IS_MVME147)
        return -ENODEV;
    return platform_device_register(&mvme147_uart_device);
}
device_initcall(mvme147_uart_init);
```

### 2.4 カーネルの設定

```bash
# mvme16x のデフォルト設定をベースにする
make ARCH=m68k CROSS_COMPILE=m68k-linux-gnu- mvme16x_defconfig
make ARCH=m68k CROSS_COMPILE=m68k-linux-gnu- menuconfig
```

以下のオプションが有効になっていることを確認します。各オプションの menuconfig 上の階層を括弧内に示します:

```
CONFIG_M68030=y               # (Processor type and features > 68030 support)
CONFIG_MMU=y                  # (自動選択)
CONFIG_MVME147=y              # (Platform dependent setup > VME board support > Motorola MVME147 support)
CONFIG_SCSI=y                 # (Device Drivers > SCSI device support)
CONFIG_MVME147_SCSI=y         # (Device Drivers > SCSI device support > SCSI low-level drivers > WD33C93 SCSI driver for MVME147)
CONFIG_BLK_DEV_SD=y           # (Device Drivers > SCSI device support > SCSI disk support)
CONFIG_EXT2_FS=y              # (File systems > Second extended fs support)
CONFIG_PROC_FS=y              # (File systems > Pseudo filesystems > /proc file system support)
CONFIG_SERIAL_8250=y          # (Device Drivers > Character devices > Serial drivers > 8250/16550 and compatible serial support)
CONFIG_SERIAL_8250_CONSOLE=y  # (Device Drivers > Character devices > Serial drivers > Console on 8250/16550 and compatible serial port)
CONFIG_NET=y                  # (Networking support)
CONFIG_INET=y                 # (Networking support > Networking options > TCP/IP networking)
CONFIG_NETDEVICES=y           # (Device Drivers > Network device support)
CONFIG_ETHERNET=y             # (Device Drivers > Network device support > Ethernet driver support)
CONFIG_NET_VENDOR_AMD=y       # (Device Drivers > Network device support > Ethernet driver support > AMD devices)
CONFIG_MVME147_NET=y          # (Device Drivers > Network device support > Ethernet driver support > AMD devices > MVME147 (LANCE) Ethernet support)
```

> **注意**: `CONFIG_SERIAL_8250` と `CONFIG_SERIAL_8250_CONSOLE` は必須です。これらがないと、カーネルは仮想 16550 UART を使用できず、ユーザー空間にコンソールがありません (`Warning: unable to open an initial console`)。

> **注意**: **OpenRC** 版の stage3 (推奨) を使用する場合、`CONFIG_CGROUPS` とそのサブオプションは不要です。**systemd** 版の stage3 を選択した場合は、`CONFIG_CGROUPS=y`、`CONFIG_CGROUP_PIDS=y`、`CONFIG_CGROUP_FREEZER=y`、`CONFIG_CGROUP_DEVICE=y`、`CONFIG_CGROUP_BPF=y` も有効にする必要があります — 詳細は Debian ガイドを参照してください。

> **注意**: `mvme16x_defconfig` では `CONFIG_M68040` と `CONFIG_M68060` がデフォルトで有効です。カーネルは実行時に CPU タイプを検出するため、有効のままでも問題ありません。無効にするとカーネルサイズが若干小さくなります。

> **注意**: `CONFIG_TRIM_UNUSED_KSYMS` は `mvme16x_defconfig` でデフォルト有効です。外部カーネルモジュール（例: `em68030fb`）をビルドする予定がある場合はこのオプションを無効にしてください。有効のままだと、カーネルから未使用のエクスポートシンボルが削除され、`insmod` 時に "Unknown symbol in module" エラーが発生します。menuconfig: **General setup > Enable unused/obsolete exported symbols** → `n` で無効化（このメニュー項目を `n` にすると `CONFIG_TRIM_UNUSED_KSYMS=y` が設定されます。紛らわしいですが、メニュー項目の `y` が trimming の *無効化* に相当します）。

> **ヒント**: menuconfig で `/` キーを押すとシンボル名で検索できます (例: `SERIAL_8250`)。各オプションの階層と依存関係が表示されます。

menuconfig で設定を保存した後、`grep` で確認できます:

```bash
grep -E "CONFIG_(M68030|MVME147|MVME147_SCSI|MVME147_NET|SCSI|BLK_DEV_SD|EXT2_FS|SERIAL_8250|SERIAL_8250_CONSOLE|NET_VENDOR_AMD)=" .config
```

すべてのオプションが `=y` と表示されていれば正しく設定されています。オプションが表示されない、または `# CONFIG_XXX is not set` となっている場合は、`menuconfig` を再実行して有効化してください。

### 2.5 ビルド

```bash
make ARCH=m68k CROSS_COMPILE=m68k-linux-gnu- vmlinux -j$(nproc)
```

出力される `vmlinux` は ELF バイナリで、Em68030 の **File > Open ELF...** で直接読み込めます。

---

## フェーズ 3: システムの起動

### 3.1 Em68030 の設定

1. Em68030 を起動します
2. **Settings** を開きます
3. **Board Type** を `MVME147` に設定します
4. **Target OS** を `Linux` に設定します
5. `gentoo.img` を **SCSI ID 0** の SCSI ディスクとして追加します
6. **Memory Size** を 128 MB に設定します
7. **Kernel command line** を `root=/dev/sda1 console=ttyS0,9600` に設定します
8. **OK** をクリックします

> **重要**: **Target OS** 設定は必ず `Linux` にしてください。この設定はブートスタブのフォーマット (Linux bootinfo vs. NetBSD bootinfo) と RTC の年エンコーディングの両方を制御します。

### 3.2 読み込みと起動

1. **File > Open ELF...** から `vmlinux` を読み込みます
2. **F5** を押して実行を開始します
3. **View > Console Window** を開きます

カーネルが起動メッセージを表示します。ブートシーケンス完了後、init システム (stage3 の選択に応じて OpenRC または systemd) がサービスを起動し、ログインプロンプトが表示されます。

### 3.3 初回ログイン

フェーズ 1.5 で設定したパスワードを使用して `root` でログインします。

### 3.4 停止と再起動

システムを停止するには:

```
# halt
```

再起動するには:

```
# reboot
```

`reboot` コマンドは Linux カーネルの `mvme147_reset()` を実行し、PCC ウォッチドッグタイマーによるハードウェアリセットを行います。エミュレータはこれを検出してウォームリブート (カーネル ELF の再読み込みと再起動) を実行します。

**Shift+F5** でいつでもエミュレータを停止できます。

---

## カーネルコマンドラインオプション

| オプション | 説明 |
|---|---|
| `root=/dev/sda1` | ルートファイルシステムデバイス (必須) |
| `console=ttyS0,9600` | 仮想 16550 UART をシステムコンソールとして使用 (必須) |
| `earlyprintk` | Z8530 SCC 経由の初期ブートメッセージを有効化 (オプション、デバッグ用) |
| `keep_bootcon` | ttyS0 登録後も初期ブートコンソールを維持 (オプション; パッチ 1 が必要) |

通常の使用では `root=/dev/sda1 console=ttyS0,9600` で十分です。

### フレームバッファと X Window System（オプション）

フレームバッファコンソール (fbcon) や X Window System などのグラフィカルディスプレイの
設定については、[フレームバッファディスプレイ セットアップガイド](setup_framebuffer_ja.md)
を参照してください。

---

## 既知の制限事項

1. **Gentoo m68k は実験的** -- Gentoo の m68k アーキテクチャはメンテナーが限られています。一部のパッケージはビルドや動作に問題がある可能性があります。
2. **エミュレータにネットワークサポートなし** -- パッケージのインストール (`emerge`) にはネットワークアクセスが必要です。stage3 準備フェーズでパッケージを事前インストールするか、ネットワークエミュレーションを実装する必要があります。
3. **仮想 16550 UART は非標準** -- 0xFFFE2000 の 16550 UART は実際の MVME147 ハードウェアには存在しません。フェーズ 2.3 のカーネルパッチは Em68030 エミュレータ固有のものです。
4. **カーネルパッチが必要** -- 標準の Linux カーネルにはエミュレータの仮想 UART サポートが含まれていません。フェーズ 2.3 で説明した 2 つのパッチは必須です。

## トラブルシューティング

### カーネルが起動時にパニックする

- カーネルが `CONFIG_MVME147=y` と `CONFIG_M68030=y` でビルドされていることを確認してください
- カーネルソースパッチ (フェーズ 2.3) が正しく適用されていることを確認してください
- カーネルを読み込む前に **Run > Full Reset** を試してください

### "VFS: Cannot open root device"

- カーネルで `CONFIG_MVME147_SCSI=y` と `CONFIG_BLK_DEV_SD=y` が有効であることを確認してください
- ルートファイルシステムのパーティションに有効な ext2 ファイルシステムがあることを確認してください
- カーネルコマンドラインで正しいルートデバイスが指定されていることを確認してください (`root=/dev/sda1`)

### "Warning: unable to open an initial console"

- 8250/16550 シリアルドライバがカーネルに組み込まれていません
- `CONFIG_SERIAL_8250=y` と `CONFIG_SERIAL_8250_CONSOLE=y` が設定されていることを確認してください
- UART プラットフォームデバイスのカーネルソースパッチ (パッチ 2) が適用されていることを確認してください
- カーネルを再ビルドしてください

### "printk: legacy bootconsole [debug0] disabled" 以降コンソール出力がない

- 初期ブートコンソール (Z8530 SCC) が 16550 UART コンソール (`ttyS0`) に置き換えられました
- `ttyS0` が登録されていない場合、ここで出力が停止します。UART プラットフォームデバイスパッチと `CONFIG_SERIAL_8250=y` を確認してください
- `earlyprintk` を使用している場合、`keep_bootcon` を追加して両方のコンソールをアクティブに保てます (パッチ 1 が必要)

### 日付/時刻が間違っている (例: 2026 年の代わりに 2058 年)

- Em68030 の設定で **Target OS** が `Linux` になっていることを確認してください
- RTC の年エンコーディングは NetBSD と Linux で異なります。設定が間違っていると年の計算が狂います

---

## 参考資料

- [Gentoo m68k プロジェクト](https://wiki.gentoo.org/wiki/Project:M68k)
- [Linux/m68k FAQ](https://www.linux-m68k.org/faq/faq.html)
- [カーネルソース: arch/m68k/kernel/head.S](https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/arch/m68k/kernel/head.S) -- ブートエントリポイントと bootinfo パース処理
