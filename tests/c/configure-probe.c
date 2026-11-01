/* The programs build systems link to check that a function exists (TC-07). Each declares the function PROBE without
   its parameters, calls it without arguments and is only linked, never run; tests/configure-probes.sh runs some to
   check that the call traps. STYLE selects the build system's template. */
#if STYLE == 1
/* autoconf's AC_CHECK_FUNC and AC_CHECK_LIB (2.7x), and Meson's has_function without a prefix. */
char PROBE (void);
int main (void) { return PROBE (); }
#elif STYLE == 2
/* ffmpeg's check_func: a declaration without a prototype. */
extern int PROBE();
int main(void){ PROBE(); }
#elif STYLE == 3
/* CMake's CheckFunctionExists.c. */
char PROBE(void);
int main(int ac, char *av[]) { PROBE(); if (ac > 1000) { return *av[0]; } return 0; }
#endif
