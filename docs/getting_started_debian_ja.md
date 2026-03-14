# はじめに: Em68030 で Debian Linux をインストール・起動する

本ガイドでは、Em68030 エミュレータ (MVME147 ボード) を使用して Debian/m68k を仮想 SCSI ハードディスクにインストールし、起動するまでの手順を説明します。

Debian には [Debian Ports](https://www.ports.debian.org/) プロジェクトの一部として積極的にメンテナンスされている m68k ポートがあり、m68k ハードウェア向けで最もサポートが充実した Linux ディストリビューションの一つです。

> **注意**: Ubuntu は m68k アーキテクチャをサポートしていません。本ガイドは Debian のみを対象としています。

## 概要

インストールは以下のフェーズで構成されます:

1. **ルートファイルシステムの準備** -- ディスクイメージの作成、パーティション作成、`debootstrap` による Debian ルートファイルシステムの構築 (WSL または Linux ホストを使用)
2. **カーネルのビルド** -- MVME147 用 Linux カーネルのクロスコンパイル (ソースパッチ適用を含む)
3. **システムの起動** -- Em68030 でカーネルを読み込み、Debian を起動

## 前提条件

- Em68030 エミュレータ
- ファイルシステム準備・カーネルビルド用の Linux 環境 (Windows 上の WSL2 または Debian/Ubuntu ホスト)
- m68k 用クロスコンパイラツールチェイン (Debian パッケージの `m68k-linux-gnu`)
- Debian パッケージ・カーネルソースをダウンロードするためのインターネット接続
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

### 1.1 必要なツールのインストール

Debian/Ubuntu ホスト上で:

```bash
sudo apt update
sudo apt install debootstrap qemu-user-static binfmt-support
```

`qemu-user-static` はユーザーモードエミュレーションにより x86 ホスト上で m68k バイナリを実行可能にします。`debootstrap` の第 2 ステージに必要です。

binfmt_misc が m68k 用に設定されていることを確認します:

```bash
cat /proc/sys/fs/binfmt_misc/qemu-m68k
```

出力に `flags: F` が含まれていれば正しく設定されています。`F` (fix-binary) フラグは必須です。このフラグにより、カーネルは登録時に QEMU インタプリタのパスを解決するため、chroot 内でも動作します。

ファイルが存在しない、または `F` フラグがない場合はサービスを再起動します:

```bash
sudo systemctl restart systemd-binfmt
```

それでも表示されない場合は手動で登録します:

```bash
echo ':qemu-m68k:M::\x7fELF\x01\x02\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x02\x00\x04:\xff\xff\xff\xff\xff\xff\xff\x00\x00\x00\x00\x00\x00\x00\x00\x00\xff\xfe\xff\xff:/usr/bin/qemu-m68k-static:F' | sudo tee /proc/sys/fs/binfmt_misc/register
```

### 1.2 ディスクイメージの作成とパーティション分割

512 MB のディスクイメージを作成し、`fdisk` でパーティションを分割します:

```bash
dd if=/dev/zero of=debian.img bs=1M count=512

fdisk debian.img
```

2 つのパーティションを作成します:
- **パーティション 1** (Linux、約 480 MB): ルートファイルシステム
- **パーティション 2** (Linux swap、約 32 MB): スワップ領域

`fdisk` コマンドの例:
```
n p 1 <enter> +480M
n p 2 <enter> <enter>
t 2 82
w
```

### 1.3 フォーマットとマウント

ループデバイスを設定し、フォーマットしてマウントします:

```bash
sudo losetup -fP debian.img
# 割り当てられたループデバイスを確認 (例: /dev/loop0)
LOOPDEV=/dev/loop0

sudo mkfs.ext4 ${LOOPDEV}p1
sudo mkswap ${LOOPDEV}p2

sudo mkdir -p /mnt/debian
sudo mount ${LOOPDEV}p1 /mnt/debian
```

### 1.4 debootstrap で Debian を構築

`debootstrap` を実行して最小限の Debian システムをインストールします。Debian/m68k パッケージは `ftp.ports.debian.org` でホストされています:

```bash
sudo debootstrap --arch=m68k --foreign --no-check-gpg \
    sid /mnt/debian http://ftp.ports.debian.org/debian-ports/
```

> **注意**: `--foreign` はホストアーキテクチャ (x86_64) とターゲット (m68k) が異なるために必要です。`--no-check-gpg` は ports アーカイブの GPG 検証をスキップします。`sid` (unstable) を使用するのは、m68k が Debian の安定版リリースでは利用できないためです。

QEMU ユーザーモードエミュレーションを使用して第 2 ステージを完了します:

```bash
sudo chroot /mnt/debian /debootstrap/debootstrap --second-stage
```

> **注意**: binfmt_misc が `F` フラグ付きで登録されている場合 (手順 1.1 参照)、カーネルはホストの `/usr/bin/qemu-m68k-static` を自動的に使用して chroot 内の m68k バイナリを実行します。`qemu-m68k-static` を chroot 内にコピーする必要はありません。`Exec format error` が発生した場合は、`F` フラグが設定されていることを確認してください (手順 1.1 参照)。

第 2 ステージでは chroot 内のすべてのパッケージを展開・設定します。数分かかる場合があります。

### 1.5 ルートファイルシステムの設定

> **注意**: QEMU ユーザーモードエミュレーションの m68k では対話シェルが動作しません。すべての設定は `sudo chroot /mnt/debian /bin/sh -c "command"` で非対話的に実行します。

一部の chroot コマンドに必要な仮想ファイルシステムをマウントします:

```bash
sudo mount --bind /dev /mnt/debian/dev
sudo mount --bind /dev/pts /mnt/debian/dev/pts
sudo mount -t proc proc /mnt/debian/proc
sudo mount -t sysfs sysfs /mnt/debian/sys
```

#### root パスワードの設定

```bash
HASHED=$(openssl passwd -6 "your_password_here")
sudo chroot /mnt/debian /bin/sh -c "usermod -p '${HASHED}' root"
```

#### fstab

```bash
sudo tee /mnt/debian/etc/fstab << 'EOF'
/dev/sda1    /        ext4    defaults,noatime    0 1
/dev/sda2    none     swap    sw                  0 0
proc         /proc    proc    defaults            0 0
sysfs        /sys     sysfs   defaults            0 0
devtmpfs     /dev     devtmpfs defaults           0 0
EOF
```

#### ホスト名

```bash
echo "mvme147" | sudo tee /mnt/debian/etc/hostname
echo "127.0.1.1 mvme147" | sudo tee -a /mnt/debian/etc/hosts
```

#### シリアルコンソール

```bash
# シリアルコンソールログインを有効化 (systemd)
sudo chroot /mnt/debian /bin/sh -c "systemctl enable serial-getty@ttyS0.service 2>/dev/null"
```

#### ネットワーク (オプション)

エミュレータのネットワークモードが **Virtual (Echo Server)**（デフォルト）の場合、
ゲスト側のネットワーク設定は不要です。以下の設定は **NAT (Host Network)** モードで
ゲストからホストネットワークにアクセスする場合のみ必要です。

デフォルトの NAT ゲートウェイアドレスは `10.0.2.2`、ゲスト IP は `10.0.2.15` です。
これらはエミュレータのデフォルト設定（設定 → ネットワーク）と一致します。

エミュレータの NAT 実装は UDP/TCP パケットを宛先 IP のままホスト OS のネットワーク
スタック経由で転送します。組み込みの DNS フォワーダは存在しないため、
`/etc/resolv.conf` にはホストから到達可能な DNS サーバ（例: `8.8.8.8` や
LAN の DNS サーバ）を指定してください。

```bash
cat << 'EOF' | sudo tee /mnt/debian/etc/systemd/network/10-eth0.network
[Match]
Name=eth0

[Network]
Address=10.0.2.15/24
Gateway=10.0.2.2
EOF
```

```bash
cat << 'EOF' | sudo tee /mnt/debian/etc/resolv.conf
nameserver 8.8.8.8
EOF
```

systemd-networkd を有効化します:

```bash
sudo chroot /mnt/debian /bin/sh -c "systemctl enable systemd-networkd 2>/dev/null"
```

#### TAP ブリッジモード（オプション）

TAP ブリッジモードはゲストをホスト LAN に直接接続し、DHCP や完全なネットワーク参加を
可能にします。TAP-Windows ドライバのインストールと Windows ブリッジ設定が必要です。
詳細は [TAP ブリッジ セットアップガイド](setup_tap_bridge_ja.md) を参照してください。

#### APT ソース (将来のパッケージインストール用)

```bash
sudo tee /mnt/debian/etc/apt/sources.list << 'EOF'
deb http://ftp.ports.debian.org/debian-ports/ sid main
deb http://ftp.ports.debian.org/debian-ports/ unreleased main
EOF
```

#### Debian Ports アーカイブ署名鍵

Debian Ports アーカイブはデフォルトの `debian-keyring` に含まれない独自の署名鍵を使用します。
この鍵がないと `apt update` が `NO_PUBKEY C6894E6BB25B9C99` で失敗します。
chroot セットアップ中に鍵パッケージをインストールしてください:

```bash
sudo chroot /mnt/debian /bin/sh -c "apt -o Acquire::AllowInsecureRepositories=true update && apt install -y debian-ports-archive-keyring"
```

### 1.6 アンマウント

```bash
sudo umount /mnt/debian/sys
sudo umount /mnt/debian/proc
sudo umount /mnt/debian/dev/pts
sudo umount /mnt/debian/dev
sudo umount /mnt/debian
sudo losetup -d ${LOOPDEV}
```

`debian.img` ファイルの準備が完了しました。Windows 上の Em68030 ディレクトリにコピーしてください。

---

## フェーズ 2: Linux カーネルのビルド

### 2.1 クロスコンパイラのインストール

Debian/Ubuntu では m68k クロスコンパイラがパッケージとして利用可能です:

```bash
sudo apt install build-essential gcc-m68k-linux-gnu flex bison libncurses-dev libssl-dev
```

これによりクロスコンパイラ (`m68k-linux-gnu-gcc`)、ビルドツール (`make`, `gcc`)、カーネル設定・コンパイルに必要な依存パッケージがインストールされます。

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
CONFIG_EXT4_FS=y              # (File systems > The Extended 4 (ext4) filesystem)
CONFIG_PROC_FS=y              # (File systems > Pseudo filesystems > /proc file system support)
CONFIG_SERIAL_8250=y          # (Device Drivers > Character devices > Serial drivers > 8250/16550 and compatible serial support)
CONFIG_SERIAL_8250_CONSOLE=y  # (Device Drivers > Character devices > Serial drivers > Console on 8250/16550 and compatible serial port)
CONFIG_NET=y                  # (Networking support)
CONFIG_INET=y                 # (Networking support > Networking options > TCP/IP networking)
CONFIG_NETDEVICES=y           # (Device Drivers > Network device support)
CONFIG_ETHERNET=y             # (Device Drivers > Network device support > Ethernet driver support)
CONFIG_NET_VENDOR_AMD=y       # (Device Drivers > Network device support > Ethernet driver support > AMD devices)
CONFIG_MVME147_NET=y          # (Device Drivers > Network device support > Ethernet driver support > AMD devices > MVME147 (LANCE) Ethernet support)
CONFIG_CGROUPS=y              # (General setup > Control Group support) -- systemd に必要
CONFIG_MEMCG=y                # (General setup > Control Group support > Memory controller)
CONFIG_CGROUP_PIDS=y          # (General setup > Control Group support > PIDs controller)
CONFIG_CGROUP_FREEZER=y       # (General setup > Control Group support > Freezer controller)
CONFIG_CGROUP_DEVICE=y        # (General setup > Control Group support > Device controller)
CONFIG_CGROUP_BPF=y           # (General setup > Control Group support > Support for eBPF programs attached to cgroups)
```

> **注意**: `CONFIG_SERIAL_8250` と `CONFIG_SERIAL_8250_CONSOLE` は必須です。これらがないと、カーネルは仮想 16550 UART を使用できず、ユーザー空間にコンソールがありません (`Warning: unable to open an initial console`)。

> **注意**: `CONFIG_CGROUPS` とそのサブオプションは systemd の起動に必要です。これらがないと、systemd は cgroup2 ファイルシステムのマウントに失敗し、ログインプロンプトまで到達しません。

> **注意**: `mvme16x_defconfig` では `CONFIG_M68040` と `CONFIG_M68060` がデフォルトで有効です。カーネルは実行時に CPU タイプを検出するため、有効のままでも問題ありません。無効にするとカーネルサイズが若干小さくなります。

> **注意**: `CONFIG_TRIM_UNUSED_KSYMS` は `mvme16x_defconfig` でデフォルト有効です。外部カーネルモジュール（例: `em68030fb`）をビルドする予定がある場合はこのオプションを無効にしてください。有効のままだと、カーネルから未使用のエクスポートシンボルが削除され、`insmod` 時に "Unknown symbol in module" エラーが発生します。menuconfig: **General setup > Enable unused/obsolete exported symbols** → `n` で無効化（このメニュー項目を `n` にすると `CONFIG_TRIM_UNUSED_KSYMS=y` が設定されます。紛らわしいですが、メニュー項目の `y` が trimming の *無効化* に相当します）。

> **ヒント**: menuconfig で `/` キーを押すとシンボル名で検索できます (例: `SERIAL_8250`)。各オプションの階層と依存関係が表示されます。

menuconfig で設定を保存した後、`grep` で確認できます:

```bash
grep -E "CONFIG_(M68030|MVME147|MVME147_SCSI|MVME147_NET|SCSI|BLK_DEV_SD|EXT4_FS|SERIAL_8250|SERIAL_8250_CONSOLE|NET_VENDOR_AMD|CGROUPS|MEMCG|CGROUP_PIDS|CGROUP_FREEZER|CGROUP_DEVICE|CGROUP_BPF)=" .config
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
5. `debian.img` を **SCSI ID 0** の SCSI ディスクとして追加します
6. **Memory Size** を 128 MB に設定します
7. **Kernel command line** を `root=/dev/sda1 console=ttyS0,9600` に設定します
8. **OK** をクリックします

> **重要**: **Target OS** 設定は必ず `Linux` にしてください。この設定はブートスタブのフォーマット (Linux bootinfo vs. NetBSD bootinfo) と RTC の年エンコーディングの両方を制御します。

### 3.2 読み込みと起動

1. **File > Open ELF...** から `vmlinux` を読み込みます
2. **F5** を押して実行を開始します
3. **View > Console Window** を開きます

カーネルが起動メッセージを表示します。ブートシーケンス完了後、systemd がサービスを起動し、ログインプロンプトが表示されます:

```
Debian GNU/Linux forky/sid mvme147 ttyS0

mvme147 login:
```

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

## Debian と Gentoo の比較 (m68k)

| | Debian | Gentoo |
|---|---|---|
| **パッケージ管理** | `apt` -- ビルド済みバイナリパッケージ | `emerge` -- ソースからビルド |
| **インストール方法** | `debootstrap` -- 高速、クロスコンパイル不要 | stage3 tarball の展開 |
| **パッケージ数** | m68k ポートに約 12,000 パッケージ | テスト済みパッケージが少ない |
| **メンテナンス** | 活発な Debian Ports コミュニティ | m68k メンテナーが限られている |
| **リソース使用量** | 中程度 (systemd) | 最小限 (OpenRC) |
| **エミュレータでの適性** | 良い -- バイナリパッケージにより低速なオンターゲットコンパイルが不要 | エミュレーション速度ではソースコンパイルは非実用的 |

m68k エミュレーション環境では Debian が一般的に推奨されます。バイナリパッケージにより低速なオンターゲットコンパイルが不要なためです。

---

## 既知の制限事項

1. **Debian m68k は非公式** -- m68k ポートは Debian Ports でメンテナンスされており、メインの Debian アーカイブには含まれていません。パッケージの利用可能性やテスト状況が限定的な場合があります。
2. **エミュレータにネットワークサポートなし** -- `apt install` にはネットワークアクセスが必要です。debootstrap フェーズでパッケージを事前インストールするか、ネットワークエミュレーションを実装する必要があります。
3. **仮想 16550 UART は非標準** -- 0xFFFE2000 の 16550 UART は実際の MVME147 ハードウェアには存在しません。フェーズ 2.3 のカーネルパッチは Em68030 エミュレータ固有のものです。
4. **カーネルパッチが必要** -- 標準の Linux カーネルにはエミュレータの仮想 UART サポートが含まれていません。フェーズ 2.3 で説明した 2 つのパッチは必須です。

## トラブルシューティング

### debootstrap の第 2 ステージが "Exec format error" で失敗する

- `qemu-user-static` と `binfmt-support` がインストールされ有効になっていることを確認してください
- binfmt_misc が `F` フラグ付きで登録されていることを確認: `cat /proc/sys/fs/binfmt_misc/qemu-m68k` -- 出力に `flags: F` が含まれている必要があります
- `F` フラグがない場合は再登録してください (手順 1.1 参照) -- `F` フラグなしではカーネルが chroot 内で QEMU インタプリタを見つけられません
- `sudo systemctl restart systemd-binfmt` で binfmt エントリを再登録してみてください

### カーネルが起動時にパニックする

- カーネルが `CONFIG_MVME147=y` と `CONFIG_M68030=y` でビルドされていることを確認してください
- カーネルソースパッチ (フェーズ 2.3) が正しく適用されていることを確認してください
- カーネルを読み込む前に **Run > Full Reset** を試してください

### "VFS: Cannot open root device"

- カーネルで `CONFIG_MVME147_SCSI=y` と `CONFIG_BLK_DEV_SD=y` が有効であることを確認してください
- ルートファイルシステムのパーティションに有効な ext4 ファイルシステムがあることを確認してください
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

### systemd が cgroup2 マウントエラーで失敗する

- カーネル設定で `CONFIG_CGROUPS=y` を有効にして再ビルドしてください

### 日付/時刻が間違っている (例: 2026 年の代わりに 2058 年)

- Em68030 の設定で **Target OS** が `Linux` になっていることを確認してください
- RTC の年エンコーディングは NetBSD と Linux で異なります。設定が間違っていると年の計算が狂います

---

## 参考資料

- [Debian Ports -- m68k](https://www.ports.debian.org/)
- [Debian m68k Wiki](https://wiki.debian.org/M68k)
- [Linux/m68k FAQ](https://www.linux-m68k.org/faq/faq.html)
- [カーネルソース: arch/m68k/kernel/head.S](https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/arch/m68k/kernel/head.S) -- ブートエントリポイントと bootinfo パース処理
- [debootstrap マニュアル](https://wiki.debian.org/Debootstrap)
