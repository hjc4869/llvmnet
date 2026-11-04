#!/usr/bin/env python3
"""Generate docs/user/limitations.md, the one page of what llvmnet does not do (TC-17).

Two parts come from code: the LLVM constructs the compiler rejects, from the generated support matrix (BE-02,
docs/user/support-matrix.md), and the system services the portable host refuses by name, from the runtime's own table
(HostSyscalls.Unavailable, printed by "llvmnet --print-host-limitations"). The rest is the fixed text below, kept here
so that the page is produced by one command: tests/limitations.sh regenerates it and fails when it is out of date.

Usage: limitations.py <llvmnet> <support-matrix.md> <output.md>
"""
import re
import subprocess
import sys

HEADER = """# Limitations

What llvmnet does not do, in one place. The two tables are generated from code: the compiler's rejected constructs from the probes behind [support-matrix.md](support-matrix.md), and the portable host's refused system calls from the runtime's own table; `scripts/limitations.py` writes this page and `tests/limitations.sh` keeps it current. Everything else here is the documented state of the 0.x releases; the plan items in parentheses track the open ones.
"""

COMPILER = """## Compiler

Programs are compiled through Clang and Flang 22 and translated from LLVM IR to CIL; what the frontends reject is theirs to report. The backend rejects the constructs below with a compile error naming the function and the instruction ([driver.md](driver.md#exit-status)); everything else in the support matrix is translated.

{rejected}

Further limits of the translation:

- Vector IR (LLVM's vectorizers with `-fvectorize`, which stay off by default (ADR-11), and Clang's vector extensions) runs as `Vector128` code at 128 bits, wider vectors as 128-bit fragments (ADR-16 Level 1). Functions whose vectors have lanes of no CIL vector type, or that use vector intrinsics without a lowering yet, and functions that use vectors mostly as containers run as scalar code. The x86 target intrinsics of SSE to SSE4.2 and PCLMULQDQ run as runtime helpers, the host's instruction or an exact composition (Level 2, [x86-intrinsics.md](x86-intrinsics.md)); AArch64 and WebAssembly target intrinsics are not translated yet, and the opt-in `--simd128` header path maps `wasm_simd128.h` intrinsics to `Vector128` ([simd128.md](simd128.md)).
- x86 target intrinsics beyond SSE4.2 and PCLMULQDQ (AVX and later, AES), the MXCSR write (`_mm_setcsr` and the `_MM_SET_*` macros) and `monitor`/`mwait` are not translated: compiling a unit that contains one warns, and linking a program that reaches it fails. Portable code sees the x86 macros only with `--isa=x86-64-v2` or its `-m` aliases; libraries' CPUID detection works there and in system mode ([x86-intrinsics.md](x86-intrinsics.md#cpu-detection)), but the portable target has no `ifunc` attribute (Clang ignores it), so only `target_clones` dispatches there. System mode predefines `__SSE__` and `__SSE2__` as native x86-64 does ([modes.md](modes.md#system-abi)).
- `longjmp` cannot cross native frames (system mode), and an external thread-local global needs a bitcode definition (SA-03).
- A `musttail` call (`[[clang::musttail]]`) becomes a CIL `tail.` call and runs in constant stack under the JIT and NativeAOT; a function that makes one takes its allocas and variadic call area from llvmnet's per-thread arena instead of the CLR stack, because NativeAOT reuses the frame only when it has no `localloc`. The `musttail` calls in the table above are rejected: a `byval` argument, an argument or result of more than 16 bytes (which Windows x64 and ARM64 pass by reference to a copy in the caller's frame), and forwarded variadic arguments. Windows x64 also passes 16-byte vectors, `long double` and 128-bit integers by reference, so there a NativeAOT executable makes a `musttail` call with one an ordinary call (the JIT keeps the guarantee through its tail-call helpers). In system mode a `musttail` call to a native function is an ordinary call. Ordinary `tail` calls stay ordinary calls: the prefix would keep RyuJIT from inlining the callee.
- A function with very many aggregate values or `setjmp` sites can exceed the CLR's limits on locals, and a very large function runs without JIT optimization; `--diagnose-limits` reports both (BE-25, BE-26).
- Emitted assemblies carry no debug information yet, so stack traces show LLVM symbol names without source lines; `-g` gives compiler diagnostics a `file:line:col` location (BE-28).
- Native code cannot call a translated variadic or aggregate callback (system mode, SA-02).
- A conservative garbage collector that finds its roots by scanning the C stack and the registers `setjmp` saved (Boehm GC, SIOD and similar interpreters) does not see pointers that translated code keeps in CIL locals and evaluation-stack slots, so it can free live objects; such a collector needs its roots registered explicitly (found by llvm-test-suite's SIOD, WG-01).
"""

PORTABLE = """## Portable mode

A portable program (`dotnet64-v2`) contains musl, libc++ and flang-rt, and reaches the operating system through the host layer in `LlvmNet.Runtime`, which implements Linux system-call semantics over .NET ([dev/host-abi.md](../dev/host-abi.md)). The services .NET cannot provide are refused with the errno below; a number the host does not implement at all returns `ENOSYS` without a name.

{unavailable}

Behavior that differs from a Linux kernel, in short ([dev/host-abi.md](../dev/host-abi.md#behavior-that-differs-from-a-linux-kernel) has the full list):

- Signals raised inside the process are delivered synchronously on the raising thread; `SIGINT`, `SIGTERM`, `SIGHUP` and `SIGQUIT` from outside reach a handler on a .NET signal thread; faults such as `SIGSEGV` are not delivered, the CLR reports them.
- Child processes are host processes that inherit only the standard descriptors; `system` and `popen` need `/bin/sh` on the host.
- File mappings are copies written back at `msync` and `munmap`; there is no memory protection.
- The process runs as user and group 1000 and owns every file it can see; inode numbers are hashes of the path.
- Descriptor-relative calls (`openat` and the other `*at` calls) resolve against the directory's path, so they are not safe against a directory being replaced between calls.
- Only the C and C.UTF-8 locales exist; the floating-point environment is fixed to round-to-nearest without exception flags.
- `__linux__`, `__unix__` and the other host macros are not defined (ADR-14), nor `__x86_64__` unless `--isa=x86-64-v2` is given; a program that selects platform code by them needs a portable path or a documented per-program flag.
- Thread stacks are at least 8 MB and `CLOCK_THREAD_CPUTIME_ID` reports process time.
- Native libraries are reached only through native extensions ([extensions.md](extensions.md)), which pass integers, floating point, pointers and structures by value without unions or bit-fields; Vulkan's window-system extensions, variadic functions and C++ interfaces are not proxied, a callback on a thread the host library started gets thread state without thread-local or key destructors, and `dlopen` serves only the libraries of the linked extensions.
"""

HOSTS = """## Hosts other than Linux

The same image runs on Linux x64 and ARM64, Windows x64 and macOS ARM64 ([deployment.md](deployment.md#supported-hosts)); macOS and ARM64 behave like Linux x64 in every host-interface check. On Windows the file system and the shell differ ([dev/host-abi.md](../dev/host-abi.md#hosts-other-than-linux)):

- Paths: a drive is a root directory named by its letter (`/c/Users/...`), `/tmp` is the temporary directory, the file system is case-insensitive.
- Permissions: there are no mode bits; `stat` reports `0644` or `0755`, `chmod` sets or clears the read-only attribute, and no directory is unsearchable.
- Symbolic links need the create-symbolic-link privilege (Developer Mode or an elevated process).
- `system` and `popen` need `sh.exe` from Git for Windows on `PATH`.
- Time-zone abbreviations from generated zoneinfo images are numeric (`+01`).

The compiler itself runs on Linux x64 only (ADR-05): NativeAOT executables for Windows and macOS are published on those hosts from the project `--aot-project` writes.
"""

SYSTEM = """## System mode

- Linux x86-64 only (`system-linux-x64-v1`); a Linux ARM64 system ABI is post-1.0 (SA-08).
- Native code cannot call translated variadic or aggregate callbacks (SA-02); thread cancellation and `pthread_exit` across managed frames (SA-05), signal handlers in translated code (SA-06), external native TLS globals (SA-03) and callbacks on threads created by native libraries (SA-04) are incomplete.
- No native C++ library interchange: C++ library code must be linked as bitcode (the optional libstdc++ archive, or libc++ in portable mode).
- `fork`, `vfork`, `_Fork` and `forkpty` fail with `ENOSYS` under the JIT, whose W^X protection maps generated code from shared memory that a child would share with its parent; NativeAOT executables, and the JIT with `DOTNET_EnableWriteXorExecute=0`, call the host's `fork`, and the child should do no more than a multithreaded program's child may before `exec`. `posix_spawn`, `system` and `popen` work in every case ([modes.md](modes.md#system-abi)).
- OpenMP needs a staged LLVM OpenMP runtime on the loader path (`scripts/install-system-openmp.sh`).
- `-static` is not accepted: the host's C library and native libraries are reached through generated bridges.
"""

WORKLOADS = """## Workloads

- ffmpeg's FATE suite is not run; the ffmpeg profile is checked on a fixture set and, locally, on private media (WG-04).
- SPEC CPU runs use explicitly selected inputs and are not reportable results; the portable-mode exclusions (`fork`, glibc-specific behavior, prototypes that contradict the definition) are listed in the SPEC evidence (ADR-14).
"""


def rejected_rows(matrix):
    """The rejected rows of the support matrix, grouped by its sections."""
    sections, current = [], None
    for line in matrix.splitlines():
        if line.startswith("## "):
            current = (line[3:].strip(), [])
            sections.append(current)
        elif current and line.startswith("| `") and "| rejected |" in line:
            cells = [cell.strip() for cell in line.strip("|").split("|")]
            current[1].append((cells[0], cells[2]))
    lines = ["| Construct | Diagnostic |", "| --- | --- |"]
    for name, rows in sections:
        for construct, diagnostic in rows:
            lines.append(f"| {name}: {construct} | {diagnostic} |")
    count = sum(len(rows) for _, rows in sections)
    total = re.search(r"(\d+) of (\d+) constructs are translated", matrix)
    summary = f"{count} of the {total.group(2)} probed constructs are rejected ({total.group(1)} translated)." if total else f"{count} constructs are rejected."
    return summary + "\n\n" + "\n".join(lines)


def unavailable_rows(table):
    lines = ["| Area | Calls | Result | Why |", "| --- | --- | --- | --- |"]
    for line in table.splitlines():
        area, calls, errno, reason = line.split("\t")
        lines.append(f"| {area} | {calls} | `{errno}` | {reason} |")
    return "\n".join(lines)


def main():
    llvmnet, matrix_path, output = sys.argv[1:4]
    with open(matrix_path) as handle:
        matrix = handle.read()
    table = subprocess.run([llvmnet, "--print-host-limitations"], check=True, capture_output=True, text=True).stdout
    page = "\n".join([HEADER, COMPILER.format(rejected=rejected_rows(matrix)), PORTABLE.format(unavailable=unavailable_rows(table)), HOSTS, SYSTEM, WORKLOADS])
    with open(output, "w") as handle:
        handle.write(page)
    return 0


if __name__ == "__main__":
    sys.exit(main())
