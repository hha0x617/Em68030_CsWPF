# Benchmark

## Dhrystone 2.1

Measured using [Keith-S-Thompson/dhrystone](https://github.com/Keith-S-Thompson/dhrystone) on NetBSD/mvme68k 10.1 running on the Em68030 emulator.

### Build

```bash
ftp -o dhry.tar.gz https://github.com/Keith-S-Thompson/dhrystone/archive/refs/heads/master.tar.gz
tar xzf dhry.tar.gz
cd dhrystone-master/v2.1
cc -O2 -o dhrystone dhry_1.c dhry_2.c -DTIME
```

### Result

| Item | Value |
|------|-------|
| Runs | 10,000,000 |
| Microseconds per run | 10.3 |
| Dhrystones per second | 97,087 |
| **DMIPS** | **55.3** |

DMIPS = Dhrystones/sec / 1757 (normalized to VAX 11/780 = 1.0 DMIPS).

### Host Environment

| Item | Value |
|------|-------|
| Emulator | Em68030 (C# / WPF) |
| JIT | OFF |
| Host CPU | Intel Core i7-13700 |
| Host OS | Windows 11 Pro 25H2 |

Emulation speed depends on the host PC performance. Results will vary on different hardware.

### Comparison

| System | DMIPS | Notes |
|--------|-------|-------|
| VAX 11/780 (1979) | 1.0 | Reference baseline |
| MC68030 25 MHz (real hardware) | ~5-8 | Approximate, varies by implementation |
| **Em68030 C# on i7-13700** | **55.3** | ~7-11x faster than real MC68030 25 MHz |
| **Em68030 C++ on i7-13700** | **79.1** | ~10-15x faster than real MC68030 25 MHz |
| Raspberry Pi 1 (ARM1176, 700 MHz) | ~875 | For reference |

### Notes

- The emulator's status bar shows ~32 MIPS (instruction throughput) and ~217 MHz (estimated cycle-based clock). These are internal metrics and not directly comparable to DMIPS, which measures application-level performance including memory access, function calls, and string operations.
- Compiled without `register` attribute as reported by the benchmark output.
- The C# version achieves approximately 70% of the C++ version's DMIPS score (55.3 vs 79.1), consistent with the internal MIPS ratio (~32 vs ~44 MIPS).
