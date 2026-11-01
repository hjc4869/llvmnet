/* main in an archive member, the gtest_main and Catch2 idiom: a native link extracts it through crt1.o's reference. */
#include <stdio.h>
extern int run_tests(void);
int main(void)
{
    printf("main from the archive: %d\n", run_tests());
    return 0;
}
