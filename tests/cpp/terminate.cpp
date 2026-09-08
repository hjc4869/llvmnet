#include <cstdlib>
#include <cstring>
#include <exception>

static void handler()
{
    std::_Exit(73);
}

static void returning_handler()
{
}

static void throwing_handler()
{
    throw 19;
}

static void throw_error()
{
    throw 42;
}

static void violate_noexcept() noexcept
{
    throw_error();
}

int main(int count, char **arguments)
{
    if (count != 2)
        return 1;
    auto original = std::set_terminate(handler);
    if (!original || std::get_terminate() != handler)
        return 2;
    if (std::set_terminate(original) != handler || std::get_terminate() != original)
        return 3;
    if (std::strcmp(arguments[1], "default") == 0)
        original();
    std::set_terminate(handler);
    if (std::strcmp(arguments[1], "noexcept") == 0) {
        try {
            violate_noexcept();
        } catch (...) {
            return 4;
        }
    }
    if (std::strcmp(arguments[1], "return") == 0)
        std::set_terminate(returning_handler);
    if (std::strcmp(arguments[1], "throw") == 0)
        std::set_terminate(throwing_handler);
    std::terminate();
}