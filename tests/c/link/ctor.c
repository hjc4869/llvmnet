/* In an archive that nothing references: linked only with -Wl,--whole-archive. */
#include <stdio.h>
__attribute__((constructor)) static void announce(void) { puts("constructor from the archive"); }
int archived_only(void) { return 1; }
