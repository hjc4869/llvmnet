#include <cstdio>
#include <typeinfo>
struct Left { int left = 17; virtual ~Left() = default; };
struct Right { int right = 29; virtual ~Right() = default; };
struct Both : Left, Right { int own = 42; };
struct Other : Left {};
struct Root { int root = 71; virtual ~Root() = default; };
struct Branch1 : virtual Root { int first = 3; };
struct Branch2 : virtual Root { int second = 5; };
struct Diamond : Branch1, Branch2 { int diamond = 99; };
__attribute__((noinline)) static Both *downcast(Left *value) { return dynamic_cast<Both *>(value); }
__attribute__((noinline)) static Right *crosscast(Left *value) { return dynamic_cast<Right *>(value); }
__attribute__((noinline)) static void fail() { throw Both{}; }
__attribute__((noinline)) static void fail_virtual() { throw Diamond{}; }
int main()
{
    Both value;
    Left *left = &value;
    if (downcast(left) != &value || crosscast(left) != static_cast<Right *>(&value)) return 1;
    Other other;
    if (downcast(&other) != nullptr || crosscast(&other) != nullptr) return 2;
    if (typeid(*left) != typeid(Both)) return 3;
    try { fail(); return 4; }
    catch (const Right &right) { if (right.right != 29) return 5; }
    Diamond diamond;
    Root *root = &diamond;
    if (dynamic_cast<Diamond *>(root) != &diamond || dynamic_cast<Branch2 *>(root) != static_cast<Branch2 *>(&diamond)) return 6;
    try { fail_virtual(); return 7; }
    catch (const Root &caught) { if (caught.root != 71) return 8; }
    std::puts("c++ RTTI: multiple inheritance, virtual bases and adjusted catches");
    return 0;
}