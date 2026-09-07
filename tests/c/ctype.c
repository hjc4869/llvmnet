#include <ctype.h>
int main(void)
{
    for (int value = -1; value <= 255; value++) {
        int alpha = (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');
        int digit = value >= '0' && value <= '9';
        if (!!isalpha(value) != alpha || !!isdigit(value) != digit) return 1;
        if (!!isalnum(value) != (alpha || digit)) return 2;
        if (!!isspace(value) != (value == 32 || (value >= 9 && value <= 13))) return 3;
        int lower = value >= 'A' && value <= 'Z' ? value + 32 : value;
        int upper = value >= 'a' && value <= 'z' ? value - 32 : value;
        if (tolower(value) != lower || toupper(value) != upper) return 4;
    }
    return 0;
}