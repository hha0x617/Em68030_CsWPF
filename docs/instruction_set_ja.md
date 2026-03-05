# MC68030 エミュレータ (C# WPF) - 命令セット実装状況

日付: 2026-03-05

## 凡例

- [x] = 実装済み
- [ ] = 未実装
- JIT = JIT コンパイル対応（レジスタ専用、.L サイズ）

---

## 実装戦略 (C# WPF)

### オペコードディスパッチ

65536 エントリのデリゲートテーブル (`Action<ushort>[65536]`)。各エントリは
InstructionDecoder のメソッドへのデリゲート。`InitOpcodeTable()` で初期化し、
ホットオペコードは専用 fast handler で上書き登録。

### Fast ハンドラ

高頻度オペコードは Group デコーダをバイパスして直接ディスパッチ:

| ハンドラ | オペコード | 説明 |
|---------|---------|------|
| FastMOVEQ | 0x7000-0x7FFE (全 Group7) | ムーブクイック即値。ハンドラ内で bit8 チェック、不正時は例外4 |
| FastMOVE_L_Dn_Dm | MOVE.L Dn,Dm | レジスタ間ロングワード転送 |
| FastBRA_B | 0x6001-0x60FE | 8ビットディスプレースメント付き無条件分岐 |
| FastBcc_B | 0x6x01-0x6xFE (cond!=0) | 8ビットディスプレースメント付き条件分岐 |
| FastRTS | 0x4E75 | サブルーチンからの復帰 |
| FastADD_L | ADD.L Dn,Dm | レジスタ間ロングワード加算 |
| FastSUB_L | SUB.L Dn,Dm | レジスタ間ロングワード減算 |
| FastCMP_L | CMP.L Dn,Dm | レジスタ間ロングワード比較 |

C++ 版との違い: C# では FastBRA_B を専用ハンドラとして登録（C++ は DecodeGroup6 で処理）。
FastMOVEQ は全 Group7 オペコードに登録し、ハンドラ内で bit8 ガード（C++ はテーブル
登録時にガード）。

### レジスタスナップショット

遅延レジスタスナップショットは未採用。C++ では有効だが、C# では JIT オーバーヘッドに
より速度低下したためリバート。各命令実行前に都度スナップショットを取得。

### JIT コンパイラ

System.Reflection.Emit による IL コード生成。レジスタ専用命令ブロックを .NET IL に
コンパイルし、DynamicMethod として実行。

- ファイル: `Core/Jit/CompiledBlock.cs`, `Core/Jit/JitCache.cs`, `Core/Jit/JitCompiler.cs`
- ExecuteNextFast() と ExecuteNextFastJit() は完全に別メソッド（C++ と同様の分離設計）
- bool 分岐でメソッド選択。Func<bool> デリゲートディスパッチは 36→30 MHz の
  回帰を引き起こしたため不採用。
- JIT ON は現時点で JIT OFF より遅い (33 vs 36 MIPS): NoInlining メソッド呼出の
  per-instruction オーバーヘッドが、コンパイル可能ブロックの希少さに対して大きすぎる。

### FPU 診断トレース

FpuInstructionDecoder に `FpuTrace()` メソッドあり。`FpuTraceEnabled` フラグで
FPU 命令のトレース出力が可能（C++ 版にはない機能）。

### 性能 (JIT OFF)

約33 MIPS / 約204 MHz サイクル (Avg モード実測)、約6.1 サイクル/命令

### サイクルテーブル

`s_cycleTable[65536]` (byte): 静的ルックアップテーブル。EA コスト関数が
アドレッシングモードごとのメモリアクセスオーバーヘッドを計算。

### ソースファイル

| ファイル | 説明 |
|---------|------|
| Core/InstructionDecoder.cs | 整数命令デコーダ（約3200行） |
| Core/FpuInstructionDecoder.cs | FPU 命令デコーダ |
| Core/Jit/JitCompiler.cs | JIT 基本ブロックコンパイラ（IL 生成） |
| Core/Jit/CompiledBlock.cs | コンパイル済みブロック表現 |
| Core/Jit/JitCache.cs | JIT ブロックキャッシュ |
| Core/MC68030.cs | CPU コア、オペコードテーブル、実行ループ |
| Core/Mmu.cs | MMU / アドレス変換 |

---

## 整数命令

### データ転送

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | MOVE.B / .W / .L | データ転送 | |
| [x] | MOVEA.W / .L | アドレスレジスタへ転送 | JIT (.L Dn->An, An->Am) |
| [x] | MOVEQ | クイック転送（8ビット即値） | JIT |
| [x] | MOVEM | 複数レジスタ転送 | |
| [x] | MOVEP.W / .L | ペリフェラルデータ転送 | |
| [x] | MOVES | ファンクションコード付き転送 (SFC/DFC) | |
| [x] | MOVE from SR | ステータスレジスタ読出し | |
| [x] | MOVE to CCR | コンディションコードレジスタ書込み | |
| [x] | MOVE to SR | ステータスレジスタ書込み（スーパーバイザ） | |
| [x] | MOVE USP | ユーザスタックポインタ転送（スーパーバイザ） | |
| [x] | MOVEC | 制御レジスタ転送（スーパーバイザ） | |
| [x] | EXG | レジスタ交換 (Dn<->Dm, An<->Am, Dn<->An) | JIT |

### 算術演算

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | ADD.B / .W / .L | 加算 | JIT (.L Dn,Dm) |
| [x] | ADDA.W / .L | アドレスレジスタへ加算 | |
| [x] | ADDI | 即値加算 | |
| [x] | ADDQ | クイック加算 (1-8) | JIT (.L Dn / An) |
| [x] | ADDX.B / .W / .L | 拡張付き加算 | |
| [x] | SUB.B / .W / .L | 減算 | JIT (.L Dn,Dm) |
| [x] | SUBA.W / .L | アドレスレジスタから減算 | |
| [x] | SUBI | 即値減算 | |
| [x] | SUBQ | クイック減算 (1-8) | JIT (.L Dn / An) |
| [x] | SUBX.B / .W / .L | 拡張付き減算 | |
| [x] | NEG.B / .W / .L | 符号反転 | JIT (.L Dn) |
| [x] | NEGX.B / .W / .L | 拡張付き符号反転 | |
| [x] | CLR.B / .W / .L | クリア | JIT (.L Dn) |
| [x] | CMP.B / .W / .L | 比較 | JIT (.L Dn,Dm) |
| [x] | CMPA.W / .L | アドレスレジスタと比較 | |
| [x] | CMPI | 即値比較 | |
| [x] | CMPM.B / .W / .L | メモリ間比較 (An)+,(Am)+ | |
| [x] | CMP2 / CHK2 | 範囲チェック比較 (68020+) | |
| [x] | MULU.W | 符号なし乗算 16x16->32 | |
| [x] | MULS.W | 符号付き乗算 16x16->32 | |
| [x] | MULU.L | 符号なし乗算 32x32->32/64 (68020+) | |
| [x] | MULS.L | 符号付き乗算 32x32->32/64 (68020+) | |
| [x] | DIVU.W | 符号なし除算 32/16 | |
| [x] | DIVS.W | 符号付き除算 32/16 | |
| [x] | DIVU.L | 符号なし除算 64/32 (68020+) | |
| [x] | DIVS.L | 符号付き除算 64/32 (68020+) | |

### 論理演算

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | AND.B / .W / .L | 論理積 | JIT (.L Dn,Dm) |
| [x] | ANDI | 即値論理積 | |
| [x] | ANDI to CCR | CCR への即値論理積 | |
| [x] | ANDI to SR | SR への即値論理積（スーパーバイザ） | |
| [x] | OR.B / .W / .L | 論理和 | JIT (.L Dn,Dm) |
| [x] | ORI | 即値論理和 | |
| [x] | ORI to CCR | CCR への即値論理和 | |
| [x] | ORI to SR | SR への即値論理和（スーパーバイザ） | |
| [x] | EOR.B / .W / .L | 排他的論理和 | JIT (.L Dn,Dm) |
| [x] | EORI | 即値排他的論理和 | |
| [x] | EORI to CCR | CCR への即値排他的論理和 | |
| [x] | EORI to SR | SR への即値排他的論理和（スーパーバイザ） | |
| [x] | NOT.B / .W / .L | 論理否定 | JIT (.L Dn) |
| [x] | TST.B / .W / .L | テスト | JIT (.L Dn) |

### シフト・ローテート

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | ASL.B / .W / .L | 算術左シフト (レジスタ/即値/メモリ) | JIT (.L #imm,Dn) |
| [x] | ASR.B / .W / .L | 算術右シフト (レジスタ/即値/メモリ) | JIT (.L #imm,Dn) |
| [x] | LSL.B / .W / .L | 論理左シフト (レジスタ/即値/メモリ) | JIT (.L #imm,Dn) |
| [x] | LSR.B / .W / .L | 論理右シフト (レジスタ/即値/メモリ) | JIT (.L #imm,Dn) |
| [x] | ROL.B / .W / .L | 左ローテート (レジスタ/即値/メモリ) | |
| [x] | ROR.B / .W / .L | 右ローテート (レジスタ/即値/メモリ) | |
| [x] | ROXL.B / .W / .L | 拡張付き左ローテート (レジスタ/即値/メモリ) | |
| [x] | ROXR.B / .W / .L | 拡張付き右ローテート (レジスタ/即値/メモリ) | |

### ビット操作

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | BTST | ビットテスト (レジスタ/即値) | |
| [x] | BCHG | ビット変更 (レジスタ/即値) | |
| [x] | BCLR | ビットクリア (レジスタ/即値) | |
| [x] | BSET | ビットセット (レジスタ/即値) | |

### ビットフィールド (68020+)

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | BFTST | ビットフィールドテスト | |
| [x] | BFCHG | ビットフィールド変更 | |
| [x] | BFCLR | ビットフィールドクリア | |
| [x] | BFSET | ビットフィールドセット | |
| [x] | BFEXTU | ビットフィールド符号なし抽出 | |
| [x] | BFEXTS | ビットフィールド符号付き抽出 | |
| [x] | BFFFO | ビットフィールド先頭ビット検索 | |
| [x] | BFINS | ビットフィールド挿入 | |

### BCD 演算

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | ABCD | BCD 加算 | |
| [x] | SBCD | BCD 減算 | |
| [x] | NBCD | BCD 符号反転 | |
| [x] | PACK | BCD パック (68020+) | |
| [x] | UNPK | BCD アンパック (68020+) | |

### プログラム制御

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | BRA | 無条件分岐 (.B/.W/.L) | JIT (.B) |
| [x] | Bcc | 条件分岐 (.B/.W/.L)、全16条件 | JIT (.B) |
| [x] | BSR | サブルーチン呼出し (.B/.W/.L) | |
| [x] | DBcc | デクリメント＆分岐、全16条件 | |
| [x] | Scc | 条件セット、全16条件 | |
| [x] | JMP | ジャンプ | |
| [x] | JSR | サブルーチンジャンプ | |
| [x] | RTS | サブルーチンからの復帰 | |
| [x] | RTR | CCR 復帰付きリターン | |
| [x] | RTE | 例外からの復帰（スーパーバイザ） | |
| [x] | RTD | ディスプレースメント付きリターン (68010+) | |
| [x] | NOP | 無操作 | JIT |
| [x] | TRAPcc | 条件トラップ (68020+) | |

### レジスタ操作

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | SWAP | レジスタ上下ワード交換 | JIT |
| [x] | EXT.W | バイト→ワード符号拡張 | JIT |
| [x] | EXT.L | ワード→ロングワード符号拡張 | JIT |
| [x] | EXTB.L | バイト→ロングワード符号拡張 (68020+) | JIT |

### スタック・アドレス

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | PEA | 実効アドレスプッシュ | |
| [x] | LEA | 実効アドレスロード | |
| [x] | LINK.W / .L | リンク＆スタックフレーム確保 | |
| [x] | UNLK | アンリンク | |

### 例外・システム

| 状態 | 命令 | 説明 | JIT |
|------|------|------|-----|
| [x] | TRAP | ソフトウェアトラップ #0-#15 | |
| [x] | TRAPV | オーバーフロー時トラップ | |
| [x] | CHK.W / .L | レジスタ範囲チェック | |
| [x] | ILLEGAL | 不正命令例外強制 | |
| [x] | RESET | 外部デバイスリセット（スーパーバイザ） | |
| [x] | STOP | CPU 停止（スーパーバイザ） | |
| [x] | TAS | テスト＆セット（アトミック） | |
| [x] | CAS | コンペア＆スワップ (68020+) | |
| [x] | CAS2 | ダブルコンペア＆スワップ (68020+) | |

---

## FPU 命令 (MC68881/MC68882)

### データ転送

| 状態 | 命令 | 説明 |
|------|------|------|
| [x] | FMOVE | FP レジスタ ⇔ メモリ/レジスタ転送 |
| [x] | FMOVECR | 定数 ROM 転送 (pi, e, ln2 等) |
| [x] | FMOVEM | 複数 FP レジスタ転送 |
| [x] | FMOVE to/from FPCR/FPSR/FPIAR | FPU 制御レジスタ転送 |

### 算術演算

| 状態 | 命令 | 説明 |
|------|------|------|
| [x] | FADD | 加算 |
| [x] | FSUB | 減算 |
| [x] | FMUL | 乗算 |
| [x] | FDIV | 除算 |
| [x] | FMOD | 剰余（IEEE、被除数の符号） |
| [x] | FREM | IEEE 剰余 |
| [x] | FABS | 絶対値 |
| [x] | FNEG | 符号反転 |
| [x] | FSQRT | 平方根 |
| [x] | FSCALE | 2のべき乗スケーリング |
| [x] | FSGLDIV | 単精度除算 |
| [x] | FSGLMUL | 単精度乗算 |
| [x] | FINT | 整数への丸め |
| [x] | FINTRZ | ゼロ方向への整数丸め |
| [x] | FGETEXP | 指数部取得 |
| [x] | FGETMAN | 仮数部取得 |

### 三角関数

| 状態 | 命令 | 説明 |
|------|------|------|
| [x] | FSIN | 正弦 |
| [x] | FCOS | 余弦 |
| [x] | FSINCOS | 正弦・余弦同時計算 |
| [x] | FTAN | 正接 |
| [x] | FASIN | 逆正弦 |
| [x] | FACOS | 逆余弦 |
| [x] | FATAN | 逆正接 |

### 双曲線関数

| 状態 | 命令 | 説明 |
|------|------|------|
| [x] | FSINH | 双曲線正弦 |
| [x] | FCOSH | 双曲線余弦 |
| [x] | FTANH | 双曲線正接 |
| [x] | FATANH | 双曲線逆正接 |

### 指数・対数関数

| 状態 | 命令 | 説明 |
|------|------|------|
| [x] | FETOX | e^x |
| [x] | FETOXM1 | e^x - 1 |
| [x] | FTWOTOX | 2^x |
| [x] | FTENTOX | 10^x |
| [x] | FLOGN | 自然対数 |
| [x] | FLOGNP1 | ln(x+1) |
| [x] | FLOG10 | 常用対数 |
| [x] | FLOG2 | 2を底とする対数 |

### 比較・分岐

| 状態 | 命令 | 説明 |
|------|------|------|
| [x] | FCMP | 比較 |
| [x] | FTST | テスト |
| [x] | FBcc | FPU 条件分岐（全32条件コード） |
| [x] | FScc | FPU 条件セット |
| [x] | FDBcc | FPU 条件デクリメント＆分岐 |
| [x] | FTRAPcc | FPU 条件トラップ |

### FPU 状態

| 状態 | 命令 | 説明 |
|------|------|------|
| [x] | FSAVE | FPU 状態保存（スーパーバイザ） |
| [x] | FRESTORE | FPU 状態復帰（スーパーバイザ） |

---

## MMU 命令 (MC68030 オンチップ)

| 状態 | 命令 | 説明 |
|------|------|------|
| [x] | PMOVE | MMU レジスタ転送 (TC, TT0, TT1, SRP, CRP, MMUSR) |
| [x] | PFLUSH | TLB エントリフラッシュ（FC とマスク指定） |
| [x] | PFLUSHA | 全 TLB エントリフラッシュ |
| [x] | PLOAD | TLB エントリプリロード (PLOADR/PLOADW) |
| [x] | PTEST | アドレス変換テスト |

---

## 未実装

| 命令 | 説明 | 備考 |
|------|------|------|
| Line-A (0xAxxx) | Line-A エミュレータトラップ | 例外10を発生（設計通り） |
| CALLM / RTM | モジュール呼出し/復帰 (68020) | 68030 で削除済み；不要 |
| cpBcc / cpDBcc / cpScc / cpTRAPcc | コプロセッサ分岐/セット (ID >= 2) | 例外11を発生 (Line-F) |
| LPSTOP | 低消費電力停止 (68060) | 68060 専用命令 |

---

## JIT まとめ

**レジスタ専用**命令（メモリアクセスなし）の基本ブロックを JIT コンパイル。
31 種の命令パターンに対応:

| カテゴリ | 命令 |
|---------|------|
| 転送 | MOVEQ, MOVE.L Dn->Dm, MOVE.L An->Dn, MOVEA.L Dn->An, MOVEA.L An->Am |
| 算術 | ADD.L, SUB.L, CMP.L (Dn,Dm), ADDQ/SUBQ (.L Dn, An), NEG.L Dn |
| 論理 | AND.L, OR.L, EOR.L (Dn,Dm), NOT.L Dn, CLR.L Dn, TST.L Dn |
| シフト | ASL/ASR/LSL/LSR.L #imm,Dn |
| レジスタ | EXG（全3形式）, SWAP Dn, EXT.W/EXT.L/EXTB.L Dn |
| 分岐 | BRA.B, Bcc.B（全条件） |
| その他 | NOP |

### C# JIT 実装詳細

- System.Reflection.Emit による IL コード生成 (DynamicMethod)
- コンパイル時に各オペコードのサイクルコストを合算し CompiledBlock.TotalCycles に格納
- サンプリング間隔 TickInterval (256)、コンパイル閾値 = 16 ヒット
- ExecuteNextFast() と ExecuteNextFastJit() は完全分離（C++ と同様の設計）
- メソッド選択は bool 分岐。Func<bool> デリゲートディスパッチは 36→30 MHz 回帰を
  引き起こしたため不採用（定方向分岐は分岐予測によりほぼゼロコスト）
- JIT ON は現時点で JIT OFF より遅い (33 vs 36 MIPS): NoInlining メソッド呼出の
  per-instruction オーバーヘッドが、コンパイル可能ブロックの希少さを上回る
- テスト: JIT 専用テスト (Em68030.Tests/)
