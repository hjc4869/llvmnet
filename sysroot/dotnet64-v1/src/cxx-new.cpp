#include <new>

namespace std {
bad_alloc::bad_alloc() noexcept = default;
bad_array_new_length::bad_array_new_length() noexcept = default;
[[noreturn]] void __throw_bad_alloc() { throw bad_alloc(); }
}