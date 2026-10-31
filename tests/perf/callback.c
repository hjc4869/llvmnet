// Function-pointer materialization (PF-13): 150 million stores of one of two function addresses into a dispatch table
// followed by a call through the table entry, the pattern of callbacks set up in a loop. Prints a checksum.
#include <stdint.h>
#include <stdio.h>

struct handler { uint64_t (*run)(uint64_t); uint64_t state; };

static __attribute__((noinline)) uint64_t forward(uint64_t value) { return value * 3 + 1; }
static __attribute__((noinline)) uint64_t backward(uint64_t value) { return value >> 1 ^ 0x9e3779b97f4a7c15u; }

static __attribute__((noinline)) void install(struct handler *handler, long index)
{
    handler->run = index & 1 ? forward : backward;
}

int main(void)
{
    static struct handler handlers[1024];
    uint64_t checksum = 0;
    for (long index = 0; index < 150000000; index++) {
        struct handler *handler = &handlers[index & 1023];
        install(handler, index ^ (index >> 10));
        handler->state = handler->run(handler->state + index);
        checksum += handler->state & 0xff;
    }
    printf("%llu\n", (unsigned long long)checksum);
    return 0;
}
