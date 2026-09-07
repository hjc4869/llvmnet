extern int must_not_resolve(void);
int calculate(int value) { return value * 99; }
int unused_archive_member(void) { return must_not_resolve(); }