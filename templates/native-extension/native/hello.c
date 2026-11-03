/* The host library of the template extension, built natively for each host:
 *   Linux:   cc -O2 -shared -fPIC -Iinclude native/hello.c -o libllvmnet-hello.so
 *   macOS:   cc -O2 -dynamiclib -Iinclude native/hello.c -o libllvmnet-hello.dylib
 *   Windows: cl /O2 /LD /Iinclude native\hello.c /Fe:llvmnet-hello.dll
 * Your extension stands for an existing library instead; this one only gives the template something to call. */
#define HELLO_BUILD 1
#include "hello.h"

#include <stdio.h>
#include <string.h>

int32_t hello_version(void) { return 10000; }

size_t hello_greet(const char *name, char *buffer, size_t size) {
    int length = snprintf(buffer, size, "Hello, %s!", name ? name : "world");
    return length < 0 ? 0 : (size_t)length;
}

hello_point hello_midpoint(hello_point a, hello_point b) {
    hello_point middle = {(a.x + b.x) / 2, (a.y + b.y) / 2};
    return middle;
}

int hello_each_word(const char *text, hello_visit visit, void *user) {
    int index = 0;
    while (text && *text) {
        size_t skip = strspn(text, " \t\n");
        text += skip;
        size_t length = strcspn(text, " \t\n");
        if (length == 0) break;
        if (visit(index++, text, length, user)) break;
        text += length;
    }
    return index;
}
