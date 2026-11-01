// C++20 library members that libstdc++ defines in src/c++20, and the C++17 floating-point conversions <format> needs:
// stringstream::str() const & and &&, formatting, atomic waits, synchronized streams and time zones. With C++23,
// std::println as well.
#include <atomic>
#include <chrono>
#include <cstdio>
#include <format>
#include <iostream>
#include <latch>
#include <semaphore>
#include <sstream>
#include <string>
#include <syncstream>
#include <thread>
#include <vector>
#if __cplusplus > 202002L
#include <print>
#endif

int main()
{
    std::stringstream stream;
    stream << "x=" << 42;
    std::string copy = stream.str();
    std::string moved = std::move(stream).str();
    std::printf("%s %s [%s]\n", copy.c_str(), moved.c_str(), stream.str().c_str());
    std::ostringstream view;
    view << "view " << 1.5;
    std::printf("%.*s\n", static_cast<int>(view.view().size()), view.view().data());

    std::puts(std::format("{:>6}|{:<6}|{:^9.3f}|{:#x}|{}", 42, "ab", 3.14159, 255, std::string("s")).c_str());
    std::puts(std::format("{:.6g} {:e} {} {}", 1.0 / 3, 6.02214076e23, 0.1, 1e-7f).c_str());

    std::atomic<int> state{0};
    std::thread waker([&] {
        state.store(1);
        state.notify_one();
    });
    state.wait(0);
    waker.join();
    std::counting_semaphore<4> slots(0);
    std::latch done(2);
    {
        std::vector<std::jthread> workers;
        for (int i = 0; i < 2; i++)
            workers.emplace_back([&] {
                slots.release();
                done.count_down();
            });
        done.wait();
    }
    slots.acquire();
    slots.acquire();
    std::printf("waits: state=%d\n", state.load());

    std::fflush(stdout);
    {
        std::osyncstream out(std::cout);
        out << "synchronized " << 7 << '\n';
    }
    std::cout.flush();

    using namespace std::chrono;
    const time_zone *zone = locate_zone("Europe/Berlin");
    sys_seconds instant = sys_days{2026y / March / 29} + 1h + 30min;
    std::puts(std::format("{} {:%F %T %Z}", zone->name(), zoned_time{zone, instant}).c_str());
#if __cplusplus > 202002L
    std::println("println {} {:.2f}", 23, 2.5);
#endif
    return 0;
}
