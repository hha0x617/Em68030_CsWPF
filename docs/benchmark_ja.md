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
