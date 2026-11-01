# Security

## What llvmnet is not

llvmnet translates C, C++ and Fortran into .NET assemblies that keep the source's memory model: raw pointers, pointer arithmetic, unchecked casts, `localloc` and indirect calls through function pointers. The emitted IL is unverifiable by design. A translated program therefore has the same memory-safety properties as its native build. An out-of-bounds write corrupts the process, a use-after-free reads freed memory, and an invalid pointer ends the process or, at worst, is exploitable as it would be natively.

- **It is not a sandbox.** Running a program under .NET does not confine it. In portable mode the host layer (`LlvmNet.Runtime`) opens files, creates threads and starts processes with the permissions of the user who runs the program; in system mode the program calls native libraries directly. Neither mode isolates the program from the machine, the user's data or other code in the same process.
- **It does not add checks the source lacks.** Bounds, lifetimes and integer overflow behave as the C, C++ or Fortran code and its compiler options define them.
- **Compiling is not running, but it is not inert either.** The compiler runs Clang and Flang on the input and, for NativeAOT, the .NET SDK and a native linker. Treat source code, build scripts and `-Xclang`-style frontend options from untrusted origins as you would for any native compiler.

Compile and run only code you would trust as a native executable.

## What counts as a vulnerability

Report these privately:

- The compiler, the runtime or the host layer making a correct program unsafe: generated code that writes outside the memory the source addresses, a host function that grants access the corresponding system call would not (for example following a path the caller did not name), or a runtime check that the documentation promises and that does not happen.
- A way for one program's input to change what the compiler emits for another, or for a crafted object, archive or managed reference to make the compiler write outside its output paths.
- A release artifact whose checksum or signature does not verify ([docs/dev/release-process.md](docs/dev/release-process.md#verifying-a-download)).

A memory-safety bug in a translated program that the native build of the same source also has is a bug in that program, not in llvmnet.

## Reporting

Use the repository's private vulnerability reporting (GitHub: **Security**, then **Report a vulnerability**). Include the llvmnet version (`llvmnet --version`), the runtime mode, a minimal source and command line, and what you observed. Please allow time for a fix before disclosing publicly.

## Supported versions

Security fixes go to the latest release. The support policy, which versions receive fixes and for how long, is in [docs/user/support.md](docs/user/support.md).
