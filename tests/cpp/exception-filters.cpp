#include <cstdio>
#include <exception>
#include <typeinfo>

#ifdef __clang__
#pragma clang diagnostic ignored "-Wdynamic-exception-spec"
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
#endif

static volatile int destroyed;
static volatile int unwinding;
static volatile int copied;

struct Value {
    int number;
    explicit Value(int value) : number(value) {}
    Value(const Value &other) : number(other.number) { ++copied; }
};

struct Polymorphic {
    virtual ~Polymorphic() {}
};

__attribute__((noinline)) static const std::type_info &dynamic_type(Polymorphic *value)
{
    return typeid(*value);
}

struct Cleanup {
    ~Cleanup() throw() {
        ++destroyed;
        if (std::uncaught_exception())
            ++unwinding;
    }
};

__attribute__((noinline)) static void allowed() throw(int)
{
    Cleanup cleanup;
    throw 42;
}

__attribute__((noinline)) static void no_exception() throw()
{
    Cleanup cleanup;
}

int main()
{
    no_exception();
    try {
        allowed();
        return 1;
    } catch (int value) {
        if (value != 42 || destroyed != 2 || unwinding != 1 || std::uncaught_exception())
            return 2;
    }
    try {
        throw Value(19);
    } catch (Value value) {
        if (value.number != 19 || copied == 0 || std::uncaught_exception())
            return 3;
    }
    try {
        dynamic_type(nullptr);
        return 4;
    } catch (const std::bad_typeid &) {
    }
    std::puts("C++ exception filters passed");
    return 0;
}