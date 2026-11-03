# A native extension for llvmnet

This directory is a template for a native extension of llvmnet's portable ABI: a package that lets a portable program call a host library through that library's own header ([user/extensions.md](../../docs/user/extensions.md)). Copy it to a repository of its own and replace the example library with yours. The toolchain package and the dotnet tool ship it in `share/llvmnet/templates/native-extension/`; `llvmnet --print-extension-kit` prints where.

| File | What it is | What to do |
| --- | --- | --- |
| `extension.json` | The description: the extension's name and version, the header to read, which functions to proxy, the host library's file names on Linux, Windows and macOS, the `-l` names that select the extension, callback pools and checks | Rename the extension and the library, and list your header and functions |
| `include/hello.h` | The API, read through llvmnet's Clang for the portable target | Replace it with your library's header |
| `native/hello.c` | The host library itself, built natively | Delete it: your extension stands for an existing library |
| `example/main.c`, `example/expected.txt` | A program that calls the library natively and in portable mode, and what it prints | Replace it with a test of your own |

[dev/extensions.md](../../docs/dev/extensions.md) describes every field of the description, the types an extension can pass and the checks it runs before it installs.

## Building

Building an extension needs an llvmnet toolchain, `python3` and the .NET 10 SDK. The kit that ships with the toolchain generates the extension from its header, builds its assembly, checks every stub against the C prototypes and installs the package:

```sh
kit="$(llvmnet --print-extension-kit | sed -n 's/^kit //p')"
bash "$kit/build-extensions.sh" .
llvmnet --list-extensions
```

The package it installed is also left in `$XDG_CACHE_HOME/llvmnet/ext-build/hello/package/`. It serves every host, so it can be archived and installed into another toolchain with `llvmnet --install-extension <dir>`.

## Trying the example

Build the host library natively, then the example with llvmnet. `-lllvmnet-hello` is one of the `-l` names the description lists, so it selects the extension:

```sh
cc -O2 -shared -fPIC -Iinclude native/hello.c -o libllvmnet-hello.so
llvmnet-clang -O2 example/main.c -lllvmnet-hello -o hello.dll
LLVMNET_EXT_HELLO_DIR=$PWD dotnet hello.dll
```

It prints `example/expected.txt`. Without the library it prints the file names it tried and exits with status 1, because it asks `llvmnet_ext_available` first. On Windows and macOS the comments of `native/hello.c` give the commands for `llvmnet-hello.dll` and `libllvmnet-hello.dylib`; the same `hello.dll` runs there.

## Publishing

The package contains the extension's headers and its assembly, not the host library: a program finds that library on the host, or beside itself. Ship the notices of the library's headers in the package with the description's `licenses` entries. An extension is rebuilt for each llvmnet minor release while llvmnet's major version is 0 ([versioning.md](../../docs/dev/versioning.md#native-extensions)).
