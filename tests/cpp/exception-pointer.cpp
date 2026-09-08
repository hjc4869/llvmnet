#include <exception>
#include <pthread.h>
#include <cstdio>

static int live;
static int failures;

struct Error {
    int value;
    explicit Error(int number) : value(number) { __atomic_fetch_add(&live, 1, __ATOMIC_SEQ_CST); }
    Error(const Error &other) : value(other.value) { __atomic_fetch_add(&live, 1, __ATOMIC_SEQ_CST); }
    ~Error() { __atomic_fetch_sub(&live, 1, __ATOMIC_SEQ_CST); }
};

struct FailingCopy {
    FailingCopy() = default;
    FailingCopy(const FailingCopy &) { throw Error(42); }
};

static void *rethrow_worker(void *argument)
{
    std::exception_ptr saved = *static_cast<std::exception_ptr *>(argument);
    for (int iteration = 0; iteration < 100; ++iteration) {
        try {
            std::rethrow_exception(saved);
        } catch (const Error &error) {
            if (error.value != 42 || std::current_exception() != saved || std::uncaught_exceptions() != 0)
                __atomic_fetch_add(&failures, 1, __ATOMIC_SEQ_CST);
        }
    }
    return nullptr;
}

int main()
{
    if (std::current_exception())
        return 1;
    std::exception_ptr saved;
    try {
        throw Error(42);
    } catch (...) {
        saved = std::current_exception();
    }
    if (!saved || live != 1)
        return 2;
    std::exception_ptr copy(saved);
    saved = nullptr;
    pthread_t workers[2];
    for (auto &worker : workers)
        if (pthread_create(&worker, nullptr, rethrow_worker, &copy))
            return 3;
    for (auto &worker : workers)
        if (pthread_join(worker, nullptr))
            return 4;
    if (failures || live != 1)
        return 5;
    copy = nullptr;
    if (live != 0)
        return 6;
    auto made = std::make_exception_ptr(Error(42));
    if (live != 1 || std::uncaught_exceptions() != 0)
        return 7;
    rethrow_worker(&made);
    made = nullptr;
    if (live != 0 || failures)
        return 8;
    auto failed = std::make_exception_ptr(FailingCopy{});
    if (live != 1 || std::uncaught_exceptions() != 0)
        return 9;
    rethrow_worker(&failed);
    failed = nullptr;
    if (live != 0 || failures || std::uncaught_exceptions() != 0)
        return 10;
    std::puts("exception_ptr ownership and concurrent rethrow passed");
    return 0;
}