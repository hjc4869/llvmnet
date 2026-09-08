#include <exception>
#include <new>
#include <stdexcept>
#include <string>
#include <typeinfo>
#include <cstdlib>
#include <cstring>
#include <cstdio>
#include <cstdarg>
#include <algorithm>
#include <chrono>
#include <time.h>

namespace {
long *reference_count(const char *message)
{
    return reinterpret_cast<long *>(const_cast<char *>(message)) - 1;
}
void release_message(const char *message)
{
    long *count = reference_count(message);
    if (__atomic_sub_fetch(count, 1L, __ATOMIC_ACQ_REL) == 0)
        std::free(count);
}
}

_LIBCPP_BEGIN_NAMESPACE_STD
namespace chrono {
system_clock::time_point system_clock::now() noexcept
{
    timespec value;
    if (clock_gettime(CLOCK_REALTIME, &value)) std::abort();
    return time_point(duration_cast<duration>(seconds(value.tv_sec) + nanoseconds(value.tv_nsec)));
}
time_t system_clock::to_time_t(const time_point &value) noexcept
{
    return static_cast<time_t>(duration_cast<seconds>(value.time_since_epoch()).count());
}
system_clock::time_point system_clock::from_time_t(time_t value) noexcept
{
    return time_point(seconds(value));
}
steady_clock::time_point steady_clock::now() noexcept
{
    timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value)) std::abort();
    return time_point(duration_cast<duration>(seconds(value.tv_sec) + nanoseconds(value.tv_nsec)));
}
}
template <class Compare, class Iterator>
void __sort(Iterator first, Iterator last, Compare compare)
{
    (void)compare;
    __less<> comparator;
    __sort_dispatch<_ClassicAlgPolicy, Iterator, __less<>>(first, last, comparator);
}
#define LLVMNET_SORT(Type) template void __sort<__less<Type>&, Type*>(Type*, Type*, __less<Type>&)
LLVMNET_SORT(char);
LLVMNET_SORT(signed char);
LLVMNET_SORT(unsigned char);
LLVMNET_SORT(short);
LLVMNET_SORT(unsigned short);
LLVMNET_SORT(int);
LLVMNET_SORT(unsigned int);
LLVMNET_SORT(long);
LLVMNET_SORT(unsigned long);
LLVMNET_SORT(long long);
LLVMNET_SORT(unsigned long long);
LLVMNET_SORT(float);
LLVMNET_SORT(double);
LLVMNET_SORT(long double);
LLVMNET_SORT(wchar_t);
#undef LLVMNET_SORT
__libcpp_refstring::__libcpp_refstring(const char *message)
{
    size_t length = std::strlen(message) + 1;
    long *storage = static_cast<long *>(std::malloc(sizeof(long) + length));
    if (!storage) throw std::bad_alloc();
    *storage = 1;
    char *text = reinterpret_cast<char *>(storage + 1);
    std::memcpy(text, message, length);
    __imp_ = text;
}
__libcpp_refstring::__libcpp_refstring(const __libcpp_refstring &source) noexcept : __imp_(source.__imp_)
{
    __atomic_add_fetch(reference_count(__imp_), 1L, __ATOMIC_RELAXED);
}
__libcpp_refstring &__libcpp_refstring::operator=(const __libcpp_refstring &source) noexcept
{
    if (this != &source) {
        __atomic_add_fetch(reference_count(source.__imp_), 1L, __ATOMIC_RELAXED);
        release_message(__imp_);
        __imp_ = source.__imp_;
    }
    return *this;
}
__libcpp_refstring::~__libcpp_refstring() { release_message(__imp_); }
bool __libcpp_refstring::__uses_refcount() const { return true; }
[[noreturn]] void __throw_runtime_error(const char *message) { throw std::runtime_error(message); }
[[noreturn]] void __libcpp_verbose_abort(const char *format, ...) noexcept
{
    va_list arguments;
    va_start(arguments, format);
    std::vfprintf(stderr, format, arguments);
    va_end(arguments);
    std::abort();
}
_LIBCPP_END_NAMESPACE_STD

namespace std {
exception::~exception() noexcept = default;
const char *exception::what() const noexcept { return "std::exception"; }
bad_exception::~bad_exception() noexcept = default;
const char *bad_exception::what() const noexcept { return "std::bad_exception"; }
bad_alloc::~bad_alloc() noexcept = default;
const char *bad_alloc::what() const noexcept { return "std::bad_alloc"; }
bad_array_new_length::~bad_array_new_length() noexcept = default;
const char *bad_array_new_length::what() const noexcept { return "std::bad_array_new_length"; }
type_info::~type_info() = default;
bad_cast::~bad_cast() noexcept = default;
const char *bad_cast::what() const noexcept { return "std::bad_cast"; }
bad_typeid::~bad_typeid() noexcept = default;
const char *bad_typeid::what() const noexcept { return "std::bad_typeid"; }
logic_error::logic_error(const char *message) : __imp_(message) {}
logic_error::logic_error(const string &message) : __imp_(message.c_str()) {}
logic_error::logic_error(const logic_error &source) noexcept : exception(source), __imp_(source.__imp_) {}
logic_error &logic_error::operator=(const logic_error &source) noexcept { __imp_ = source.__imp_; return *this; }
logic_error::~logic_error() noexcept = default;
const char *logic_error::what() const noexcept { return __imp_.c_str(); }
runtime_error::runtime_error(const char *message) : __imp_(message) {}
runtime_error::runtime_error(const string &message) : __imp_(message.c_str()) {}
runtime_error::runtime_error(const runtime_error &source) noexcept : exception(source), __imp_(source.__imp_) {}
runtime_error &runtime_error::operator=(const runtime_error &source) noexcept { __imp_ = source.__imp_; return *this; }
runtime_error::~runtime_error() noexcept = default;
const char *runtime_error::what() const noexcept { return __imp_.c_str(); }
domain_error::~domain_error() noexcept = default;
invalid_argument::~invalid_argument() noexcept = default;
length_error::~length_error() noexcept = default;
out_of_range::~out_of_range() noexcept = default;
range_error::~range_error() noexcept = default;
overflow_error::~overflow_error() noexcept = default;
underflow_error::~underflow_error() noexcept = default;
}