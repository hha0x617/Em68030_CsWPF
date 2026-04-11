# ディスクイメージ・ユーティリティツール

`tools/` ディレクトリには、ディスクイメージの作成・管理およびゲスト OS へのファイル転送用スクリプトが含まれています。

## 前提条件

- **シェルスクリプト (`.sh`)** の実行には Linux 環境が必要です。Windows では
  **WSL (Windows Subsystem for Linux)** で Ubuntu 22.04 以降を使用してください。
  WSL のインストール: `wsl --install Ubuntu-24.04`
- Windows 上で `core.autocrlf=true`（デフォルト）のまま clone した場合、
  `.sh` ファイルの改行コードが CRLF に変換され、WSL で実行すると
  `/bin/bash^M: bad interpreter` エラーが発生します。対処方法:
  LF のまま clone する: `git clone -c core.autocrlf=input <repo-url>`、
  または既存ファイルを変換する: `sed -i 's/\r$//' tools/*.sh`
- **PowerShell スクリプト (`.ps1`)** は Windows 上で動作し、
  **Docker Desktop** のインストールと起動が必要です。
  スクリプトの実行が実行ポリシーでブロックされる場合は、以下のいずれかで対応してください:
  - `Set-ExecutionPolicy RemoteSigned -Scope CurrentUser` を実行し、
    ローカルで作成されたスクリプトの実行を許可する。ただし、この設定により
    デジタル署名のないローカルの `.ps1` ファイルがすべて実行可能になります。
  - または、実行ポリシーの変更や Docker を必要としない、WSL 上の同等の
    `.sh` スクリプトを使用する。
- Linux ディスクイメージを作成するスクリプト（`create-debian-disk.sh`、
  `expand-linux-disk.sh`）は `sudo`（root 権限）で実行する必要があります。

## 一覧

| スクリプト | 動作環境 | 説明 |
|-----------|---------|------|
| `create-netbsd-disk.ps1` / `.sh` | Windows, Linux/WSL | NetBSD ディスクイメージ作成（BSD ディスクラベル付き） |
| `create-debian-disk.sh` | Linux/WSL | Debian/m68k ディスクイメージ作成（debootstrap 使用） |
| `create-gentoo-disk.sh` | Linux/WSL | Gentoo/m68k ディスクイメージ作成（stage3 tarball から） |
| `expand-netbsd-disk.ps1` / `.sh` | Windows, Linux/WSL | 既存 NetBSD ディスクイメージの拡張 |
| `expand-linux-disk.sh` | Linux/WSL | 既存 Linux (Debian/Gentoo) ディスクイメージの拡張 |
| `create-iso.ps1` / `.sh` | Windows, Linux/WSL | ファイル転送用 ISO イメージ作成 |
| `mkdisklabel.c` | (ヘルパー) | NetBSD VID ディスクラベルライター（自動コンパイル） |

---

## NetBSD ディスクイメージの作成

NetBSD VID ディスクラベル付きの raw SCSI ディスクイメージを作成します。
miniroot イメージを指定すると sd0b に配置されます（インストール用）。

**Windows (PowerShell、Docker が必要):**
```powershell
.\tools\create-netbsd-disk.ps1 -Size 2G -Miniroot miniroot.fs -Output netbsd.img
```

**Linux / WSL:**
```bash
./tools/create-netbsd-disk.sh -s 2G -m miniroot.fs -o netbsd.img
```

> **注意:** シェルスクリプトは `mkdisklabel.c` を自動コンパイルするため、`gcc`（または `$CC` で指定した C コンパイラ）が必要です。
> Ubuntu/WSL の場合: `sudo apt install build-essential`

| オプション | デフォルト | 説明 |
|-----------|----------|------|
| `-s` / `-Size` | `2G` | ディスクイメージサイズ (500M〜4T) |
| `-m` / `-Miniroot` | 自動検出 | sd0b に配置する miniroot.fs。省略時は出力先・スクリプトディレクトリから `miniroot.fs` または `miniroot.fs.gz` を自動検出。`.gz` は自動解凍。miniroot が見つからない場合、sd0b はそのまま 32 MB のスワップパーティションとして確保されます。 |
| `-o` / `-Output` | `disk.img` | 出力ファイル |

miniroot ダウンロード先: `https://cdn.netbsd.org/pub/NetBSD/NetBSD-10.1/mvme68k/installation/miniroot/`

---

## Debian ディスクイメージの作成

debootstrap を使用して Debian/m68k のディスクイメージを作成します。root 権限が必要です。

```bash
sudo ./tools/create-debian-disk.sh -s 1G -p root -o debian.img
```

| オプション | デフォルト | 説明 |
|-----------|----------|------|
| `-s` | `1G` | ディスクイメージサイズ (500M〜4T) |
| `-p` | `root` | root パスワード |
| `-o` | `debian.img` | 出力ファイル |
| `-w` | `64` | スワップパーティションサイズ (MB) |
| `-n` | (無効) | NAT ネットワーク設定を有効化 (10.0.2.15/24) |

必要なパッケージ: `debootstrap`, `qemu-user-static` **6.0 以上** (binfmt_misc の F フラグ付き),
`sfdisk`, `mkfs.ext4`, `openssl`

> **注意:** `qemu-m68k-static` はバージョン 6.0 以上が必要です。古いバージョン
> （例: Ubuntu 18.04 付属の 2.11）では、現在の Debian sid パッケージが使用する
> m68k 命令の一部に対応しておらず、debootstrap の second stage で
> "Illegal instruction" エラーが発生します。

---

## Gentoo ディスクイメージの作成

stage3 tarball から Gentoo/m68k のディスクイメージを作成します。root 権限が必要です。

```bash
sudo ./tools/create-gentoo-disk.sh -t stage3-m68k-openrc-<DATE>.tar.xz -s 2G -o gentoo.img
# or
sudo ./tools/create-gentoo-disk.sh -t stage3-m68k-systemd-<DATE>.tar.xz -s 2G -o gentoo.img
```

| オプション | デフォルト | 説明 |
|-----------|----------|------|
| `-t` | (必須) | Gentoo stage3 tarball |
| `-s` | `2G` | ディスクイメージサイズ (500M〜4T) |
| `-p` | `root` | root パスワード |
| `-o` | `gentoo.img` | 出力ファイル |
| `-w` | `64` | スワップパーティションサイズ (MB) |
| `-i` | 自動判定 | init システム (`openrc` または `systemd`)。tarball のファイル名から自動判定 |
| `-n` | (無効) | NAT ネットワーク設定を有効化 (10.0.2.15/24) |

**Stage3 ダウンロード**（スクリプト実行前にダウンロードしてください）:

```bash
# 最新の tarball ファイル名を確認 (openrc または systemd)
curl -s https://distfiles.gentoo.org/releases/m68k/autobuilds/latest-stage3-m68k-openrc.txt
curl -s https://distfiles.gentoo.org/releases/m68k/autobuilds/latest-stage3-m68k-systemd.txt

# いずれかをダウンロード (<DATE> を上記で確認したタイムスタンプに置き換え)
wget https://distfiles.gentoo.org/releases/m68k/autobuilds/current-stage3-m68k-openrc/stage3-m68k-openrc-<DATE>.tar.xz
wget https://distfiles.gentoo.org/releases/m68k/autobuilds/current-stage3-m68k-systemd/stage3-m68k-systemd-<DATE>.tar.xz
```

---

## NetBSD ディスクイメージの拡張

既存の NetBSD ディスクイメージを拡張し、VID ディスクラベルを更新します。
既存のファイルシステムとパーティションオフセットは保持されます（read-modify-write 方式）。

**Windows (PowerShell、Docker が必要):**
```powershell
.\tools\expand-netbsd-disk.ps1 -Size 2G netbsd.img
.\tools\expand-netbsd-disk.ps1 netbsd.img            # ラベルのみ
```

**Linux / WSL:**
```bash
./tools/expand-netbsd-disk.sh -s 2G netbsd.img
./tools/expand-netbsd-disk.sh netbsd.img            # ラベルのみ書き換え（サイズ変更なし）
```

> **注意:** シェルスクリプトは `mkdisklabel.c` を自動コンパイルするため、`gcc`（または `$CC` で指定した C コンパイラ）が必要です。
> Ubuntu/WSL の場合: `sudo apt install build-essential`

| オプション | デフォルト | 説明 |
|-----------|----------|------|
| `-s` / `-Size` | (なし) | 新しいサイズ。省略時はラベルのみ書き換え |
| `-w` / `-SwapMB` | `32` | スワップパーティション (sd0b) サイズ (MB) |

拡張後、NetBSD を起動してファイルシステムをリサイズしてください:
```
# resize_ffs /dev/sd0a
```

---

## Linux ディスクイメージの拡張

既存の Linux (Debian/Gentoo) ディスクイメージを拡張します。MBR パーティションテーブルを書き換え、
`resize2fs` でファイルシステムをリサイズします。ゲスト側での追加作業は不要です。root 権限が必要です。

```bash
sudo ./tools/expand-linux-disk.sh -s 2G debian.img
sudo ./tools/expand-linux-disk.sh -s 4G gentoo.img
```

| オプション | デフォルト | 説明 |
|-----------|----------|------|
| `-s` | (必須) | 新しいサイズ (現在のイメージより大きい必要あり。小さい値は拒否されます)。上の例の `2G` / `4G` は説明用で、省略時に使えるデフォルトではありません。 |
| `-w` | `64` | スワップパーティションサイズ (MB) |
| `-f` | 自動判定 | ファイルシステムタイプ (`ext2`, `ext3`, `ext4`) |

---

## ISO イメージの作成（ファイル転送）

ディレクトリから ISO イメージを作成し、エミュレータの SCSI CD-ROM 経由で
ゲスト OS にファイルを転送します。

**Windows (PowerShell、Docker が必要):**
```powershell
.\tools\create-iso.ps1 C:\path\to\files
.\tools\create-iso.ps1 C:\path\to\files -Output transfer.iso
```

**Linux / WSL:**
```bash
./tools/create-iso.sh /path/to/files
./tools/create-iso.sh -o transfer.iso /path/to/files
```

| オプション | デフォルト | 説明 |
|-----------|----------|------|
| `-o` / `-Output` | `<ディレクトリ名>.iso` | 出力 ISO ファイル |

**ゲスト側での使い方:**
1. エミュレータの Settings > SCSI CD-ROM に ISO ファイルを設定
2. ゲスト内でマウント:
   ```sh
   # NetBSD
   mount -t cd9660 /dev/cd0a /mnt

   # Linux
   mount -t iso9660 /dev/sr0 /mnt
   ```
3. `/mnt/` からファイルをコピー
4. アンマウント: `umount /mnt`

---

## 必要な環境のまとめ

| スクリプト | root | Docker | 必要なパッケージ |
|-----------|------|--------|-----------------|
| `create-netbsd-disk.ps1` | — | 必要 | — |
| `create-netbsd-disk.sh` | 不要 | 不要 | `gcc`, `libc6-dev` |
| `create-debian-disk.sh` | 必要 | 不要 | `debootstrap`, `qemu-user-static` (>= 6.0), `sfdisk`, `mkfs.ext4`, `openssl` |
| `create-gentoo-disk.sh` | 必要 | 不要 | `sfdisk`, `mkfs.ext2`, `openssl` |
| `expand-netbsd-disk.ps1` | — | 必要 | — |
| `expand-netbsd-disk.sh` | 不要 | 不要 | `gcc`, `libc6-dev` |
| `expand-linux-disk.sh` | 必要 | 不要 | `sfdisk`, `resize2fs`, `e2fsck` |
| `create-iso.ps1` | — | 必要 | — |
| `create-iso.sh` | 不要 | 不要 | `genisoimage` or `mkisofs` |

> **注意:** すべてのシェルスクリプトは起動時に必要なパッケージの有無を自動検出します。
> 不足がある場合はインストールの確認を求め、承認されれば `apt-get` でインストールします。
> 拒否された場合はエラー終了します。
