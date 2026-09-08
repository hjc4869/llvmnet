#include <cstdio>

struct Position {
    long offset;
    int state;
};

struct Buffer {
    virtual Position seek(long offset) = 0;
    virtual ~Buffer() {}
};

struct Concrete : Buffer {
    Position seek(long offset) override { return Position{offset + 19, 23}; }
};

__attribute__((noinline)) static Position invoke(Buffer *buffer, long offset)
{
    return buffer->seek(offset);
}

int main()
{
    Concrete buffer;
    Position result = invoke(&buffer, 100);
    if (result.offset != 119 || result.state != 23)
        return 1;
    std::puts("aggregate virtual callbacks passed");
    return 0;
}