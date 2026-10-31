#!/usr/bin/env python3
"""Feed every option the frontends' own --help lists to the driver and classify what happens (TC-01).

Each option, in both runtime modes, is given to "llvmnet --runtime=<mode> <option> [value] -c <source>" in its own
scratch directory, with a value chosen from the placeholder in the help line (<file>, <dir>, <value>, ...). The outcome is:

  works           exit 0 and the object was written, or the option is one that stops before an object on purpose
  frontend-error  nonzero exit and the frontend reported the problem itself ("error:" naming the option or value)
  not-accepted    the driver rejected the option with its reason ("llvmnet: error: option '...' is not accepted")
  driver-error    another "llvmnet: error:" with a usage or input message (exit 1 or 2)
  FAIL            an internal error (exit 3), a stack trace, a timeout, exit 0 with no output, or a value that was
                  mistaken for an input file

Usage: options.py <llvmnet> <work-directory> <output.tsv> [--jobs N]
Exit status 1 when any option FAILs. The TSV has one row per option and mode with the outcome and the first message.
"""
import concurrent.futures
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CLANG = os.environ.get("LLVMNET_CLANG", "clang-22")
FLANG = os.environ.get("LLVMNET_FLANG", "flang-22")
# Options that stop before an object on purpose, so exit 0 without one is their correct result.
NO_OBJECT = {"-E", "-S", "-M", "-MM", "-###", "--help", "--version", "-fsyntax-only", "-o", "-dumpmachine", "-dumpversion",
             "-print-search-dirs", "-print-resource-dir", "-print-target-triple", "-print-file-name=", "-print-prog-name=",
             "-print-targets", "-print-supported-cpus", "-print-supported-extensions", "-print-enabled-extensions",
             "-print-effective-triple", "-print-libgcc-file-name", "-print-runtime-dir", "-print-multi-flags-experimental",
             "-print-diagnostic-options", "-print-library-module-manifest-path", "-print-rocm-search-dirs", "-print-ivar-layout",
             "--driver-mode", "-fdriver-only", "--analyze", "-emit-ast", "-emit-interface-stubs", "-emit-merged-ifs",
             "-rewrite-objc", "-rewrite-legacy-objc", "-ccc-print-phases", "-ccc-print-bindings", "--precompile", "-fsyntax-only",
             "-extract-api", "--extract-api", "-fdirectives-only", "-fmodule-output", "-Wl,--version", "-fget-symbols-sources",
             "-module-file-info", "-verify-pch", "-objcmt-migrate-literals", "-emit-cir", "-fdump-record-layouts"}


def parse_help(text):
    """Yields (spelling, value placeholder, separate) for each option line: separate says the value is a separate argument."""
    for line in text.splitlines():
        match = re.match(r"^  (-{1,2}[^\s<]+)(<[^>]*>)?(?:\s+(<[^>]*>))?", line)
        if not match:
            continue
        name, joined, separate = match.groups()
        if joined is not None and separate is not None:
            yield name, joined, separate
        elif joined is not None:
            yield name, joined, None
        elif separate is not None:
            yield name, None, separate
        else:
            yield name, None, None


def value_for(placeholder, name, work):
    text = placeholder.strip("<>").lower()
    if name in ("-std=", "-std"): return "c11"
    if name in ("-x",): return "c"
    if name.startswith("-W") and not name.startswith("-Wl") and not name.startswith("-Wa") and not name.startswith("-Wp"): return "all"
    if "file" in text or "script" in text or "pch" in text: return os.path.join(work, "value.h")
    if "dir" in text or "path" in text or "prefix" in text: return work
    if "macro" in text: return "LLVMNET_OPTION_TEST=1"
    if "language" in text: return "c"
    if "triple" in text: return "x86_64-unknown-linux-gnu"
    if "arch" in text: return "x86_64"
    if "cpu" in text: return "generic"
    if "name" in text: return "x"
    return "1"


def run_one(llvmnet, work, source, mode, driver, index, name, joined, separate, timeout):
    # One directory per run: "-fno-builtin" and "-fno-builtin-" must not share one.
    directory = os.path.join(work, mode, f"{index}-{re.sub(r'[^A-Za-z0-9_.+-]', '_', name).lstrip('-')}")
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(directory)
    with open(os.path.join(directory, "value.h"), "w") as header:
        header.write("/* a value for options that take a file */\n")
    arguments = [name + (value_for(joined, name, directory) if joined else "")]
    if separate:
        arguments.append(value_for(separate, name, directory))
    command = [llvmnet, f"--runtime={mode}", *arguments, "-c", source]
    environment = dict(os.environ, LLVMNET_DRIVER=driver)
    try:
        completed = subprocess.run(command, cwd=directory, env=environment, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        code, stderr = completed.returncode, completed.stderr
    except subprocess.TimeoutExpired:
        code, stderr = 124, "timeout"
    base = os.path.splitext(os.path.basename(source))[0]
    produced = os.path.exists(os.path.join(directory, base + ".o"))
    message = next((line for line in stderr.splitlines() if "error" in line.lower()), stderr.strip().splitlines()[0] if stderr.strip() else "")
    option = name.rstrip("=") if name.endswith("=") else name
    if code == 3 or "Unhandled exception" in stderr or "   at LlvmNet" in stderr or "internal compiler error" in stderr:
        outcome = "FAIL: internal error"
    elif code == 124:
        outcome = "FAIL: timeout"
    elif "Unsupported input kind" in stderr or "No input files" in stderr:
        outcome = "FAIL: the value was taken for an input"
    elif code == 0:
        outcome = "works" if produced or option in NO_OBJECT or name in NO_OBJECT else "FAIL: exit 0 without an object"
    elif re.search(r"llvmnet: error: option '.*' is not accepted", stderr):
        outcome = "not-accepted"
    elif re.search(r"llvmnet: error: ", stderr) and code in (1, 2):
        outcome = "driver-error"
    elif code != 0 and (re.search(r"error|unknown|invalid|unsupported|not supported|cannot|missing|expected", stderr, re.IGNORECASE) or option in stderr):
        outcome = "frontend-error"
    else:
        outcome = f"FAIL: exit {code} without a clear error"
    shutil.rmtree(directory, ignore_errors=True)
    return name, mode, driver, " ".join(arguments), outcome, code, message[:200]


def main():
    jobs = 8
    arguments = sys.argv[1:]
    if "--jobs" in arguments:
        index = arguments.index("--jobs")
        jobs = int(arguments[index + 1])
        del arguments[index:index + 2]
    llvmnet, work, output = arguments[0], arguments[1], arguments[2]
    os.makedirs(work, exist_ok=True)
    tasks = []
    for tool, driver, source in ((CLANG, "clang", os.path.join(ROOT, "tests", "c", "smoke.c")), (FLANG, "flang", os.path.join(ROOT, "tests", "fortran", "core.f90"))):
        if shutil.which(tool) is None:
            continue
        text = subprocess.run([tool, "--help"], check=True, capture_output=True, text=True).stdout
        for name, joined, separate in parse_help(text):
            for mode in ("portable", "system"):
                tasks.append((mode, driver, name, joined, separate, source))
    rows = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=jobs) as pool:
        futures = [pool.submit(run_one, llvmnet, work, source, mode, driver, index, name, joined, separate, 180) for index, (mode, driver, name, joined, separate, source) in enumerate(tasks)]
        for future in concurrent.futures.as_completed(futures):
            rows.append(future.result())
    rows.sort(key=lambda row: (row[2], row[0], row[1]))
    with open(output, "w") as tsv:
        tsv.write("option\tmode\tfrontend\targuments\toutcome\texit\tmessage\n")
        for row in rows:
            tsv.write("\t".join(str(field).replace("\t", " ") for field in row) + "\n")
    failures = [row for row in rows if row[4].startswith("FAIL")]
    for driver in ("clang", "flang"):
        for mode in ("portable", "system"):
            subset = [row for row in rows if row[2] == driver and row[1] == mode]
            if not subset:
                continue
            counts = {}
            for row in subset:
                counts[row[4].split(":")[0]] = counts.get(row[4].split(":")[0], 0) + 1
            print(f"{driver} options in {mode} mode: {len(subset)}: " + ", ".join(f"{count} {outcome}" for outcome, count in sorted(counts.items())))
    for row in failures:
        print(f"FAIL: {row[2]} {row[1]} {row[3]}: {row[4]} (exit {row[5]}): {row[6]}", file=sys.stderr)
    print(f"{len(rows)} option runs, {len(failures)} failures; details in {output}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
