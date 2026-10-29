/* llvmnet port of musl: the thread pointer is a per-thread slot in the managed host,
 * set by the set_thread_area system call. TLS stays below the thread pointer as on x86-64. */
uintptr_t __llvmnet_get_tp(void);

static inline uintptr_t __get_tp() { return __llvmnet_get_tp(); }

#define MC_PC gregs[REG_RIP]
