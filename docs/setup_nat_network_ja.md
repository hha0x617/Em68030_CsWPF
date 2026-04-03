# NAT ネットワーク セットアップガイド

このガイドでは、Em68030 エミュレータの NAT (Host Network) モードの設定方法を説明します。
NAT モードはホスト側の設定なしでゲストにインターネットアクセスを提供します。

## 概要

| モード | 説明 | 用途 |
|--------|------|------|
| Virtual (Echo Server) | 内蔵エコーサーバー、ホストネットワークなし | ネットワークスタックのテスト（ゲスト設定不要） |
| **NAT (Host Network)** | **エミュレータ経由のユーザーモード NAT** | **ホスト設定不要でインターネットアクセス** |
| TAP (Bridge) | ホスト LAN への直接 L2 ブリッジ | 完全なネットワーク参加、DHCP、サーバーホスティング（[セットアップガイド](setup_tap_bridge_ja.md)） |

## NAT モードの仕組み

エミュレータの NAT 実装は UDP/TCP パケットを宛先 IP のままホスト OS のネットワーク
スタック経由で転送します。ゲストは仮想ゲートウェイを通じて通信します。

- デフォルトゲートウェイ IP: `10.0.2.2`
- デフォルトゲスト IP: `10.0.2.15/24`
- これらの値は設定 → Network で変更可能

> **注意:** 内蔵の DNS フォワーダーはありません。ゲストの `/etc/resolv.conf`（または
> 同等のファイル）にはホストから到達可能な DNS サーバー（例: `8.8.8.8`、または
> LAN の DNS サーバー）を指定する必要があります。

## ステップ 1: エミュレータの設定

1. エミュレータの設定を開く（設定メニューまたはツールバーボタン）
2. **Network Mode** ドロップダウンで **「NAT (Host Network)」** を選択
3. 必要に応じて **Gateway IP** を調整（デフォルト: `10.0.2.2`）
4. OK をクリックして保存
5. カーネルイメージを再読み込みして変更を反映

## ステップ 2: ゲスト OS の設定

### Linux (Debian — systemd-networkd)

```ini
# /etc/systemd/network/10-eth0.network
[Match]
Name=eth0

[Network]
Address=10.0.2.15/24
Gateway=10.0.2.2
```

```bash
# /etc/resolv.conf
nameserver 8.8.8.8
```

適用:

```bash
systemctl restart systemd-networkd
```

### Linux (Gentoo — OpenRC)

```bash
# /etc/conf.d/net
config_eth0="10.0.2.15/24"
routes_eth0="default via 10.0.2.2"
```

```bash
# /etc/resolv.conf
nameserver 8.8.8.8
```

サービスリンクを作成して起動:

```bash
cd /etc/init.d && ln -s net.lo net.eth0
rc-service net.eth0 start
```

### NetBSD

`/etc/rc.conf` を編集:

```
ifconfig_le0="inet 10.0.2.15 netmask 255.255.255.0"
defaultroute="10.0.2.2"
```

`/etc/resolv.conf` を編集:

```
nameserver 8.8.8.8
```

適用:

```
# /etc/rc.d/network restart
```

## ステップ 3: 接続の確認

1. **IP アドレスの確認:**
   ```bash
   # Linux
   ip addr show eth0

   # NetBSD
   ifconfig le0
   ```

2. **接続テスト:**
   ```bash
   ping -c 3 8.8.8.8
   ```

3. **DNS 解決のテスト:**
   ```bash
   ping -c 3 google.com
   ```

## トラブルシューティング

### ゲストがゲートウェイ (10.0.2.2) に到達できない

- エミュレータの設定でネットワークモードが「NAT (Host Network)」になっていることを確認
- ネットワークモード変更後にカーネルイメージを再読み込みしたことを確認
- ゲスト IP とゲートウェイがエミュレータの設定と一致していることを確認

### IP 接続は動作するが DNS が動作しない

- `/etc/resolv.conf` が動作する DNS サーバーを指していることを確認
- パブリック DNS サーバーを試す: `nameserver 8.8.8.8`
- IP アドレスに直接 ping を試す: `ping 8.8.8.8`

### 接続が遅い、またはパケットがドロップされる

- NAT モードはユーザー空間でのパケット処理のため、TAP ブリッジモードよりも
  やや高いオーバーヘッドがあります
- より良いパフォーマンスが必要な場合は [TAP ブリッジモード](setup_tap_bridge_ja.md) を検討してください

## 他のモードとの比較

| 機能 | Virtual (Echo) | NAT | TAP (Bridge) |
|------|---------------|-----|--------------|
| ホスト設定 | 不要 | 不要 | TAP ドライバ + ブリッジ設定 |
| ゲスト IP アドレス | N/A | プライベート (10.0.2.x) | LAN アドレス（DHCP または静的） |
| インターネットアクセス | なし | あり | あり |
| LAN からアクセス可能 | いいえ | いいえ | はい |
| プロトコルサポート | ICMP, TCP, UDP エコーのみ | TCP, UDP, ICMP | すべて（L2 ブリッジ） |
