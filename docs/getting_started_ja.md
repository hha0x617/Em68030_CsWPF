# はじめに

Em68030 エミュレータでゲスト OS をインストール・起動するためのセットアップガイドです。

## ゲスト OS インストールガイド

| ガイド | 説明 |
|--------|------|
| [NetBSD](getting_started_netbsd_ja.md) | 仮想 SCSI ディスクに NetBSD/mvme68k 10.1 をインストール・起動 |
| [Debian Linux](getting_started_debian_ja.md) | Debian ルートファイルシステムの構築と Linux 6.12 の起動 |
| [Gentoo Linux](getting_started_gentoo_ja.md) | Gentoo ルートファイルシステムの構築と Linux 6.12 の起動 |

## その他のセットアップガイド

| ガイド | 説明 |
|--------|------|
| [フレームバッファディスプレイ (Linux)](https://github.com/hha0x617/Em68030-Guest-Linux/blob/main/docs/setup_framebuffer_ja.md) | fbcon と X Window System のセットアップ (Em68030-Guest-Linux) |
| [フレームバッファディスプレイ (NetBSD)](https://github.com/hha0x617/Em68030-Guest-NetBSD/blob/main/docs/setup_framebuffer_ja.md) | wscons, wsfb, X Window System のセットアップ (Em68030-Guest-NetBSD) |
| [NAT ネットワーク](setup_nat_network_ja.md) | NAT モードのゲストネットワーク設定 (Linux/NetBSD) |
| [TAP ブリッジネットワーク](setup_tap_bridge_ja.md) | TAP-Windows アダプタによるブリッジネットワーク |

## 謝辞

このエミュレータは、産業界の大半が m68k アーキテクチャから離れた後も、サポートを
継続してきた複数のオープンソース OS コミュニティがあって初めて存在しうるものです。
以下のプロジェクトとそれを支えるボランティアの方々に、心より感謝申し上げます:

- **The NetBSD Project** — 「もちろん NetBSD は動きます (Of course it runs NetBSD)」
  という長年の姿勢と、NetBSD/mvme68k を Tier-II プラットフォームとしてビルド・
  テスト・リリースし続けていただいていることに感謝します。特に `port-mvme68k` の
  メンテナーの方々には、pkgsrc・カーネル・ツールチェインの継続的な変更に対して
  ポートを機能させ続けていただいていることに感謝します。
  https://www.netbsd.org/ports/mvme68k/

- **Linux カーネルコミュニティ** — 特に m68k サブシステムのメンテナーの方々と
  `linux-m68k` メーリングリストの貢献者の皆様 — に感謝します。皆様の継続的な活動が
  なければ、このディレクトリの Debian / Gentoo ガイドのいずれも成立しません。
  http://www.linux-m68k.org/

- **The Debian Project / Debian Ports チーム** — Debian/m68k unofficial port の維持、
  このアーキテクチャ向け buildd インフラの運用、`debian-68k` メーリングリストの
  活性化に感謝します。Debian コミュニティ全体が「リリース対象外アーキテクチャも
  歓迎する」という方針を取り続けていただいているからこそ、これが可能になっています。
  https://www.ports.debian.org/

- **The Gentoo Project / Gentoo m68k チーム** — m68k 向け stage3 tarball の定期的な
  公開、portage ツリーの m68k 対応の維持、エミュレーション環境でのソースベース
  インストールのサポートに感謝します。Gentoo コミュニティがニッチなアーキテクチャを
  一流の対象として扱う姿勢は、何物にも代えがたい貴重なものです。
  https://wiki.gentoo.org/wiki/M68k

皆様の継続的な活動によって、これらのインストールガイドが成立しています。
