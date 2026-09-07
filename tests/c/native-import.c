extern int abs(int value);
static volatile int value = -42;
int main(void) { return abs(value) != 42; }