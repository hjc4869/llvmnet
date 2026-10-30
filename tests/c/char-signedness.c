/* Plain char signedness differs between objects: the library half is compiled with signed char, the program half
   with -funsigned-char. A char argument or result carries the byte, and each side reads it with its own signedness. */
#include <stdio.h>
#include <string.h>
#if defined(LIBRARY)
int char_value(char c) { return c; }
char char_make(int value) { return (char)value; }
int char_compare(const char *left, const char *right) { return *left < *right; }
#else
int char_value(char c);
char char_make(int value);
int char_compare(const char *left, const char *right);
int main(void) {
    char c = (char)0xe9;
    char text[] = "caf\xe9";
    printf("%d %d %d %d\n", c, char_value(c), char_make(0xe9), char_value(char_make(0xe9)));
    printf("%d %d\n", c > 'a', char_compare("\xe9", "a"));
    printf("%d %d\n", strchr(text, c) == text + 3, strcmp(text, "cafe") > 0);
    return 0;
}
#endif
