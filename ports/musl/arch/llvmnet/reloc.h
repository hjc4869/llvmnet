/* llvmnet port of musl: only the definitions src/thread/__unmapself.c needs; the dynamic linker is not built. */
#define LDSO_ARCH "llvmnet"

/* Thread stacks are not execution stacks under .NET, so "switching" to a private stack is a plain call. */
#define CRTJMP(pc, sp) ((void (*)(void))(pc))()
