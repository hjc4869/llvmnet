#include <stdio.h>
extern char *getenv();
int main(int count, char **arguments)
{
    return count > 1 && strcmp(arguments[1], "probe") && getenv("PATH") == 0;
}