/* The program of tests/ext-structs.sh and of the structs fixture of the tests/xp/ bundle (NX-07): every entry point of
 * the synthetic API in tests/extensions/structs/, called directly and through dlsym, printing what each returns.
 * STRUCTS_NO_DLSYM leaves out the calls through dlsym's pointers, which system mode does not make with structures by
 * value. */
#include <dlfcn.h>
#include <inttypes.h>
#include <llvmnet-structs.h>
#include <stdio.h>

int main(void) {
    pairf p = {1.5f, -2.25f};
    pairf scaled = pair_scale(p, 2.0f);
    printf("pair_scale: %.4f %.4f, pair_length2: %.4f\n", scaled.x, scaled.y, pair_length2(scaled));
    mixed m = mixed_make(-41, 0.5);
    printf("mixed_make: %d %.4f, mixed_sum: %.4f\n", m.i, m.d, mixed_sum(m, 3));
    odd17 v;
    for (int i = 0; i < 17; i++) v.bytes[i] = (uint8_t)(i * 13 + 5);
    odd17 r = odd_reverse(v);
    printf("odd_reverse: first %u last %u, odd_sum: %" PRIu32 " %" PRIu32 "\n", r.bytes[0], r.bytes[16], odd_sum(v), odd_sum(r));
    quad a = {1, 2, 3, 4}, b = {0.5, -1, 2.5, 8};
    quad s = quad_add(a, b);
    printf("quad_add: %.2f %.2f %.2f %.2f, quad_dot: %.4f\n", s.a, s.b, s.c, s.d, quad_dot(a, b));
    tagged t = tagged_make(scaled, 99);
    printf("tagged_make: %.4f %.4f tag %d, size %zu\n", t.point.x, t.point.y, t.tag, sizeof t);
    small3 n = small_next((small3){-32768, 255});
    printf("small_next: %d %u\n", n.s, n.c);
    printf("structs_version: %d\n", structs_version());
#ifndef STRUCTS_NO_DLSYM
    void *library = dlopen("libllvmnet-structs.so", RTLD_NOW);
    if (!library) {
        printf("dlopen: %s\n", dlerror());
        return 1;
    }
    quad (*add)(quad, quad) = (quad (*)(quad, quad))dlsym(library, "quad_add");
    mixed (*make)(int32_t, double) = (mixed (*)(int32_t, double))dlsym(library, "mixed_make");
    quad through = add(s, a);
    mixed made = make(12, -0.25);
    printf("through dlsym: quad_add %.2f %.2f %.2f %.2f, mixed_make %d %.4f\n", through.a, through.b, through.c, through.d, made.i, made.d);
#endif
    return 0;
}
