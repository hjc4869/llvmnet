#include <chrono>
#include <ctime>
#include <initializer_list>

static auto initialized = std::chrono::system_clock::now();

int main()
{
    using std::chrono::system_clock;
    using std::chrono::steady_clock;
    using std::chrono::seconds;
    using std::chrono::duration_cast;
    auto current = system_clock::now();
    auto epoch = std::time(nullptr);
    if (system_clock::to_time_t(current) < epoch - 2 || system_clock::to_time_t(current) > epoch + 2) return 1;
    if (duration_cast<seconds>(current - initialized).count() > 2) return 2;
    for (std::time_t value : {-123456, 0, 123456}) {
        if (system_clock::to_time_t(system_clock::from_time_t(value)) != value) return 3;
    }
    auto previous = steady_clock::now();
    for (int index = 0; index < 1000; ++index) {
        auto next = steady_clock::now();
        if (next < previous) return 4;
        previous = next;
    }
    return 0;
}