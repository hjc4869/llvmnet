/* Process start for musl in the portable sysroot (replaces crt1.o). The emitted .NET entry method builds a
 * Linux-style argv/envp/auxv vector and calls __llvmnet_libc_main; musl then owns initialization,
 * atexit handlers, stdio flushing and exit, exactly as with a native static link. */
void __init_libc(char **envp, char *program_name);
int atexit(void (*)(void));
_Noreturn void exit(int);

/* Provided by the compiler: runs llvm.global_ctors after musl is initialized and registers
 * llvm.global_dtors through __llvmnet_register_finalizer. */
void __llvmnet_run_constructors(void);

/* Weak, so that relinking an already linked module (for example one kept with --keep-ir) with this object again works. */
__attribute__((weak)) int __llvmnet_register_finalizer(void (*function)(void)) { return atexit(function); }

__attribute__((weak)) int __llvmnet_libc_main(int argc, char **argv, char **envp, int (*main_function)(int, char **, char **))
{
    __init_libc(envp, argv[0]);
    __llvmnet_run_constructors();
    exit(main_function(argc, argv, envp));
}
