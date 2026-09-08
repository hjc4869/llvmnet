#include <cstdio>
#include <new>

struct Throws {
    Throws() { throw 19; }
};

int main()
{
    int *value = new (std::nothrow) int(42);
    if (!value || *value != 42)
        return 1;
    delete value;
    char *array = new (std::nothrow) char[37];
    if (!array)
        return 2;
    array[36] = 23;
    if (array[36] != 23)
        return 3;
    delete[] array;
    volatile std::size_t impossible = static_cast<std::size_t>(-1);
    if (::operator new(impossible, std::nothrow) != 0 || ::operator new[](impossible, std::nothrow) != 0)
        return 4;
    try {
        delete new (std::nothrow) Throws;
        return 5;
    } catch (int number) {
        if (number != 19)
            return 6;
    }
    std::puts("nothrow allocation and constructor cleanup passed");
    return 0;
}