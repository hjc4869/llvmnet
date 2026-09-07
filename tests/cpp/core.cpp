#include <cstdio>
#include <vector>
#include <algorithm>

static int destroyed;
static int initialized;
struct Scope {
    ~Scope() { destroyed++; }
};
struct Base {
    virtual int compute(int value) const = 0;
    virtual ~Base() = default;
};
struct Derived : Base {
    int compute(int value) const override { return value * 3; }
};
struct Failure { int value; };
__attribute__((noinline)) static int initialize() { initialized++; return 42; }
__attribute__((noinline)) static int cached() { static int value = initialize(); return value; }
__attribute__((noinline)) static int calculate(int value)
{
    Scope scope;
    if (value == 1) throw 17;
    if (value == 2) throw Failure{29};
    return value + 4;
}
__attribute__((noinline)) static int dispatch(Base *object, int value) { return object->compute(value); }
int main()
{
    std::vector<int> values;
    for (int index = 0; index < 20; index++) values.push_back(20 - index);
    std::sort(values.begin(), values.end());
    if (values.front() != 1 || values.back() != 20 || values.size() != 20) return 1;
    Base *object = new Derived;
    if (dispatch(object, 14) != 42) return 2;
    delete object;
    if (cached() != 42 || cached() != 42 || initialized != 1) return 3;
    try { calculate(1); return 4; }
    catch (int value) { if (value != 17 || destroyed != 1) return 5; }
    try {
        try { calculate(2); return 6; }
        catch (const Failure &failure) { if (failure.value != 29) return 7; throw; }
    }
    catch (const Failure &failure) { if (failure.value != 29 || destroyed != 2) return 8; }
    try { calculate(1); return 9; }
    catch (...) { if (destroyed != 3) return 10; }
    if (calculate(3) != 7 || destroyed != 4) return 11;
    std::printf("c++: vector=%zu static=%d destructors=%d\n", values.size(), initialized, destroyed);
    return 0;
}