# Contributing to Em68030_CsWPF

Thanks for your interest!  This is the **C# / WPF** port of the MC68030
emulator targeting the MVME147 single-board computer.

## Getting the source

```bash
git clone https://github.com/hha0x617/Em68030_CsWPF.git
```

## Build prerequisites

- **.NET 8 SDK** ([download](https://dotnet.microsoft.com/download/dotnet/8.0))
- Windows 10 1809 or later (for running the app)

## Building

```bash
# Debug build
dotnet build Em68030/Em68030.csproj

# Release build
dotnet build Em68030/Em68030.csproj -c Release

# Run the tests
dotnet test Em68030.Tests/Em68030.Tests.csproj -c Release
```

## Running the app

```bash
dotnet run --project Em68030/Em68030.csproj -c Release
```

## Making a change

1. Fork the repository and create a feature branch off `main`.
2. Keep commits focused; write commit messages that explain the *why*.
3. Add or update tests (xUnit under `Em68030.Tests/`) for behaviour
   changes.
4. Open a pull request against `main`.  CI must pass before merge.

## Commit style

- Subject line ≤ 72 chars, imperative mood, optional `type(scope):` prefix
  (`feat(cpu):`, `fix(mmu):`, `docs:`, `ci:`, `chore:`).
- Body wrapped to 72 chars, focused on motivation and trade-offs.
- For CPU correctness fixes, include the failing case (instruction,
  operands, expected vs observed register / memory state) so reviewers
  can reproduce it quickly.

## Reporting bugs / requesting features

Use the issue templates in [`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE/).
Security vulnerabilities go through [`SECURITY.md`](SECURITY.md) instead.

## Parallel C++ port

A C++/WinRT + WinUI 3 port lives at
[Em68030_WinUI3Cpp](https://github.com/hha0x617/Em68030_WinUI3Cpp).  The
two share the same MC68030 ISA semantics and MVME147 device set — fixes
to the CPU/devices in one port usually want a mirror PR in the other.

## License

By submitting a contribution you agree it will be licensed under the
**Apache-2.0** terms as the rest of the repository.
