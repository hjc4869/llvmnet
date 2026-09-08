#include <chrono>
#include <cstdio>
#include <future>
#include <stdexcept>
#include <string>
#include <thread>

int main()
{
    std::promise<int> promise;
    auto result = promise.get_future();
    if (result.wait_for(std::chrono::milliseconds(1)) != std::future_status::timeout)
        return 1;
    try {
        promise.get_future();
        return 2;
    } catch (const std::future_error &error) {
        if (error.code() != std::make_error_code(std::future_errc::future_already_retrieved))
            return 3;
    }
    promise.set_value(42);
    if (result.get() != 42 || result.valid())
        return 4;
    try {
        promise.set_value(43);
        return 5;
    } catch (const std::future_error &error) {
        if (error.code() != std::make_error_code(std::future_errc::promise_already_satisfied))
            return 6;
    }
    std::future<int> abandoned;
    {
        std::promise<int> temporary;
        abandoned = temporary.get_future();
    }
    try {
        abandoned.get();
        return 7;
    } catch (const std::future_error &error) {
        if (error.code() != std::make_error_code(std::future_errc::broken_promise))
            return 8;
    }
    auto deferred = std::async(std::launch::deferred, [] { return 19; });
    if (deferred.wait_for(std::chrono::seconds(0)) != std::future_status::deferred)
        return 9;
    auto shared = deferred.share();
    if (shared.get() != 19 || shared.get() != 19)
        return 10;
    auto exception = std::async(std::launch::async, []() -> int { throw std::runtime_error("future failure"); });
    try {
        exception.get();
        return 11;
    } catch (const std::runtime_error &error) {
        if (std::string(error.what()) != "future failure")
            return 12;
    }
    std::promise<int> on_exit;
    auto exited = on_exit.get_future();
    std::thread worker([owned = std::move(on_exit)]() mutable { owned.set_value_at_thread_exit(73); });
    if (exited.get() != 73)
        return 13;
    worker.join();
    std::puts("futures, promise errors, waits and thread-exit results passed");
}