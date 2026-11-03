/* Native extensions of the llvmnet portable ABI (docs/user/extensions.md).
 *
 * A program linked with a native extension (--extension=<name>, or -l with a name the extension's manifest lists) calls
 * a host library, such as EGL, through the extension's stubs, which load the library when they are first called. These
 * functions ask whether that will work on the running host before the program depends on it. They are part of
 * LlvmNet.Runtime and of every llvmnet toolchain, in both runtime modes; the header is outside the frozen dotnet64-v2
 * header set. */
#ifndef LLVMNET_EXT_H
#define LLVMNET_EXT_H

#ifdef __cplusplus
extern "C" {
#endif

/* 1 when the program is linked with the native extension NAME and the extension can be used on this host: every host
 * library it stands for loads and provides the functions the extension requires. 0 otherwise, including when the program
 * is not linked with NAME. Loads the host libraries. In system mode, an extension the program is linked with is
 * available, because the system loader loaded its libraries before the program started. */
int llvmnet_ext_available(const char *name);

/* What the native extension NAME loaded, one line per host library ("EGL: /usr/lib/x86_64-linux-gnu/libEGL.so.1"), or
 * why it could not (the file names tried and the system loader's message); NULL when the program is not linked with
 * NAME. The string stays valid until the process ends. */
const char *llvmnet_ext_library(const char *name);

#ifdef __cplusplus
}
#endif

#endif
