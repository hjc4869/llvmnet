/* Computed goto (indirectbr) is not supported: with -g the error names this file and line. */
int main(int argc, char **argv)
{
    (void)argv;
    static void *targets[] = {&&first, &&second};
    goto *targets[argc > 1];
first:
    return 1;
second:
    return 2;
}
