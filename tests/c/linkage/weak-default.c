/* Weak default definitions, musl-style weak aliases and undefined weak references. */
int hook(void) __attribute__((weak));
int hook(void) { return 1; }
static int dummy(void) { return 10; }
int alias_default(void) __attribute__((weak, alias("dummy")));
extern int maybe_defined(void) __attribute__((weak));
extern int maybe_data __attribute__((weak));
int main(void)
{
    int failures = 0;
    failures += hook() != 2;            /* strong definition in strong.c wins */
    failures += alias_default() != 20;  /* strong definition in strong.c wins over the weak alias */
    failures += maybe_defined != 0;     /* only referenced weakly: undefined, so null */
    failures += &maybe_data != 0;
    return failures;
}
