#include <vector>
#include <algorithm>
#include <array>
#include <cstdio>
#include <cstdint>
#include <stdexcept>
static_assert(sizeof(long) == 8);
static_assert(sizeof(wchar_t) == 4);
static_assert(sizeof(long double) == 16);
int main()
{
    std::vector<int> values;
    for (int index = 20; index > 0; index--) values.push_back(index);
    std::sort(values.begin(), values.end());
    std::array<int, 3> selected = {values[0], values[9], values[19]};
    if (selected[0] != 1 || selected[1] != 10 || selected[2] != 20) return 1;
    try { (void)values.at(100); return 2; }
    catch (const std::exception &error) { if (!error.what() || !error.what()[0]) return 3; }
    struct alignas(256) Aligned { int value = 42; };
    Aligned *aligned = new Aligned;
    if ((reinterpret_cast<std::uintptr_t>(aligned) & 255) || aligned->value != 42) return 4;
    delete aligned;
    std::printf("portable libc++: size=%zu first=%d last=%d\n", values.size(), values.front(), values.back());
    return 0;
}