#include <math.h>

/* C23's roundeven for long double, which the linker substitutes for llvm.roundeven.f80. The port's floating-point
 * environment rounds to nearest only (src/fenv/llvmnet/fenv.c), so rint is it. */
long double roundevenl(long double x)
{
	return rintl(x);
}
