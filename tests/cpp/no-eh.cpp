// Built with -fno-exceptions -fno-rtti: virtual dispatch, templates and containers without landing pads or type_info.
#include <cstdio>
#include <vector>
#include <memory>
struct Shape {
    virtual ~Shape() = default;
    virtual int area() const = 0;
};
struct Square : Shape {
    int side;
    explicit Square(int side) : side(side) {}
    int area() const override { return side * side; }
};
struct Oblong : Shape {
    int width, height;
    Oblong(int width, int height) : width(width), height(height) {}
    int area() const override { return width * height; }
};
template <typename T> T sum(const std::vector<T> &values)
{
    T total{};
    for (const T &value : values) total += value;
    return total;
}
int main()
{
    std::vector<std::unique_ptr<Shape>> shapes;
    shapes.push_back(std::make_unique<Square>(3));
    shapes.push_back(std::make_unique<Oblong>(2, 5));
    std::vector<int> areas;
    for (const auto &shape : shapes) areas.push_back(shape->area());
    std::printf("areas %d %d total %d\n", areas[0], areas[1], sum(areas));
#if defined(__EXCEPTIONS) || defined(__cpp_exceptions)
    std::printf("exceptions enabled\n");
#else
    std::printf("exceptions disabled\n");
#endif
#if defined(__GXX_RTTI) || defined(__cpp_rtti)
    std::printf("rtti enabled\n");
#else
    std::printf("rtti disabled\n");
#endif
    return sum(areas) != 19;
}
