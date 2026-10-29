/* Lives in an archive; a weak reference alone must not extract it (ELF archive semantics). */
int maybe_defined(void) { return 3; }
int maybe_data = 4;
