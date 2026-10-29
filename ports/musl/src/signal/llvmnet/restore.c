/* llvmnet port of musl: signal handlers are invoked by the host as ordinary calls, so there is no
 * signal frame to return through; sigaction still needs these restorer symbols to exist. */
void __restore(void) {}
void __restore_rt(void) {}
