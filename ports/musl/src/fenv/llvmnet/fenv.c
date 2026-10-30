/* llvmnet port of musl: the floating-point environment of CIL. .NET always rounds to nearest and has no
 * exception flags, so the x86-64 <fenv.h> names are available but only FE_TONEAREST can be selected, no flag is
 * ever set, and raising one reports failure. Upstream's generic src/fenv/fenv.c reports success for both. */
#include <fenv.h>

int feclearexcept(int mask)
{
	return 0;
}

int feraiseexcept(int mask)
{
	return mask & FE_ALL_EXCEPT ? -1 : 0;
}

int fetestexcept(int mask)
{
	return 0;
}

int fegetround(void)
{
	return FE_TONEAREST;
}

int __fesetround(int r)
{
	return r == FE_TONEAREST ? 0 : -1;
}

int fegetenv(fenv_t *envp)
{
	return 0;
}

int fesetenv(const fenv_t *envp)
{
	return 0;
}
