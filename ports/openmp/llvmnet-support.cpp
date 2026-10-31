// Definitions the assembly file z_Linux_asm.S provides on other targets; the portable target compiles no assembly.
#include "kmp.h"

extern "C" {
// The lock of the unnamed critical section (.gomp_critical_user_ in the assembly file) and its address, which
// kmp_gsupport.cpp uses for GOMP_critical_start and GOMP_critical_end.
static kmp_critical_name __kmp_unnamed_critical_lock;
kmp_critical_name *__kmp_unnamed_critical_addr = &__kmp_unnamed_critical_lock;
}
