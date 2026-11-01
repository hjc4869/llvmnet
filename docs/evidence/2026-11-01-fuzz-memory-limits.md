# Evidence: memory limits for differential fuzzing (2026-11-01)

Build host: Linux x86-64 (Debian 13, kernel 6.12, transparent huge pages `always`), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, no swap, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang 22.1.8. Plan item FD-16, `tests/fuzz.sh`. Every number below comes from the command named with it.

## The incident

On 2026-11-01, `tests/run.sh libc-conformance fuzz` ran the fuzz test next to libc-test with the default of `nproc / 2` = 16 jobs. Together they exhausted the host's memory, and the kernel's OOM killer stopped the desktop session that had started the run. A second attempt inside `systemd-run --user --scope -p MemoryMax=40G -p MemorySwapMax=0` was killed with one native YARPGen program at 41 GB. The script limited only the time a program could run (`timeout 10` natively, `timeout 300` under `dotnet`), not its memory. The nightly runner has 16 GB.

## How much memory YARPGen programs need

YARPGen's C programs keep their state in global arrays, which the native build places in `.lbss`; this is why the script builds them with `-mcmodel=large`. Static data of seeds 1 to 100, built as the fuzz test builds them (`yarpgen --std=c --seed=N`, then `clang-22 -O(N%4) -w -mcmodel=large driver.c func.c`, sizes from `llvm-size-22`):

| Static data | Seeds |
| --- | ---: |
| under 512 MB | 73 |
| 512 MB to 2 GB | 5 |
| 2 to 4 GB | 1 |
| 4 to 16 GB | 8 |
| 16 to 100 GB | 8 |
| over 100 GB (up to 473 GB, seed 100) | 5 |

A running program touches every array, so its peak resident set is its static data. Measured with GNU `time -f %M`, each program in its own 12 GB scope:

| Seed | Static data (MB) | Native peak RSS (MB) | Elapsed |
| ---: | ---: | ---: | ---: |
| 40 | 1,080 | 1,082 | 0.84 s |
| 43 | 1,197 | 1,199 | 0.08 s |
| 77 | 1,828 | 1,829 | 0.26 s |
| 41 | 3,094 | 3,096 | 0.89 s |
| 32 | 5,196 | 5,197 | 5.79 s |

The llvmnet builds keep globals in lazily zeroed `calloc` blocks (`Memory.AllocateGlobal`), the way an ELF `.bss` is kept, so they need the native amount plus the runtime. Across the 78 seeds under 2 GB, measured the same way for the native, system-mode and portable-mode builds (`dotnet program.dll`), the llvmnet build needed at most 50 MB more than native in either mode. The largest is seed 77: 1,830 MB native, 1,871 MB system, 1,877 MB portable. Every output matched native.

## Choosing a mechanism

- **`ulimit -v` (`RLIMIT_AS`)** is exact for native programs but cannot limit `dotnet`. Without a limit the runtime reserves about 256 GB of address space (`VmPeak` 260,784 MB for a hello world). Under a limit it falls back to smaller reservations, but it still needs far more address space than it touches, and the gap is not a constant that could be added to the limit. A hello world (37 MB resident) runs from `ulimit -v` 1,536 MB up. YARPGen seed 43 (1,263 MB resident in system mode, 1,277 MB in portable mode) needs 5,888 MB, or 5,376 to 5,632 MB with `DOTNET_GCHeapHardLimit=0x20000000`. Under too low a limit the program fails with `System.OutOfMemoryException` in its module initializer (exit 134). That cannot be told apart from a crash, which the fuzz test must report as an llvmnet failure.
- **cgroups** (`systemd-run --user --scope -p MemoryMax=`) limit resident memory exactly, and local runs use them around the whole test. Per program, though, they need a user systemd. In a `debian:trixie` container run as root, as CI runs, `/sys/fs/cgroup` is mounted read-only (`mkdir: cannot create directory '/sys/fs/cgroup/fuzz': Read-only file system`) and `/run/systemd/system` does not exist.
- **A watchdog** works wherever `/proc` does, so this is what the script uses. `run_limited` in `tests/fuzz.sh` starts the program and reads its resident set from `/proc/<pid>/statm` every 20 ms. Between checks it sleeps with `read -t` on a pipe, so the loop starts no processes. It kills the program with `SIGKILL` when it passes the memory limit or the time limit, and records which; it replaces `timeout` for the program runs. Checks of the function on its own: `true` returns in 2 ms (100 runs in 0.11 s); `exit 3` keeps its status; `sleep 5` with a 1 s limit is stopped at 1.8 s as a timeout; `tail /dev/zero` is stopped as over the limit after 0.05 s at 200 MB and after 0.3 s at 2,048 MB. Transparent huge pages fault memory in at up to about 15 GB/s (seed 43 touched 1.2 GB in 0.08 s), so a program can pass the limit by up to about 300 MB before the next check.

A host that cannot map a program's static data kills the program at start: seed 100, with 473 GB of `.lbss`, exits with status 139 on this host before running any code. So the script also checks the native build's static data (data plus bss from `llvm-size-22`) before running it. This also makes the skip decision for these seeds the same on every host.

## What the script does now

- Natively: a program whose static data is over `FUZZ_MEMORY_MB` (default 2,048) is skipped without running (`over the memory limit: 40040 MB of static data`). One that grows past the limit while running is stopped and skipped (`over the memory limit: more than 2048 MB resident`). One that runs out of its 10 s, fails or does not build is skipped with that reason, where all of these used to be `native did not finish`. The summary line counts the memory skips.
- Under llvmnet: each build runs with 300 s and `FUZZ_MEMORY_MB` + 512 MB, ten times the 50 MB the runtime was measured to need. Since the native program fitted in `FUZZ_MEMORY_MB`, a build over its limit fails as `memory (over 2560 MB)` and is kept with a `repro.sh`, like a timeout.
- `JOBS` defaults to half the processors, at most four (it was half the processors). The nightly runner keeps its two jobs; this host goes from 16 to 4 unless `JOBS` is set.
- Budget: per job, at most a program at its limit plus about 300 MB before the next check (2.4 GB native, 2.9 GB under llvmnet), or one llvmnet compile (714 to 854 MB in GNU `time` for YARPGen seeds 2 and 77 in either mode). With the defaults a run stays under about `JOBS` × 3 GB: 6 GB on the 4-core, 16 GB nightly runner, 12 GB with four jobs.
- Why 2,048 MB: it skips 22 of YARPGen seeds 1 to 100. A 4,096 MB limit would skip 21 (only seed 41, at 3 GB, lies between) and double the worst case; a 1,024 MB limit would skip 26.

## Validation

Each run in its own `systemd-run --user --scope -p MemoryMax=24G -p MemorySwapMax=0`, with the peak read from the scope's `MemoryPeak`:

| Run | Wall time | Scope peak | Result |
| --- | ---: | ---: | --- |
| `FUZZ_SEEDS=100 FUZZ_GENERATORS=yarpgen JOBS=2 tests/fuzz.sh` | 189 s | 2,577 MB | 22 skipped, all over the memory limit by static data (3,094 MB to 473,070 MB: seeds 1, 5, 9, 11, 15, 21, 24, 26, 32, 35, 41, 52, 57, 58, 60, 67, 72, 86, 88, 94, 95, 100); 78 matched in both modes; PASS |
| `FUZZ_SEEDS=200 FUZZ_GENERATORS=csmith tests/fuzz.sh` (4 jobs) | 258 s | 2,845 MB | 25 skipped (native did not finish in 10 s), 173 matched, 2 rejected with a diagnostic; PASS |
| the same with the script at `fd6fe97` and `JOBS=4` | 250 s | 2,827 MB | 25 skipped, 173 matched, 2 rejected; PASS |

The two csmith `results.tsv` files are identical except for the reason text of the 25 skipped seeds (`native did not finish` became `native did not finish in 10 s`). The wall times are not comparable, because the new run shared the host with the RSS measurement above.

The branches the validation runs do not reach were run on purpose (csmith seed 2):

- With `FUZZ_MEMORY_MB=1`, the native program is stopped and skipped as `over the memory limit: more than 1 MB resident`.
- With a `dotnet` shim earlier in `PATH` that replaces the generated programs with `tail /dev/zero` and `FUZZ_MEMORY_MB=100`, both modes report `memory (over 612 MB)`. The run fails, the program is kept with its `repro.sh`, and no process is left behind.

In the CI image (`llvmnet-ci:trixie` built from `scripts/ci/install-packages.sh`, run as root with `--cpuset-cpus 0-3 --memory 16g`, no systemd), `run_limited` ran the portable build of seed 43 to the same output as native with a 2,560 MB limit and stopped it as `memory` with 1,000 MB. `sleep 5` with a 1 s limit was stopped as `time`.

Not yet seen: the nightly job on GitHub, which waits for the repository to be published (FD-19).
