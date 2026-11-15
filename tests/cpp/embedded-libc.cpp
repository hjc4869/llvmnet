// A library that embeds the C library (llvmnet -shared --embed-libc), called from .NET by tests/embedded-libc.sh through
// LlvmNet.Checks call-exports: static constructors, the heap, stdio, errno and thread_local, on the thread that first
// calls the library and on another .NET thread. Only the five default-visibility functions are exported.
#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#define EXPORT extern "C" __attribute__((visibility("default")))

namespace {
struct Registry {
    Registry() : value(42) { names.push_back("constructed"); }
    int value;
    std::vector<std::string> names;
};
Registry registry;
thread_local int per_thread = 7;
}

int helper(int value) { return value * 2; }  // hidden by -fvisibility=hidden, so not exported

EXPORT int constructed(void) { return registry.value + static_cast<int>(registry.names.size()) * 1000; }

EXPORT int heap(void)
{
    std::string text = "abc";
    for (int index = 0; index < 1000; index++) text += std::to_string(index);
    std::vector<char *> blocks;
    for (int index = 0; index < 256; index++) blocks.push_back(static_cast<char *>(std::malloc(64 + index)));
    for (char *block : blocks) std::free(block);
    return static_cast<int>(text.size());
}

EXPORT int print(void)
{
    int written = std::printf("hello from the embedded C library\n");
    std::fflush(stdout);
    return written;
}

EXPORT int format(void)
{
    char buffer[64];
    std::snprintf(buffer, sizeof buffer, "%.3f %s %d", 3.14159, "x", helper(21));
    return std::strcmp(buffer, "3.142 x 42") == 0;
}

EXPORT int thread_state(void)
{
    per_thread++;
    errno = 0;
    std::strtol("99999999999999999999", nullptr, 10);
    return per_thread * 10 + (errno == ERANGE);
}
