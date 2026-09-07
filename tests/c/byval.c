struct large { long values[8]; };
static volatile long replacement = 99;
__attribute__((noinline)) static long modify(struct large value)
{
    long original = value.values[3];
    value.values[3] = replacement;
    return original + value.values[3];
}
__attribute__((noinline)) static struct large return_large(long value)
{
    struct large result;
    for (int index = 0; index < 8; index++) result.values[index] = value + index;
    return result;
}
int main(void)
{
    struct large original = return_large(7);
    if (modify(original) != 109) return 1;
    if (original.values[3] != 10 || original.values[7] != 14) return 2;
    return 0;
}