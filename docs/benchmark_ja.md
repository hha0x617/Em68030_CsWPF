# ベンチマーク

## Dhrystone 2.1

Em68030 エミュレータ上の NetBSD/mvme68k 10.1 で [Keith-S-Thompson/dhrystone](https://github.com/Keith-S-Thompson/dhrystone) を使用して計測。

### ビルド

```bash
ftp -o dhry.tar.gz https://github.com/Keith-S-Thompson/dhrystone/archive/refs/heads/master.tar.gz
tar xzf dhry.tar.gz
cd dhrystone-master/v2.1
cc -O2 -o dhrystone dhry_1.c dhry_2.c -DTIME
```

### 結果

| 項目 | 値 |
|------|---|
| 実行回数 | 10,000,000 |
| 1 回あたりの時間 | 10.3 マイクロ秒 |
| Dhrystones/秒 | 97,087 |
| **DMIPS** | **55.3** |

DMIPS = Dhrystones/秒 / 1757 (VAX 11/780 = 1.0 DMIPS を基準とした正規化値)。

### ホスト環境

| 項目 | 値 |
|------|---|
| エミュレータ | Em68030 (C# / WPF) |
| JIT | OFF |
| ホスト CPU | Intel Core i7-13700 |
| ホスト OS | Windows 11 Pro 25H2 |

エミュレーション速度はホスト PC の性能に依存します。異なるハードウェアでは結果が変わります。

### 比較

| システム | DMIPS | 備考 |
|---------|-------|------|
| VAX 11/780 (1979) | 1.0 | 基準値 |
| MC68030 25 MHz (実機) | ~5-8 | 概算、実装により異なる |
| **Em68030 C# on i7-13700** | **55.3** | 実機 MC68030 25 MHz の約 7-11 倍 |
| **Em68030 C++ on i7-13700** | **79.1** | 実機 MC68030 25 MHz の約 10-15 倍 |
| Raspberry Pi 1 (ARM1176, 700 MHz) | ~875 | 参考値 |

### 備考

- エミュレータのステータスバーには ~217 MHz (概算サイクルベースクロック) が表示されます。これは概算サイクルカウントに基づくエミュレータ内部の指標であり、メモリアクセス・関数呼び出し・文字列操作を含むアプリケーションレベルの性能を測定する DMIPS とは直接比較できません。
- ベンチマーク出力に "Program compiled without 'register' attribute" と報告されています。
- C# 版の DMIPS スコアは C++ 版の約 70% (55.3 vs 79.1) であり、MHz の比率 (~217 vs ~270 MHz) とほぼ一致しています。

---

## CoreMark 1.0

Em68030 エミュレータ上の NetBSD/mvme68k 10.1 で [eembc/coremark](https://github.com/eembc/coremark) を使用して計測。

### 前提条件

ゲスト上に `git` と `gmake` をインストール（NetBSD 標準の `make` は CoreMark の Makefile と非互換）:

```sh
pkg_add git gmake
```

### ビルド

```sh
git clone https://github.com/eembc/coremark.git
cd coremark
gmake PORT_DIR=posix CC=gcc XCFLAGS="-O2 -m68030" RECURSE_OUT=1
```

### 実行

```sh
./coremark.exe 0 0 0 5000
```

### 結果

| 項目 | 値 |
|------|---|
| 反復回数 | 5,000 |
| 合計時間（秒） | 44.301 |
| **CoreMark スコア** | **112.86** |
| コンパイラ | GCC 10.5.0 |
| コンパイラフラグ | `-O2 -m68030 -DPERFORMANCE_RUN=1 -lrt` |
| メモリ配置 | Heap |

CoreMark スコア = 反復回数 / 合計時間。

**公式レポートフォーマット:**
```
CoreMark 1.0 : 112.864269 / GCC10.5.0 -O2 -O2 -m68030 -DPERFORMANCE_RUN=1 -lrt / Heap
```

### ホスト環境

| 項目 | 値 |
|------|---|
| エミュレータ | Em68030 (C# / WPF) |
| JIT | OFF |
| ホスト CPU | Intel Core i7-13700 |
| ホスト OS | Windows 11 Pro 25H2 |

### 比較

| システム | CoreMark | 備考 |
|---------|----------|------|
| MC68030 25 MHz (実機) | ~10-20 | 概算、実装により異なる |
| Em68030 C++ on i7-13700 | 155.55 | 実機 MC68030 25 MHz の約 8-15 倍 |
| **Em68030 C# on i7-13700** | **112.86** | C++ 版の約 73% |
| Raspberry Pi 1 (ARM1176, 700 MHz) | ~1,073 | 参考値 |
