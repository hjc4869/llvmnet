#include <array>
#include <atomic>
#include <condition_variable>
#include <cxxabi.h>
#include <cstdlib>
#include <fstream>
#include <functional>
#include <iomanip>
#include <iostream>
#include <list>
#include <sstream>
#include <string>
#include <mutex>
#include <memory>
#include <regex>
#include <thread>
#include <unordered_map>

int main(int count, char **arguments)
{
    if (count != 2)
        return 1;
    std::unordered_map<int, std::string> values;
    for (int index = 0; index < 1000; ++index)
        values.emplace(index, "entry");
    values.rehash(4096);
    values.erase(41);
    if (values.size() != 999 || values.at(42) != "entry" || values.count(41) != 0)
        return 6;
    std::list<int> ordered{3, 1};
    std::list<int> pending{2};
    ordered.splice(ordered.end(), pending);
    ordered.sort();
    ordered.reverse();
    if (!pending.empty() || ordered.size() != 3 || ordered.front() != 3 || ordered.back() != 1)
        return 10;
    std::function<int(int)> function = [](int value) { return value + 19; };
    if (function(23) != 42)
        return 11;
    function = nullptr;
    try {
        function(1);
        return 12;
    } catch (const std::bad_function_call &) {
    }
    std::weak_ptr<int> weak;
    {
        auto owner = std::make_shared<int>(42);
        weak = owner;
        std::shared_ptr<int> shared(weak);
        if (*shared != 42 || shared.use_count() != 2)
            return 13;
    }
    if (!weak.expired() || weak.lock())
        return 14;
    try {
        std::shared_ptr<int> expired(weak);
        return 15;
    } catch (const std::bad_weak_ptr &) {
    }
    std::regex pattern("([a-z]+)=([0-9]+)");
    std::smatch match;
    std::string record = "value=42";
    if (!std::regex_match(record, match, pattern) || match[1] != "value" || match[2] != "42")
        return 16;
    try {
        std::regex invalid("[");
        return 17;
    } catch (const std::regex_error &) {
    }
    int demangle_status = -1;
    char *demangled = abi::__cxa_demangle("_Z3fooi", nullptr, nullptr, &demangle_status);
    if (demangle_status != 0 || !demangled || std::string(demangled) != "foo(int)")
        return 18;
    std::free(demangled);
    demangled = abi::__cxa_demangle("not a mangled name", nullptr, nullptr, &demangle_status);
    if (demangled || demangle_status != -2)
        return 19;
    std::mutex mutex;
    std::condition_variable condition;
    int ready = 0;
    bool release = false;
    std::atomic<int> total{0};
    std::array<std::thread, 4> workers;
    std::array<std::thread::id, 4> identities;
    for (int index = 0; index < 4; ++index) {
        workers[index] = std::thread([&, index] {
            std::unique_lock<std::mutex> lock(mutex);
            identities[index] = std::this_thread::get_id();
            ++ready;
            condition.notify_all();
            condition.wait(lock, [&] { return release; });
            lock.unlock();
            for (int iteration = 0; iteration < 1000; ++iteration)
                total.fetch_add(1, std::memory_order_relaxed);
        });
    }
    {
        std::unique_lock<std::mutex> lock(mutex);
        condition.wait(lock, [&] { return ready == 4; });
        release = true;
    }
    condition.notify_all();
    for (auto &worker : workers)
        worker.join();
    if (total != 4000)
        return 7;
    for (int first = 0; first < 4; ++first)
        for (int second = first + 1; second < 4; ++second)
            if (identities[first] == identities[second])
                return 8;
    std::ostringstream formatted;
    formatted << "stream " << std::setw(4) << 42 << ' ' << std::fixed << std::setprecision(3) << 1.25;
    if (formatted.str() != "stream   42 1.250")
        return 2;
    {
        std::ofstream output(arguments[1]);
        output << formatted.str() << '\n';
        if (!output)
            return 3;
    }
    std::ifstream input(arguments[1]);
    std::string line;
    if (!std::getline(input, line) || line != formatted.str())
        return 4;
    std::istringstream numbers("-19 2.5");
    std::istringstream skipped("abc42");
    skipped.ignore(3);
    if (skipped.gcount() != 3 || skipped.peek() != '4')
        return 9;
    int integer = 0;
    double fraction = 0;
    numbers >> integer >> fraction;
    if (integer != -19 || fraction != 2.5)
        return 5;
    std::cout << line << std::endl;
    return 0;
}