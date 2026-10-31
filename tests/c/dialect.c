/* Reports the language dialect the frontend compiled it as, to compare the driver's defaults with the frontend's. */
#include <stdio.h>
int main(void)
{
#ifdef __cplusplus
    printf("c++ %ld\n", (long)__cplusplus);
#else
    printf("c %ld\n", (long)__STDC_VERSION__);
#endif
    return 0;
}
