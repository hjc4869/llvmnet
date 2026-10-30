// Catch clause selection: catch (...) after a typed handler whose type has the program's first type id, and pointer
// handlers, which receive the thrown pointer's value and convert to qualified, void and base-class pointers.
#include <cstdio>

struct Base {
    virtual ~Base() {}
    int value = 7;
};
struct Other {
    int other = 3;
};
struct Derived : Other, Base {
    int extra = 9;
};

int main()
{
    try {
        throw 1.5;
    } catch (int) {
        std::puts("int handler");
        return 1;
    } catch (...) {
        std::puts("catch-all handler");
    }
    typedef char Array[4];
    Array text = {'H', 'i', '!', 0};
    try {
        throw text;
    } catch (Array &) {
        std::puts("array handler");
        return 1;
    } catch (const char *pointer) {
        std::printf("pointer handler: %s\n", pointer);
    }
    try {
        try {
            throw "literal";
        } catch (char *) {
            std::puts("dropped const");
            return 1;
        }
    } catch (const char *literal) {
        std::printf("kept const: %s\n", literal);
    }
    try {
        throw new Derived;
    } catch (void *any) {
        std::printf("void pointer: %d\n", any != nullptr);
        delete static_cast<Derived *>(any);
    }
    try {
        throw new Derived;
    } catch (Base *base) {
        std::printf("second base: %d\n", base->value);
        delete base;
    }
    try {
        throw static_cast<Derived *>(nullptr);
    } catch (Derived *null) {
        std::printf("null pointer: %d\n", null == nullptr);
    }
    return 0;
}
