#include <stdio.h>
extern int needs(void);
int main(void)
{
    printf("needs %d\n", needs());
    return needs() != 5;
}
