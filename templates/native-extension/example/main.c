/* A program that uses the template extension. The same source builds natively against the host library and with
 * llvmnet in portable mode (-lllvmnet-hello adds the extension); in portable mode it first asks whether the library can
 * be loaded on this host. */
#include <hello.h>
#include <stdio.h>
#include <string.h>
#ifdef __LLVMNET_PORTABLE__
#include <llvmnet/ext.h>
#endif

static int count_letters(int index, const char *word, size_t length, void *user) {
    *(size_t *)user += length;
    printf("word %d: %.*s\n", index, (int)length, word);
    return strncmp(word, "stop", length) == 0 && length == 4;
}

int main(void) {
#ifdef __LLVMNET_PORTABLE__
    if (!llvmnet_ext_available("hello")) {
        printf("the hello library cannot be used here:\n%s\n", llvmnet_ext_library("hello"));
        return 1;
    }
#endif
    printf("hello_version: %d\n", (int)hello_version());
    char buffer[16];
    size_t length = hello_greet("portable world", buffer, sizeof buffer);
    printf("greeting: %s (%zu characters in full)\n", buffer, length);
    hello_point middle = hello_midpoint((hello_point){1.0, 2.0}, (hello_point){4.0, -6.0});
    printf("midpoint: %.1f, %.1f\n", middle.x, middle.y);
    size_t letters = 0;
    int words = hello_each_word("one two  three stop four", count_letters, &letters);
    printf("visited %d words, %zu letters\n", words, letters);
    return 0;
}
