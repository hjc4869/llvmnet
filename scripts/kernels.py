#!/usr/bin/env python3
"""Builds a native kernel package (ADR-16 Level 3, SV-13 and SV-14; docs/user/native-kernels.md) from natively built
objects: for each runtime identifier its objects (assembled by nasm, or compiled or assembled by Clang for that target)
become a shared library for the JIT and a static library for NativeAOT, and manifest.json lists the functions they define
and the data they share with the program. An archive given as an object contributes its native members, so a project's
own archives, which hold its C as llvmnet's bitcode and its assembly as native objects, serve as they are; llvmnet leaves
their native members to the package when it links them with --native-kernels.

    scripts/kernels.py --name dsp --version 1.0 --library dsp-kernels --output dsp/ \\
        --runtime linux-x64 obj/x86/*.o --runtime linux-arm64 obj/aarch64/*.o \\
        [--bitcode libavcodec.a ...] [--exclude ff_cpu_cpuid ...] [--not-leaf dsp_wait ...] [--optional ff_emms_asm ...]

Data the objects read but do not define (C tables that assembly uses) is the program's: --bitcode names the program's
bitcode objects or archives, from which llvmnet --print-kernel-data gives each variable's size and alignment, and the
package defines storage for it, which the program uses where the library is loaded. Data the objects define is exported
for the program. Every shared datum gets an accessor, llvmnet_kernel_data_<name>. Functions the objects call but do not
define are an error (kernels must not call back into the program). --exclude leaves a function to the program (a C
version for every host, such as CPU detection written in assembly). --not-leaf marks functions that block or run long,
which keep .NET's GC transition as NativeAOT calls them. --optional marks void functions that the program calls whatever
the CPU (ffmpeg's ff_emms_asm, which clears MMX state after its kernels): they do nothing where the host has no library.

Linkers: ld.lld-22 (Linux), ld64.lld-22 (macOS), lld-link-22 (Windows); llvm-nm-22, llvm-objcopy-22, llvm-ar-22.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# Symbols the linker defines, which objects reference without the program's defining them.
LINKER_SYMBOLS = {"_GLOBAL_OFFSET_TABLE_", "__ImageBase", "_DYNAMIC", "__dso_handle"}
TRIPLES = {"linux-x64": "x86_64-linux-gnu", "linux-arm64": "aarch64-linux-gnu", "win-x64": "x86_64-pc-windows-msvc",
           "osx-arm64": "arm64-apple-macos11", "osx-x64": "x86_64-apple-macos11"}


def run(command, **options):
    result = subprocess.run(command, capture_output=True, text=True, **options)
    if result.returncode != 0:
        sys.exit(f"kernels.py: {' '.join(command[:3])}... failed:\n{result.stderr}")
    return result.stdout


def native_members(archive, directory):
    """Extracts the native object members (ELF, COFF, Mach-O) of a GNU or BSD archive into the directory, under unique
    names, as members with one name can occur twice."""
    data = open(archive, "rb").read()
    if not data.startswith(b"!<arch>\n"):
        sys.exit(f"kernels.py: {archive} is not an archive")
    names, offset, extracted = b"", 8, []
    while offset + 60 <= len(data):
        header = data[offset:offset + 60]
        name, size = header[:16].decode().rstrip(), int(header[48:58].decode())
        body = data[offset + 60:offset + 60 + size]
        offset += 60 + size + (size & 1)
        if name.startswith("#1/"):
            length = int(name[3:])
            name, body = body[:length].rstrip(b"\0").decode(), body[length:]
        elif name == "//":
            names = body
            continue
        elif name.startswith("/") and name[1:].isdigit():
            start = int(name[1:])
            name = names[start:names.index(b"\n", start)].decode().rstrip("/")
        elif name in ("/", "/SYM64/", "__.SYMDEF", "__.SYMDEF SORTED"):
            continue
        name = name.rstrip("/")
        if body[:4] == b"\x7fELF" or body[:4] in (b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe") or body[:2] in (b"\x64\x86", b"\x64\xaa"):
            path = os.path.join(directory, f"{len(os.listdir(directory))}-{os.path.basename(name)}")
            open(path, "wb").write(body)
            extracted.append(path)
    return extracted


def symbols(objects):
    """The objects' defined functions, defined data and undefined symbols, by name (Mach-O's leading underscore removed)."""
    functions, data, undefined = set(), set(), set()
    for line in run(["llvm-nm-22", "-P", "-g", *objects]).splitlines():
        parts = line.split()
        if len(parts) < 2 or line.endswith(":"):
            continue
        name, kind = parts[0], parts[1]
        if kind == "U":
            undefined.add(name)
        elif kind in "Tt":
            functions.add(name)
        elif kind in "DdRrBbCcSs":
            data.add(name)
    return functions, data, undefined - functions - data - LINKER_SYMBOLS


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--name", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--library", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--runtime", nargs="+", action="append", required=True, metavar=("RID", "OBJECT"))
    parser.add_argument("--bitcode", nargs="*", default=[])
    parser.add_argument("--exclude", nargs="*", default=[])
    parser.add_argument("--not-leaf", nargs="*", default=[])
    parser.add_argument("--optional", nargs="*", default=[])
    parser.add_argument("--llvmnet", default=os.path.join(ROOT, "bin", "llvmnet"))
    arguments = parser.parse_args()
    output = os.path.abspath(arguments.output)
    excluded = set(arguments.exclude)
    runtimes, all_functions, all_data = {}, set(), set()
    with tempfile.TemporaryDirectory() as work:
        for rid, *inputs in arguments.runtime:
            if rid not in TRIPLES:
                sys.exit(f"kernels.py: {rid} is not a runtime identifier this helper links ({', '.join(TRIPLES)})")
            members = os.path.join(work, f"{rid}-members")
            os.makedirs(members)
            objects = []
            for item in inputs:
                with open(item, "rb") as file:
                    archive = file.read(8) == b"!<arch>\n"
                objects += native_members(item, members) if archive else [item]
            if not objects:
                sys.exit(f"kernels.py: no native objects for {rid}")
            macho = rid.startswith("osx-")
            strip = (lambda name: name[1:] if name.startswith("_") else name) if macho else (lambda name: name)
            functions, data, undefined = symbols(objects)
            functions = {strip(name) for name in functions} - excluded
            data = {strip(name) for name in data}
            undefined = {strip(name) for name in undefined} - excluded
            # What the objects read but do not define comes from the program, which must define it as data.
            program_data = sorted(undefined)
            shared = sorted(set(program_data) | data)
            names = os.path.join(work, f"{rid}.data")
            with open(names, "w") as file:
                file.write("\n".join(shared) + "\n")
            source = os.path.join(work, f"{rid}-data.c")
            if program_data and not arguments.bitcode:
                sys.exit(f"kernels.py: the {rid} objects read {', '.join(program_data[:5])}... from the program; name its bitcode with --bitcode")
            if shared:
                run([arguments.llvmnet, f"--print-kernel-data={names}", *arguments.bitcode, "-o", source])
                text = open(source).read()
                missing = [name for name in program_data if f"unsigned char {name}[]" in text]
                if missing:
                    sys.exit(f"kernels.py: the {rid} objects need {', '.join(missing[:8])}, which neither they nor the program's data define (kernels must not call the program)")
            else:
                open(source, "w").write("/* No shared data. */\n")
            data_object = os.path.join(work, f"{rid}-data.o")
            windows = rid.startswith("win-")
            # Windows objects name no C runtime library, as the package's DLL links none.
            run(["clang-22", f"--target={TRIPLES[rid]}", "-fms-omit-default-lib" if windows else "-fPIC", "-O1", "-c", source, "-o", data_object])
            native = os.path.join(output, "runtimes", rid, "native")
            os.makedirs(native, exist_ok=True)
            # The static library keeps the objects as they are; the shared one exports every function and datum, which
            # assemblers such as ffmpeg's x86inc mark hidden.
            static = os.path.join(native, ("" if windows else "lib") + arguments.library + (".lib" if windows else ".a"))
            if os.path.exists(static):
                os.remove(static)
            run(["llvm-ar-22", "--format=" + ("coff" if windows else "darwin" if macho else "gnu"), "rcs", static, *objects, data_object])
            accessors = {f"llvmnet_kernel_data_{name}" for name in shared}
            exported = sorted(functions | accessors | set(shared))
            visible = []
            listing = os.path.join(work, f"{rid}.visible")
            with open(listing, "w") as file:
                file.write("\n".join(("_" + name if macho else name) for name in exported) + "\n")
            for index, item in enumerate(objects):
                copy = os.path.join(work, f"{rid}-{index}-{os.path.basename(item)}")
                if rid.startswith("linux-"):
                    run(["llvm-objcopy-22", f"--set-symbols-visibility={listing}=default", item, copy])
                else:
                    shutil.copy(item, copy)
                visible.append(copy)
            if rid.startswith("linux-"):
                shared_library = os.path.join(native, f"lib{arguments.library}.so")
                run(["ld.lld-22", "-shared", "-Bsymbolic", "--no-undefined", "-z", "noexecstack", "-soname", f"lib{arguments.library}.so", "-o", shared_library, *visible, data_object])
            elif macho:
                shared_library = os.path.join(native, f"lib{arguments.library}.dylib")
                run(["ld64.lld-22", "-dylib", "-arch", "arm64" if rid.endswith("arm64") else "x86_64", "-platform_version", "macos", "11.0", "11.0",
                     "-install_name", f"@rpath/lib{arguments.library}.dylib", "-undefined", "error", "-o", shared_library, *visible, data_object])
            else:
                shared_library = os.path.join(native, f"{arguments.library}.dll")
                run(["lld-link-22", "/dll", "/noentry", "/nodefaultlib", "/noimplib", "/machine:x64", f"/out:{shared_library}", *[f"/export:{name}" + ("" if name in functions | accessors else ",DATA") for name in exported], *visible, data_object])
            runtimes[rid] = {"shared": os.path.relpath(shared_library, output), "static": os.path.relpath(static, output)}
            all_functions |= functions
            all_data |= set(shared)
    manifest = {"name": arguments.name, "version": arguments.version, "library": arguments.library, "runtimes": runtimes,
                "symbols": {name: {**({"leaf": False} if name in arguments.not_leaf else {}), **({"optional": True} if name in arguments.optional else {})}
                            for name in sorted(all_functions)}, "data": sorted(all_data)}
    with open(os.path.join(output, "manifest.json"), "w") as file:
        json.dump(manifest, file, indent=1)
        file.write("\n")
    print(f"{output}: {len(all_functions)} functions, {len(all_data)} shared data, {', '.join(runtimes)}")


if __name__ == "__main__":
    main()
