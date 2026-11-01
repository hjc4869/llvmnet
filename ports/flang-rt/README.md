# flang-rt port

`scripts/build-flang-rt.sh` compiles flang-rt 22.1.8 from the pinned llvm-project sources for both runtime modes. It
copies `flang-rt/lib/runtime` and applies the patches in this directory to the copy:

- `execute-status.patch`: `TerminationCheck` takes the wait status of `system()` as an `int`. Upstream passes a
  `std::int64_t`, and musl's `WIFSIGNALED(s)`, `(((s)&0xffff)-1U < 0xffu)`, is then true for a status of 0, so
  `EXECUTE_COMMAND_LINE` reported `CMDSTAT` 7 ("Killed by signal") for every successful command in portable mode.
  glibc's definition works with either width, so system mode was not affected. Found by flang-rt's unit tests (WG-03).
