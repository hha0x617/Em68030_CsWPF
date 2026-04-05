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

- The emulator's status bar shows ~217 MHz (estimated cycle-based clock). This is an internal metric based on approximate cycle counting and is not directly comparable to DMIPS, which measures application-level performance including memory access, function calls, and string operations.
- Compiled without `register` attribute as reported by the benchmark output.
- The C# version achieves approximately 70% of the C++ version's DMIPS score (55.3 vs 79.1), consistent with the MHz ratio (~217 vs ~270 MHz).

---

## CoreMark 1.0

Measured using [eembc/coremark](https://github.com/eembc/coremark) on NetBSD/mvme68k 10.1 running on the Em68030 emulator.

### Prerequisites

Install `git` and `gmake` on the guest (NetBSD's default `make` is not compatible with CoreMark's Makefile):

```sh
pkg_add git gmake
```

### Build

```sh
git clone https://github.com/eembc/coremark.git
cd coremark
gmake PORT_DIR=posix CC=gcc XCFLAGS="-O2 -m68030" RECURSE_OUT=1
```

### Run

```sh
./coremark.exe 0 0 0 5000
```

### Result

| Item | Value |
|------|-------|
| Iterations | 5,000 |
| Total time (secs) | 44.301 |
| **CoreMark score** | **112.86** |
| Compiler | GCC 10.5.0 |
| Compiler flags | `-O2 -m68030 -DPERFORMANCE_RUN=1 -lrt` |
| Memory location | Heap |

CoreMark score = Iterations / Total time.

**Official reporting format:**
```
CoreMark 1.0 : 112.864269 / GCC10.5.0 -O2 -O2 -m68030 -DPERFORMANCE_RUN=1 -lrt / Heap
```

### Host Environment

| Item | Value |
|------|-------|
| Emulator | Em68030 (C# / WPF) |
| JIT | OFF |
| Host CPU | Intel Core i7-13700 |
| Host OS | Windows 11 Pro 25H2 |

### Comparison

| System | CoreMark | Notes |
|--------|----------|-------|
| MC68030 25 MHz (real hardware) | ~10-20 | Approximate, varies by implementation |
| Em68030 C++ on i7-13700 | 155.55 | ~8-15x faster than real MC68030 25 MHz |
| **Em68030 C# on i7-13700** | **112.86** | ~73% of C++ version |
| Raspberry Pi 1 (ARM1176, 700 MHz) | ~1,073 | For reference |
