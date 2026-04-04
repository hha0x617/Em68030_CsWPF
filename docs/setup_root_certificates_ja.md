# ルート証明書 セットアップガイド (NetBSD)

NetBSD で HTTPS 経由のファイルダウンロード（`pkg_add`、`ftp`、`git clone` 等）を行う際、
以下のような SSL 証明書エラーが発生する場合があります:

```
fatal: unable to access '...': SSL: certificate verification failed
```

または:

```
Certificate verification failed for ...
unable to get local issuer certificate
```

これは NetBSD にデフォルトで Mozilla ルート証明書（CA 証明書）が含まれていないことが原因です。
以下の手順でインストールと設定を行います。

## 方法 1: mozilla-rootcerts-openssl のインストール（推奨）

`mozilla-rootcerts-openssl` パッケージは証明書のインストールと OpenSSL 証明書ディレクトリへの
ハッシュシンボリックリンク作成を自動的に行います:

```sh
pkg_add mozilla-rootcerts-openssl
```

これが最も簡単な方法で、インストール後に追加のコマンドは不要です。

## 方法 2: mozilla-rootcerts による手動セットアップ

`mozilla-rootcerts`（`-openssl` なし）がインストール済みの場合は以下を実行:

```sh
mozilla-rootcerts install
```

これにより `/etc/openssl/certs` に証明書が展開され、OpenSSL が必要とするハッシュ
シンボリックリンクが作成されます。

## NetBSD 10.0 以降: certctl rehash

NetBSD 10.0 で導入された `certctl` で証明書ストアを管理できます。
証明書インストール後に以下を実行:

```sh
certctl rehash
```

これにより証明書ストアが最新の状態に更新されます。

## 確認方法

インストール後、HTTPS 接続が正常に動作することを確認:

```sh
# ftp (NetBSD 内蔵の HTTP クライアント) でテスト
ftp -o /dev/null https://cdn.netbsd.org/

# pkg_add でテスト (PKG_PATH が設定済みの場合)
pkg_add -n bash
```

## 一時的な回避策（非推奨）

証明書検証を一時的に無効にする方法もありますが、**セキュリティチェックが無効化されるため、
最終手段としてのみ使用してください。**

`git` の場合:

```sh
git config --global http.sslVerify false
```

`pkg_add` / `ftp` の場合:

```sh
export FTP_SSL_INSECURE=1
export SSL_NO_VERIFY_PEER=1
```

**使用後は必ず検証を再有効化してください:**

```sh
git config --global http.sslVerify true
unset FTP_SSL_INSECURE
unset SSL_NO_VERIFY_PEER
```
