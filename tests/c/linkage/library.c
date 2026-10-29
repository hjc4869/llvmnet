static int implementation(int value) { return value * 3; }
int public_alias(int value) __attribute__((alias("implementation")));
