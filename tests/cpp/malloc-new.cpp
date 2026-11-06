// C++ allocation on the C library's allocator: every form of operator new and delete (plain, array, nothrow,
// aligned, sized), containers growing on eight threads at once, objects deleted on a thread other than the one that
// created them, and thread_local objects whose destructors free memory while their thread exits. Prints totals that
// must equal the native build's. What operator new returns must be a block of the C library's malloc: in portable
// mode, mimalloc's malloc_usable_size is 0 for any address that is not one of its blocks.
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <vector>

extern "C" std::size_t malloc_usable_size(void *p);

static bool owned(const void *p, std::size_t size) { return malloc_usable_size(const_cast<void *>(p)) >= size; }

struct alignas(256) Wide { unsigned char bytes[256]; };

struct Tracked
{
    std::vector<std::string> names;
    ~Tracked() { names.clear(); names.shrink_to_fit(); }
};

thread_local Tracked tracked;
static std::mutex lock;
static std::vector<std::unique_ptr<std::string>> handed;

static unsigned long work(int seed)
{
    unsigned long total = 0;
    std::vector<std::string> strings;
    for (int i = 0; i < 20000; i++) {
        strings.emplace_back(static_cast<std::size_t>(1 + (seed * 31 + i) % 300), static_cast<char>('a' + i % 26));
        if (i % 3 == 0) strings.pop_back();
    }
    for (const std::string &s : strings) total += s.size() + static_cast<unsigned char>(s.back());
    for (int i = 0; i < 100; i++) tracked.names.push_back(std::to_string(seed * 1000 + i));
    auto shared = std::make_shared<std::vector<int>>(1000, seed);
    std::lock_guard<std::mutex> guard(lock);
    for (int i = 0; i < 50; i++) handed.push_back(std::make_unique<std::string>(100 + i, 'x'));
    return total + static_cast<unsigned long>(shared->size());
}

int main()
{
    int *one = new int(7);
    int *many = new int[1000]();
    Wide *wide = new Wide;
    Wide *wides = new Wide[3];
    char *nothrow = new (std::nothrow) char[1 << 20];
    void *raw = ::operator new(4096);
    void *aligned = ::operator new(1000, std::align_val_t(4096));
    std::string text(5000, 'q');
    bool all = owned(one, sizeof *one) && owned(many, 1000 * sizeof *many) && owned(wide, sizeof *wide) &&
               owned(wides, 3 * sizeof *wides) && owned(nothrow, 1 << 20) && owned(raw, 4096) && owned(aligned, 1000) &&
               owned(text.data(), 5000);
    std::printf("operator new allocates from the C library's malloc: %s\n", all ? "yes" : "no");
    std::printf("aligned new: %d %d %d\n", reinterpret_cast<std::uintptr_t>(wide) % 256 == 0,
                reinterpret_cast<std::uintptr_t>(wides) % 256 == 0, reinterpret_cast<std::uintptr_t>(aligned) % 4096 == 0);
    ::operator delete(aligned, std::align_val_t(4096));
    ::operator delete(raw, 4096);
    delete[] nothrow;
    delete[] wides;
    delete wide;
    delete[] many;
    delete one;

    unsigned long totals[8] = {};
    std::vector<std::thread> threads;
    for (int round = 0; round < 4; round++) {
        for (int i = 0; i < 8; i++) threads.emplace_back([&totals, i, round] { totals[i] += work(round * 8 + i); });
        for (std::thread &thread : threads) thread.join();
        threads.clear();
    }
    unsigned long total = 0;
    for (unsigned long t : totals) total += t;
    std::size_t bytes = 0;
    for (const auto &s : handed) bytes += s->size();
    handed.clear();
    std::printf("threads: %lu, strings handed to main and deleted there: %zu bytes\n", total, bytes);
    return 0;
}
