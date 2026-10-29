/* llvmnet port of musl: there is no ELF entry point. The emitted .NET entry method calls
 * __init_libc, the program's constructors, main and exit directly. */
