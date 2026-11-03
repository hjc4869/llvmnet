/* The API of the template extension: replace it with the header of the library your extension stands for. The
 * generator reads this file through llvmnet's Clang for the portable target, so it must parse there (no host-only
 * types); HELLO_API only matters to the native build of the library on Windows. */
#ifndef HELLO_H
#define HELLO_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32) && defined(HELLO_BUILD)
#define HELLO_API __declspec(dllexport)
#elif defined(_WIN32)
#define HELLO_API __declspec(dllimport)
#else
#define HELLO_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct { double x, y; } hello_point;

/* Called with each word of the text, in order; returning nonzero stops the walk. */
typedef int (*hello_visit)(int index, const char *word, size_t length, void *user);

HELLO_API int32_t hello_version(void);
/* Writes "Hello, <name>!" to buffer (NUL-terminated, cut to size) and returns the length of the whole greeting. */
HELLO_API size_t hello_greet(const char *name, char *buffer, size_t size);
/* A structure passed and returned by value. */
HELLO_API hello_point hello_midpoint(hello_point a, hello_point b);
/* A callback the library calls back into the program. Returns the number of words visited. */
HELLO_API int hello_each_word(const char *text, hello_visit visit, void *user);

#ifdef __cplusplus
}
#endif

#endif
