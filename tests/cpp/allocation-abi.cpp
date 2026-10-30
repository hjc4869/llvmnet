// C++ allocation ABI details (CX-09): a program's own operator new and delete replace the library's, sized and
// aligned forms are called, and an allocation the C library refuses throws std::bad_alloc. Pointers escape through
// sink so that the compiler cannot elide the allocations, which the standard permits.
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <new>

static int allocations, sized_deletes, aligned_allocations, aligned_deletes;
static void *volatile sink;

void *operator new(std::size_t size)
{
    allocations++;
    if (void *memory = std::malloc(size ? size : 1)) return memory;
    throw std::bad_alloc();
}
void operator delete(void *memory) noexcept { std::free(memory); }
void operator delete(void *memory, std::size_t) noexcept
{
    sized_deletes++;
    std::free(memory);
}
void *operator new(std::size_t size, std::align_val_t alignment)
{
    aligned_allocations++;
    if (void *memory = std::aligned_alloc(static_cast<std::size_t>(alignment), (size + static_cast<std::size_t>(alignment) - 1) & ~(static_cast<std::size_t>(alignment) - 1)))
        return memory;
    throw std::bad_alloc();
}
void operator delete(void *memory, std::align_val_t) noexcept
{
    aligned_deletes++;
    std::free(memory);
}
void operator delete(void *memory, std::size_t, std::align_val_t) noexcept
{
    aligned_deletes++;
    std::free(memory);
}

struct alignas(64) Wide {
    char bytes[64];
};
struct Plain {
    long values[3];
};

int main()
{
    Plain *plain = new Plain();
    sink = plain;
    delete plain;
    Wide *wide = new Wide();
    sink = wide;
    std::printf("aligned to 64: %d\n", reinterpret_cast<std::uintptr_t>(wide) % 64 == 0);
    delete wide;
    std::printf("allocations %d, sized deletes %d, aligned allocations %d, aligned deletes %d\n", allocations, sized_deletes,
        aligned_allocations, aligned_deletes);
    try {
        volatile std::size_t huge = static_cast<std::size_t>(1) << 62;
        char *never = new char[huge];
        std::printf("allocated %p\n", static_cast<void *>(never));
    } catch (const std::bad_alloc &error) {
        std::printf("bad_alloc\n");
    }
    volatile std::size_t too_large = static_cast<std::size_t>(1) << 62;
    char *nothing = new (std::nothrow) char[too_large];
    sink = nothing;
    std::printf("nothrow null: %d\n", nothing == nullptr);
    return 0;
}
