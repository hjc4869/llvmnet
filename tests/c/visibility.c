/* A library built with -fvisibility=hidden: only the default-visibility definition is exported. */
__attribute__((visibility("default"))) int exported_function(int value) { return value + 1; }
int hidden_function(int value) { return value + 2; }
static int local_function(int value) { return value + 3; }
int uses_both(int value) { return hidden_function(local_function(value)); }
__attribute__((visibility("default"))) int exported_user(int value) { return uses_both(value); }
