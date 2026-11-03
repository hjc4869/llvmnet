# Evidence: PF-19, vectored I/O in one host call (2026-11-03)

Host: AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB, Linux 6.12.113, .NET 10.0.14, clang 22.1.8; base commit `262c643`. The benchmark comparisons alternate the builds they compare on the same host, one process at a time.

## The nightly failure

The 2026-11-02 nightly on GitHub CI (commit `95cf6bb`, an ubuntu-24.04 runner with an Azure 7.0 kernel and 4 cores, in a `debian:trixie` container) failed the PF-01 thresholds on `fwrite`: 6.76 times its baseline under the JIT and 9.13 times as NativeAOT, 6.4 s against 1.0 s native on the runner. On this host at the same code, `fwrite` matched the baseline (0.99 and 0.92), so nothing had regressed: the runner charges far more than this host for what the program asks of the host layer.

## The cause

musl's stdio hands the kernel two vectors per transfer. `__stdio_write` flushes with `writev(fd, {buffered bytes, new data}, 2)`, and `__stdio_read` refills with `readv(fd, {caller's buffer, length - 1}, {FILE buffer, 4096})`. Natively each is one system call. The host layer's `Vector()` (`src/LlvmNet.Runtime/Host/HostFiles.cs`) called `Write` or `Read` once per vector, so every flush of the benchmark's 256-byte `fwrite` loop became a 4,096-byte write and a 256-byte write, each 4,096-byte write after the first started 256 bytes further into a page than the one before, and every 64 KiB `fread` became two reads.

`strace -f -c` of `tests/perf/fwrite.c` (1 GiB written in 256-byte `fwrite` calls, then read back in 64 KiB `fread` calls):

| build | writes | reads |
| --- | ---: | ---: |
| native glibc | 262,145 `write` (4,096 bytes) | 16,387 `read` |
| NativeAOT, before | 493,447 `pwrite64` (2,176 bytes on average) | 32,773 `pread64` |
| NativeAOT, after | 246,724 `pwrite64` (4,352 bytes) | 16,390 `pread64` |

After the change there is one host write per musl flush (1 GiB / 4,352 bytes = 246,724), as native musl's `writev` makes; glibc fills its buffer before writing and so writes 4,096-byte blocks. The reads are glibc's count: after the first, each `fread` asks for 61,440 bytes into the caller's buffer and 4,096 into the FILE buffer, 65,536 in all, which is one host read now.

The split also broke Linux semantics that do not depend on speed:

- A `writev` of at most `PIPE_BUF` bytes to a pipe was not atomic. Two threads writing 1,000-byte records as a 1-byte header and a 999-byte body tore records in 20 of 20 trials of a standalone probe; the check is now in `tests/c/host-errors.c`.
- A non-blocking `writev` of 200 bytes into a pipe with 100 bytes free wrote the first vector and returned 100; Linux returns `EAGAIN`. Above `PIPE_BUF` the count was the vectors that fitted whole (4,000 of 12,000 bytes into 4,096 free) instead of what fits (4,096).
- A `readv` from a blocking pipe whose bytes exactly filled the first vectors waited for more data for the later vectors; Linux returns what the pipe holds.
- `readv` with no vectors on a closed descriptor returned 0 instead of `EBADF`.

## The change

`Vector()` treats the vectors as one transfer. A run of vectors of up to 64 KiB in all goes through a buffer from `ArrayPool<byte>.Shared`: gathered and written with one host call, or read with one host call and scattered. A run with a single non-empty vector is transferred in place, without the copy. Longer transfers go in runs of vectors that fit the buffer (a longer vector by itself) and end at the first short count. Reads go past the first run only on regular files and devices, which never block, so a pipe or terminal read returns what is there. A transfer of zero bytes still checks the descriptor. Errors, `EAGAIN`, `EPIPE` and `SIGPIPE` come from the one host call as before, and an error after some bytes were transferred returns their count, as before.

So a pipe write of at most `PIPE_BUF` bytes is one `Pipe.Write`, which waits for room for all of it or returns `EAGAIN`, as in Linux.

**Copying reads.** Scattering a 64 KiB read copies it once more. A variant that read vectors of 32 KiB or more in place (two host calls per `fread`, no copy) ran within noise of the copying version on this host (medians of 7 alternating runs, in place against copying: NativeAOT 0.500 and 0.486 s, JIT 0.566 and 0.583 s). So one limit serves both directions, and every `fread` of the benchmark costs one host call, which is what the CI runner needs.

## Conformance

`tests/c/host-errors.c` gains `vectors()`, 31 checks that `tests/host.sh` runs natively and diffs under the JIT and NativeAOT (229 checks in all, all identical to native):

- files: `writev` of 3, 70,000 and 5 bytes and of 40 vectors of 2,000 bytes (above the limit, in runs), `readv` of 65,535 and 4,096 bytes (musl's first refill) and of 10, 100 and 1,000 bytes, `preadv` across the end of the file, `pwritev`, positions after each, zero vectors, a bad descriptor, 1,025 vectors, a `SIZE_MAX` vector, and 71,000 bytes from `/dev/zero` in two vectors (a device read continues past the first run);
- pipes, sized so that Linux's page-granular pipe accounting and the host's byte count agree: `EAGAIN` for 200 bytes into 100 free, 100 bytes into 100 free, 4,096 of 12,000 bytes into one free page, a `readv` split over two vectors, a `readv` that returns what the pipe holds, `preadv` on a pipe (`ESPIPE`), `EPIPE` without a reader, and two threads writing 100 records each as two-vector `writev` calls whose records must arrive whole.

The same program with the old `LlvmNet.Runtime.dll` differs from native on the bad descriptor, on both `EAGAIN` checks, on the partial count, and then blocks in the pipe `readv` until the timeout.

## Timing on this host

`python3 scripts/benchmark.py --only fwrite --repeats 9`, run four times back to back: twice with the runtime at `262c643` and twice with the change, alternating, everything else the same. "vs baseline" is the harness's host-normalized time over `tests/perf/baseline.tsv` (1.00 = unchanged; it fails above 1.25):

| run | runtime | native (s) | JIT (s) | JIT vs baseline | NativeAOT (s) | NativeAOT vs baseline |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | before | 0.368 | 0.610 | 1.04 | 0.533 | 1.00 |
| 1 | after | 0.367 | 0.586 | 0.99 | 0.491 | 0.91 |
| 2 | before | 0.383 | 0.627 | 1.05 | 0.547 | 1.01 |
| 2 | after | 0.363 | 0.573 | 1.00 | 0.491 | 0.94 |

The run the item asks for, `python3 scripts/benchmark.py --only fwrite --repeats 5` with the change, gave native 0.363 s, JIT 0.578 s (1.02 against the baseline) and NativeAOT 0.501 s (0.98). Native `fwrite` ran faster on this host than when the baseline was written (fastest 0.350 s against 0.419 s), and the harness normalizes by it, so the absolute times sit 17 to 18% below the baseline's while the normalized ones move by a few percent between runs.

`scripts/ab-time.py 10` with the same programs and only the runtime differing (median seconds, host load 3 to 5):

| program | before | after |
| --- | ---: | ---: |
| native | 0.401 | |
| JIT | 0.656 | 0.629 |
| NativeAOT | 0.576 | 0.549 |

The JIT build's host calls (`strace -f -c dotnet fwrite.dll`) moved the same way: `pwrite64` from 493,447 to 246,724 and `pread64` from 32,807 to 16,424 (the rest are the runtime's own reads).

On the CI runner the same program took 6.4 s against 1.0 s native before the change, far more per host call than here, so halving the calls should matter more there. That is confirmed only when the nightly benchmark (or `scripts/benchmark.py --only fwrite`) runs on CI after the push.

## Gates

Run in a `systemd-run --user --scope -p MemoryMax=32G -p MemorySwapMax=0` scope:

- `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 4`: 107 passed, 0 failed, in 466 s.
- `tests/libc-conformance.sh`: 385 of 403 counted tests pass (functional 60 of 67, regression 59 of 67, math 192 of 195, api 74 of 74). The 18 failures are the same tests as in a run on `master` earlier the same day (`fork`, `vfork`, `exec`, sockets, System V IPC, `sem_open`, `sigaltstack`, thread cancellation, `fenv` and three `long double` cases).
