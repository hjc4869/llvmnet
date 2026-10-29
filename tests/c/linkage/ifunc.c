static int generic(void) { return 1; }
static void *resolve(void) { return (void *)generic; }
int selected(void) __attribute__((ifunc("resolve")));
int main(void) { return selected() - 1; }
