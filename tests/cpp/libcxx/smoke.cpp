// libc++ on musl: containers, algorithms, strings and streams, regex, filesystem,
// threads, futures, formatting, smart pointers, exceptions and RTTI. Output is library-independent so native
// clang++ with libstdc++ serves as the oracle.
#include <algorithm>
#include <any>
#include <atomic>
#include <charconv>
#include <chrono>
#include <condition_variable>
#include <exception>
#include <filesystem>
#include <format>
#include <fstream>
#include <functional>
#include <future>
#include <iomanip>
#include <iostream>
#include <map>
#include <memory>
#include <mutex>
#include <numeric>
#include <optional>
#include <random>
#include <regex>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <typeinfo>
#include <unordered_map>
#include <variant>
#include <vector>

struct Base { virtual ~Base() = default; virtual std::string name() const { return "Base"; } };
struct Derived : Base { std::string name() const override { return "Derived"; } };
struct Error : std::runtime_error { int code; Error(const char *what, int value) : std::runtime_error(what), code(value) {} };
static int &counter() { static int value = 41; return ++value; }

int main()
{
    std::vector<int> values{5, 3, 9, 1, 7, 3};
    std::sort(values.begin(), values.end());
    values.erase(std::unique(values.begin(), values.end()), values.end());
    std::cout << "sorted:";
    for (int value : values) std::cout << ' ' << value;
    std::cout << " sum=" << std::accumulate(values.begin(), values.end(), 0) << '\n';
    std::map<std::string, int> words;
    std::istringstream text("the quick brown fox jumps over the lazy dog the end");
    for (std::string word; text >> word;) ++words[word];
    std::cout << "the=" << words["the"] << " unique=" << words.size() << '\n';
    std::unordered_map<int, std::string> lookup{{1, "one"}, {2, "two"}};
    std::set<std::string> ordered{"pear", "apple", "fig"};
    std::cout << lookup.at(2) << ' ' << *ordered.begin() << ' ' << std::setw(8) << std::setfill('.') << std::left << "pad" << "|\n";
    std::cout << std::fixed << std::setprecision(4) << 3.14159265 << ' ' << std::scientific << 12345.678 << std::defaultfloat << '\n';
    std::cout << std::to_string(42) << ' ' << std::to_string(2.5) << ' ' << std::stoi("-17") << ' ' << std::stod("6.25e1") << '\n';
    char buffer[32]; auto [end, error] = std::to_chars(buffer, buffer + sizeof buffer, 0.1);
    std::cout << "to_chars " << std::string(buffer, end) << ' ' << (error == std::errc()) << '\n';
    std::cout << std::format("format {:>6}|{:#x}|{:.3f}|{}", "ab", 255, 2.0 / 3.0, std::vector<int>{1, 2}.size()) << '\n';
    std::regex pattern(R"((\w+)@(\w+)\.com)");
    std::smatch match; std::string mail = "contact: alice@example.com";
    if (std::regex_search(mail, match, pattern)) std::cout << "regex " << match[1] << " at " << match[2] << '\n';
    std::cout << std::regex_replace(std::string("a1b22c333"), std::regex("[0-9]+"), "#") << '\n';
    try { throw Error("custom failure", 7); }
    catch (const std::exception &caught) { std::cout << "caught " << caught.what() << " code=" << dynamic_cast<const Error &>(caught).code << '\n'; }
    try { std::vector<int>().at(3); } catch (const std::out_of_range &) { std::cout << "out_of_range\n"; }
    try { std::any value = 1; (void)std::any_cast<std::string>(value); } catch (const std::bad_any_cast &) { std::cout << "bad_any_cast\n"; }
    std::exception_ptr saved;
    try { throw std::logic_error("stored"); } catch (...) { saved = std::current_exception(); }
    try { std::rethrow_exception(saved); } catch (const std::logic_error &error) { std::cout << "rethrown " << error.what() << '\n'; }
    std::unique_ptr<Base> object = std::make_unique<Derived>();
    std::cout << object->name() << ' ' << (dynamic_cast<Derived *>(object.get()) != nullptr) << ' ' << (typeid(*object) == typeid(Derived)) << '\n';
    auto shared = std::make_shared<int>(5); std::weak_ptr<int> weak = shared;
    std::cout << "shared " << shared.use_count() << ' ' << !weak.expired() << '\n';
    std::variant<int, std::string> choice = std::string("text");
    std::optional<int> maybe; std::function<int(int)> square = [](int x) { return x * x; };
    std::cout << std::get<std::string>(choice) << ' ' << maybe.value_or(-1) << ' ' << square(9) << ' ' << counter() << ' ' << counter() << '\n';
    std::mutex mutex; std::condition_variable ready; int finished = 0; std::atomic<long> total{0};
    std::vector<std::thread> threads;
    for (int id = 0; id < 4; id++)
        threads.emplace_back([&, id] {
            for (int i = 0; i < 10000; i++) total += id + 1;
            std::lock_guard<std::mutex> lock(mutex); ++finished; ready.notify_one();
        });
    { std::unique_lock<std::mutex> lock(mutex); ready.wait(lock, [&] { return finished == 4; }); }
    for (auto &thread : threads) thread.join();
    std::future<int> answer = std::async(std::launch::async, [] { return 6 * 7; });
    std::promise<std::string> promise; std::future<std::string> promised = promise.get_future();
    std::thread producer([&] { promise.set_value("promised"); }); producer.join();
    std::cout << "threads total=" << total << " async=" << answer.get() << ' ' << promised.get() << '\n';
    namespace fs = std::filesystem;
    fs::path directory = fs::current_path() / "libcxx-smoke-dir";
    fs::create_directories(directory / "nested");
    { std::ofstream file(directory / "nested" / "data.txt"); file << "line one\nline two\n"; }
    std::ifstream file(directory / "nested" / "data.txt"); std::string line; int lines = 0;
    while (std::getline(file, line)) ++lines;
    std::cout << "filesystem lines=" << lines << " size=" << fs::file_size(directory / "nested" / "data.txt")
              << " exists=" << fs::exists(directory) << " ext=" << fs::path("a/b/c.tar.gz").extension() << '\n';
    std::cout << "removed=" << fs::remove_all(directory) << '\n';
    std::mt19937 engine(12345); std::uniform_int_distribution<int> die(1, 6);
    std::cout << "mt19937 " << engine() << ' ' << die(engine) << '\n';
    auto start = std::chrono::steady_clock::now();
    std::this_thread::sleep_for(std::chrono::milliseconds(5));
    std::cout << "chrono ok=" << (std::chrono::steady_clock::now() - start >= std::chrono::milliseconds(5)) << '\n';
    std::cout << std::to_string(1.0L / 3) << '\n';
    return 0;
}
