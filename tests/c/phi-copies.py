#!/usr/bin/env python3
"""phi-copies.py <seed>: prints a C program whose run() is a dispatch loop of more than 512 LLVM values, the shape whose
phis ShareFunctionLocals gives the copied values' locals (PF-22): each case rotates variables of one type in cycles
(parallel assignments, swaps among them), updates others from each other, forwards values through conditional
branches and small inner loops, and several cases share one target. The program prints every variable, the values
some of them had before the last pass (copies live past the loop) and a step count, for comparison with native."""
import random
import sys

seed = int(sys.argv[1])
rng = random.Random(seed)
types = {"u64": "uint64_t", "u32": "uint32_t", "u8": "uint8_t", "f64": "double", "ptr": "const uint32_t *"}
variables = [(f"v{index}", kind) for index, kind in enumerate(rng.choice(["u64", "u64", "u32", "u32", "u8", "f64", "ptr"]) for _ in range(40))]
by_kind = {kind: [name for name, k in variables if k == kind] for kind in types}
integers = [name for name, kind in variables if kind in ("u64", "u32", "u8")]
cases = 44


def value(name):
    """An integer view of a variable."""
    kind = dict(variables)[name]
    if kind == "f64":
        return f"(uint64_t)({name} * 16.0)"
    if kind == "ptr":
        return f"(uint64_t)*{name}"
    return f"(uint64_t){name}"


def assign(target, expression):
    """target = an integer expression, kept in range for its type."""
    kind = dict(variables)[target]
    if kind == "f64":
        return f"{target} = {target} * 0.75 + (double)(({expression}) & 255);"
    if kind == "ptr":
        return f"{target} = table + (({expression}) & 63);"
    return f"{target} = ({types[kind]})({expression});"


def update():
    target = rng.choice([name for name, _ in variables])
    left, right = rng.sample(integers, 2)
    operation = rng.choice(["+", "^", "-", "*", "|"])
    shift = rng.randrange(0, 13)
    return assign(target, f"({value(left)} {operation} ({value(right)} >> {shift})) + {rng.randrange(1, 1000)}u")


def rotation():
    kind = rng.choice([kind for kind, names in by_kind.items() if len(names) >= 2])
    names = rng.sample(by_kind[kind], rng.randrange(2, min(len(by_kind[kind]), 7) + 1))
    temporaries = [f"t{index}" for index in range(len(names))]
    lines = [f"{types[kind]} {t} = {name};" for t, name in zip(temporaries, names)]
    # A cycle: each variable takes the next one's old value (a swap for two).
    lines += [f"{name} = {temporaries[(index + 1) % len(names)]};" for index, name in enumerate(names)]
    return "{ " + " ".join(lines) + " }"


def forward():
    kind = rng.choice([kind for kind, names in by_kind.items() if len(names) >= 2])
    first, second = rng.sample(by_kind[kind], 2)
    test = rng.choice(integers)
    return f"if ({value(test)} & {1 << rng.randrange(0, 6)}u) {first} = {second}; else {{ {update()} }}"


def inner():
    kind = rng.choice([kind for kind, names in by_kind.items() if len(names) >= 2])
    first, second = rng.sample(by_kind[kind], 2)
    bound, counter = rng.sample(integers, 2)
    return (f"for (unsigned j = 0; j < ({value(bound)} & 7u); j++) {{ {types[kind]} t = {first}; {first} = {second}; "
            f"{second} = t; {assign(counter, f'{value(counter)} + j + 1u')} }}")


print("#include <stdint.h>\n#include <stdio.h>\n#include <stdlib.h>\n")
print("static uint32_t table[64];\n")
print("static void __attribute__((noinline)) run(uint64_t seed, unsigned steps) {")
for index, (name, kind) in enumerate(variables):
    if kind == "f64":
        print(f"    double {name} = (double)((seed >> {index % 17}) & 1023);")
    elif kind == "ptr":
        print(f"    const uint32_t *{name} = table + ((seed + {index}) & 63);")
    else:
        print(f"    {types[kind]} {name} = ({types[kind]})(seed * {2 * index + 1}u + {index});")
watched = rng.sample([name for name, _ in variables], 4)
for name in watched:
    print(f"    {types[dict(variables)[name]]} before_{name} = {name};")
print(f"    unsigned state = (unsigned)(seed % {cases}u), count = 0;")
print("    while (steps-- != 0) {")
for name in watched:
    print(f"        before_{name} = {name};")
print("        count++;")
print("        switch (state) {")
for case in range(cases):
    # Some cases share a body (the same target for several case values).
    print(f"        case {case}:")
    if rng.random() < 0.2 and case + 1 < cases:
        continue
    body = [rng.choice([rotation, update, update, forward, inner, rotation])() for _ in range(rng.randrange(3, 8))]
    for line in body:
        print(f"            {line}")
    selector = rng.choice(integers)
    print(f"            state = (unsigned)(({value(selector)} + {rng.randrange(1, 97)}u) % {cases}u);")
    print("            break;")
print("        default: state = 0; break;")
print("        }")
print("    }")
for name, kind in variables + [(f"before_{name}", dict(variables)[name]) for name in watched]:
    shown = name.removeprefix("before_")
    if kind == "f64" or dict(variables).get(shown) == "f64":
        print(f'    printf("{name} %.17g\\n", {name});')
    elif kind == "ptr" or dict(variables).get(shown) == "ptr":
        print(f'    printf("{name} %ld\\n", (long)({name} - table));')
    else:
        print(f'    printf("{name} %llu\\n", (unsigned long long){name});')
print('    printf("count %u state %u\\n", count, state);')
print("}\n")
print("int main(int argc, char **argv) {")
print("    for (unsigned index = 0; index < 64; index++) table[index] = index * 2654435761u >> 7;")
print("    unsigned steps = argc > 1 ? (unsigned)strtoul(argv[1], 0, 10) : 100000;")
print("    for (uint64_t seed = 1; seed <= 3; seed++) run(seed * 0x9e3779b97f4a7c15u, steps);")
print("    return 0;")
print("}")
