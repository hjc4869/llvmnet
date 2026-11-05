#!/usr/bin/env python3
"""Generate LLVM IR semantics probes.

Each probe is a noinline optnone function taking and returning bit patterns, so neither the native
pipeline nor llvmnet can fold it. main() calls every probe with edge-case operands and prints
"<case> <64-bit chunks of the result, least significant first>". The natively compiled module is the
oracle. Undefined behavior and poison (division by zero, signed overflow in sdiv, shift amounts >= width,
out-of-range fptoi) is never generated. NaN results are canonicalized except for the bitwise
fneg/fabs/copysign, because LLVM does not specify NaN payloads or signs for other operations; the sign of
a zero minnum/maxnum result is likewise unspecified and cleared.

Usage: generate.py <output-directory> [abi]   writes integer.ll, float.ll, extended.ll and cases.tsv
"""
import os
import random
import struct
import sys

random.seed(20261030)


def ints(width):
    if width == 1:
        return [0, 1]
    mask = (1 << width) - 1
    values = {0, 1, 2, mask, mask - 1, (1 << (width - 1)) - 1, 1 << (width - 1), (1 << (width - 1)) + 1}
    values |= {random.getrandbits(width) for _ in range(3)}
    if width > 8:
        values |= {0x5a & mask, (0x5a << (width // 2)) & mask}
    return sorted(values)


def signed(value, width):
    return value - (1 << width) if value >> (width - 1) & 1 else value


def literal(value, width):
    return str(signed(value, width)) if width > 1 else ("true" if value else "false")


class Module:
    def __init__(self, abi):
        self.functions, self.calls, self.cases, self.abi = [], [], [], abi
        self.names = set()

    def probe(self, name, params, result_width, body):
        """body: list of IR lines using %a, %b, %c; must set %out (an i<result_width> value)."""
        if name not in self.names:
            self.names.add(name)
            args = ", ".join(f"i{w} %{n}" for n, w in zip("abc", params))
            self.functions.append(f"define internal i{result_width} @{name}({args}) #0 {{\nentry:\n  " +
                                  "\n  ".join(body) + f"\n  ret i{result_width} %out\n}}\n")
        return name

    def call(self, function, params, values, result_width, description):
        index = len(self.cases)
        self.cases.append(description)
        args = ", ".join(f"i{w} {literal(v, w)}" for w, v in zip(params, values))
        lines = [f"%r{index} = call i{result_width} @{function}({args})"]
        chunks = (result_width + 63) // 64
        wide = f"%r{index}"
        if result_width != 64 * chunks:
            lines.append(f"%w{index} = zext i{result_width} %r{index} to i{64 * chunks}")
            wide = f"%w{index}"
        parts = []
        for chunk in range(chunks):
            shifted = wide
            if chunk:
                lines.append(f"%s{index}_{chunk} = lshr i{64 * chunks} {wide}, {64 * chunk}")
                shifted = f"%s{index}_{chunk}"
            if chunks == 1:
                parts.append(shifted)
            else:
                lines.append(f"%p{index}_{chunk} = trunc i{64 * chunks} {shifted} to i64")
                parts.append(f"%p{index}_{chunk}")
        arguments = ", ".join(f"i64 {part}" for part in parts)
        lines.append(f"call i32 (ptr, ...) @printf(ptr @format{chunks}, i32 {index}, {arguments})")
        self.calls += lines

    def write(self, path):
        with open(path, "w") as out:
            out.write('target triple = "x86_64-pc-linux-gnu"\n')
            for chunks in (1, 2, 3):
                text = "%d" + " %llx" * chunks + "\\0A\\00"
                out.write(f'@format{chunks} = private constant [{4 + 5 * chunks} x i8] c"{text}"\n')
            out.write("declare i32 @printf(ptr, ...)\n\n")
            out.write("".join(self.functions))
            out.write("define i32 @main() {\nentry:\n  " + "\n  ".join(self.calls) + "\n  ret i32 0\n}\n")
            out.write("attributes #0 = { noinline optnone }\n")
            if self.abi:
                out.write(f'!llvm.module.flags = !{{!0}}\n!0 = !{{i32 1, !"llvmnet.abi", !"{self.abi}"}}\n')


def integer_module(m):
    widths = [1, 7, 8, 16, 32, 33, 64, 65, 100, 128]
    binary = ["add", "sub", "mul", "and", "or", "xor", "shl", "lshr", "ashr", "udiv", "sdiv", "urem", "srem"]
    for w in widths:
        for op in binary:
            f = m.probe(f"{op}_i{w}", [w, w], w, [f"%out = {op} i{w} %a, %b"])
            for a in ints(w):
                for b in ints(w):
                    if op in ("udiv", "urem", "sdiv", "srem") and b == 0:
                        continue
                    if op in ("sdiv", "srem") and a == 1 << (w - 1) and b == (1 << w) - 1:
                        continue
                    if op in ("shl", "lshr", "ashr") and b >= w:
                        continue
                    m.call(f, [w, w], [a, b], w, f"{op} i{w} {a:#x} {b:#x}")
        for predicate in ["eq", "ne", "ugt", "uge", "ult", "ule", "sgt", "sge", "slt", "sle"]:
            f = m.probe(f"icmp_{predicate}_i{w}", [w, w], 1, [f"%out = icmp {predicate} i{w} %a, %b"])
            for a in ints(w)[::2]:
                for b in ints(w)[::2]:
                    m.call(f, [w, w], [a, b], 1, f"icmp {predicate} i{w} {a:#x} {b:#x}")
        for target in widths:
            if target == w:
                continue
            casts = ["zext", "sext"] if target > w else ["trunc"]
            for cast in casts:
                f = m.probe(f"{cast}_i{w}_i{target}", [w], target, [f"%out = {cast} i{w} %a to i{target}"])
                for a in ints(w):
                    m.call(f, [w], [a], target, f"{cast} i{w}->i{target} {a:#x}")
        unary = [("ctpop", ""), ("ctlz", ", i1 false"), ("cttz", ", i1 false"), ("bitreverse", ""), ("abs", ", i1 false")]
        if w % 16 == 0:
            unary.append(("bswap", ""))
        for name, extra in unary:
            if w == 1 and name == "abs":
                continue
            f = m.probe(f"{name}_i{w}", [w], w, [f"%out = call i{w} @llvm.{name}.i{w}(i{w} %a{extra})"])
            m.declare(f"declare i{w} @llvm.{name}.i{w}(i{w}{', i1' if extra else ''})")
            for a in ints(w):
                m.call(f, [w], [a], w, f"{name} i{w} {a:#x}")
        for name in ["smax", "smin", "umax", "umin", "sadd.sat", "uadd.sat", "ssub.sat", "usub.sat"]:
            f = m.probe(f"{name.replace('.', '_')}_i{w}", [w, w], w, [f"%out = call i{w} @llvm.{name}.i{w}(i{w} %a, i{w} %b)"])
            m.declare(f"declare i{w} @llvm.{name}.i{w}(i{w}, i{w})")
            for a in ints(w):
                for b in ints(w)[::3]:
                    m.call(f, [w, w], [a, b], w, f"{name} i{w} {a:#x} {b:#x}")
        for name in ["uadd", "sadd", "usub", "ssub", "umul", "smul"]:
            m.declare(f"declare {{i{w}, i1}} @llvm.{name}.with.overflow.i{w}(i{w}, i{w})")
            for part, width, extract in [("value", w, 0), ("flag", 1, 1)]:
                f = m.probe(f"{name}_overflow_{part}_i{w}", [w, w], width,
                            [f"%pair = call {{i{w}, i1}} @llvm.{name}.with.overflow.i{w}(i{w} %a, i{w} %b)",
                             f"%out = extractvalue {{i{w}, i1}} %pair, {extract}"])
                for a in ints(w)[::2]:
                    for b in ints(w)[::2]:
                        m.call(f, [w, w], [a, b], width, f"{name}.with.overflow.{part} i{w} {a:#x} {b:#x}")
        if 1 < w <= 64:
            for name in ["sshl.sat", "ushl.sat"]:
                f = m.probe(f"{name.replace('.', '_')}_i{w}", [w, w], w, [f"%out = call i{w} @llvm.{name}.i{w}(i{w} %a, i{w} %b)"])
                m.declare(f"declare i{w} @llvm.{name}.i{w}(i{w}, i{w})")
                for a in ints(w):
                    for b in [0, 1, 2, w // 2, w - 1]:
                        m.call(f, [w, w], [a, b], w, f"{name} i{w} {a:#x} {b}")
        if w > 1:
            for name in ["fshl", "fshr"]:
                f = m.probe(f"{name}_i{w}", [w, w, w], w, [f"%out = call i{w} @llvm.{name}.i{w}(i{w} %a, i{w} %b, i{w} %c)"])
                m.declare(f"declare i{w} @llvm.{name}.i{w}(i{w}, i{w}, i{w})")
                for a in ints(w)[::3]:
                    for c in [0, 1, w - 1, w, w + 3, (1 << w) - 1]:
                        m.call(f, [w, w, w], [a, ints(w)[-2], c & ((1 << w) - 1)], w, f"{name} i{w} {a:#x} {c:#x}")
                # A rotate: both halves are the same value, which the backend lowers separately.
                f = m.probe(f"{name}_rotate_i{w}", [w, w], w, [f"%out = call i{w} @llvm.{name}.i{w}(i{w} %a, i{w} %a, i{w} %b)"])
                for a in ints(w)[::3]:
                    for c in [0, 1, w - 1, w, w + 3, (1 << w) - 1]:
                        m.call(f, [w, w], [a, c & ((1 << w) - 1)], w, f"{name} rotate i{w} {a:#x} {c:#x}")
            for name in ["scmp", "ucmp"]:
                f = m.probe(f"{name}_i{w}", [w, w], 8, [f"%out = call i8 @llvm.{name}.i8.i{w}(i{w} %a, i{w} %b)"])
                m.declare(f"declare i8 @llvm.{name}.i8.i{w}(i{w}, i{w})")
                for a in ints(w)[::2]:
                    for b in ints(w)[::2]:
                        m.call(f, [w, w], [a, b], 8, f"{name} i{w} {a:#x} {b:#x}")


def switch_module(m):
    """Switches (JP-03): dense, negative, wrapping, sign-boundary, sparse and ranged case values, shared targets,
    an unreachable default, and tables of the fewest cases, called with every case value, its neighbours and edge
    values. Each target returns its own number; one reaches the join block directly, so the switch's own edges store
    a phi that depends on the condition. The probes use their own random generator, so the other modules' values stay
    as they were."""
    rng = random.Random(20261105)
    for w in [7, 8, 16, 32, 33, 64, 128]:
        mask = (1 << w) - 1
        sign = 1 << (w - 1)
        sets = {
            "dense": [(v, v % 5) for v in range(12) if v not in (3, 8)],
            "negative": [(v & mask, v % 4) for v in range(-6, 7) if v != -2],
            "top": [((mask - v) & mask, v % 3) for v in range(10)],
            "signbound": [((sign + v) & mask, v % 4) for v in range(-4, 4)],
            "sparse": [(1 << k, k % 6) for k in range(0, w, max(1, w // 9))] + [(rng.getrandbits(w), 6) for _ in range(4)],
            "ranges": [(v, 1) for v in range(10, 20)] + [(v, 2) for v in range(20, 30)] + [(100, 3)] +
                      [(v, 4) for v in range(1000 & mask, (1000 & mask) + 4)] + [((mask - v) & mask, 5) for v in range(3)],
            "mixed": [(v, v % 3) for v in range(16)] + [(sign >> 1, 3), (mask, 4), (sign, 5)] + [(v & mask, 6) for v in range(200, 231)],
            "bits": [(v, 1) for v in (1, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31)] + [(40, 2)],
            "few3": [(v, v) for v in range(3)],
            "few4": [(v, v) for v in range(4)],
            "few5": [(v + 2, v) for v in range(5)],
        }
        for name, cases in sets.items():
            seen, unique = set(), []
            for value, target in cases:
                if value & mask not in seen:
                    seen.add(value & mask)
                    unique.append((value & mask, target))
            switch_probe(m, f"switch_{name}_i{w}", w, unique, False, sorted(seen) + [0, 2, mask, sign, 0x5a & mask, rng.getrandbits(w)])
        switch_probe(m, f"switch_unreachable_i{w}", w, [(v, v % 3) for v in range(8)], True, list(range(8)))


def switch_probe(m, name, w, cases, unreachable, inputs):
    mask = (1 << w) - 1
    targets = sorted({target for _, target in cases})
    entries = "  ".join(f"i{w} {literal(value, w)}, label %t{target}" for value, target in cases)
    extend = "zext" if w < 32 else "trunc" if w > 32 else None
    body = [f"%x = {extend} i{w} %a to i32" if extend else "%x = add i32 %a, 0",
            f"switch i{w} %a, label %default [ {entries} ]"]
    for target in targets[1:]:
        body += [f"t{target}:", "br label %join"]
    body += ["default:", "unreachable" if unreachable else "br label %join", "join:"]
    incoming = [f"[ %x, %entry ]"] * sum(target == targets[0] for _, target in cases) + [f"[ {target + 1}, %t{target} ]" for target in targets[1:]]
    body.append("%out = phi i32 " + ", ".join(incoming + ([] if unreachable else ["[ -1, %default ]"])))
    # The first target is the join block itself, reached from the switch's block.
    body[1] = body[1].replace(f"label %t{targets[0]}", "label %join")
    f = m.probe(name, [w], 32, body)
    for value in dict.fromkeys(v & mask for value in inputs for v in (value - 1, value, value + 1)):
        if not unreachable or any(value == case for case, _ in cases):
            m.call(f, [w], [value], 32, f"{name} {value:#x}")


FLOATS = {
    32: ("float", "<f", "<I", 0x7fc00000),
    64: ("double", "<d", "<Q", 0x7ff8000000000000),
}


def float_bits(width, value):
    _, pack, unpack, _ = FLOATS[width]
    return struct.unpack(unpack, struct.pack(pack, value))[0]


def float_values(width):
    specials = [0.0, -0.0, 1.0, -1.0, 0.5, 1.5, -2.5, 3.0, 0.1, 1e30 if width == 32 else 1e300, -7.25e-3,
                float("inf"), float("-inf"), 2.0 ** 24 + 1, -(2.0 ** 31), 123456.789]
    bits = [float_bits(width, v) for v in specials]
    bits += [1, (1 << (width - 1)) | 1]                       # smallest denormals
    bits += [0x7f7fffff if width == 32 else 0x7fefffffffffffff]  # largest finite
    bits += [FLOATS[width][3], FLOATS[width][3] | (1 << (width - 1))]  # NaN, negative NaN
    return bits


def float_module(m):
    for w, (name, _, _, canonical) in FLOATS.items():
        values = float_values(w)
        canon = [f"%isnan = fcmp uno {name} %r, %r", f"%bits = bitcast {name} %r to i{w}",
                 f"%out = select i1 %isnan, i{w} {signed(canonical, w)}, i{w} %bits"]
        load2 = [f"%x = bitcast i{w} %a to {name}", f"%y = bitcast i{w} %b to {name}"]
        for op in ["fadd", "fsub", "fmul", "fdiv", "frem"]:
            f = m.probe(f"{op}_{name}", [w, w], w, load2 + [f"%r = {op} {name} %x, %y"] + canon)
            for a in values:
                for b in values[::2]:
                    m.call(f, [w, w], [a, b], w, f"{op} {name} {a:#x} {b:#x}")
        f = m.probe(f"fneg_{name}", [w], w, [f"%x = bitcast i{w} %a to {name}", f"%r = fneg {name} %x", f"%out = bitcast {name} %r to i{w}"])
        for a in values:
            m.call(f, [w], [a], w, f"fneg {name} {a:#x}")
        for predicate in ["false", "oeq", "ogt", "oge", "olt", "ole", "one", "ord", "ueq", "ugt", "uge", "ult", "ule", "une", "uno", "true"]:
            f = m.probe(f"fcmp_{predicate}_{name}", [w, w], 1, load2 + [f"%out = fcmp {predicate} {name} %x, %y"])
            for a in values[::2]:
                for b in values[1::2]:
                    m.call(f, [w, w], [a, b], 1, f"fcmp {predicate} {name} {a:#x} {b:#x}")
        bitwise = {"fabs": [f"%r = call {name} @llvm.fabs.f{w}({name} %x)"]}
        rounding = ["floor", "ceil", "trunc", "rint", "nearbyint", "round", "roundeven", "sqrt"]
        for intrinsic in rounding:
            f = m.probe(f"{intrinsic}_{name}", [w], w, [f"%x = bitcast i{w} %a to {name}", f"%r = call {name} @llvm.{intrinsic}.f{w}({name} %x)"] + canon)
            m.declare(f"declare {name} @llvm.{intrinsic}.f{w}({name})")
            for a in values:
                m.call(f, [w], [a], w, f"{intrinsic} {name} {a:#x}")
        for intrinsic, body in bitwise.items():
            f = m.probe(f"{intrinsic}_{name}", [w], w, [f"%x = bitcast i{w} %a to {name}"] + body + [f"%out = bitcast {name} %r to i{w}"])
            m.declare(f"declare {name} @llvm.{intrinsic}.f{w}({name})")
            for a in values:
                m.call(f, [w], [a], w, f"{intrinsic} {name} {a:#x}")
        f = m.probe(f"copysign_{name}", [w, w], w, load2 + [f"%r = call {name} @llvm.copysign.f{w}({name} %x, {name} %y)", f"%out = bitcast {name} %r to i{w}"])
        m.declare(f"declare {name} @llvm.copysign.f{w}({name}, {name})")
        for a in values[::2]:
            for b in values[1::3]:
                m.call(f, [w, w], [a, b], w, f"copysign {name} {a:#x} {b:#x}")
        for intrinsic in ["minnum", "maxnum", "minimum", "maximum", "minimumnum", "maximumnum"]:
            # minnum/maxnum may return either operand when they compare equal, so the sign of a zero result is unspecified.
            zero_sign = [f"%r0 = call {name} @llvm.{intrinsic}.f{w}({name} %x, {name} %y)", f"%iszero = fcmp oeq {name} %r0, 0.0",
                         f"%r = select i1 %iszero, {name} 0.0, {name} %r0"] if intrinsic in ("minnum", "maxnum") else \
                        [f"%r = call {name} @llvm.{intrinsic}.f{w}({name} %x, {name} %y)"]
            f = m.probe(f"{intrinsic}_{name}", [w, w], w, load2 + zero_sign + canon)
            m.declare(f"declare {name} @llvm.{intrinsic}.f{w}({name}, {name})")
            for a in values[::2]:
                for b in values[1::2]:
                    m.call(f, [w, w], [a, b], w, f"{intrinsic} {name} {a:#x} {b:#x}")
        f = m.probe(f"fma_{name}", [w, w, w], w, load2 + [f"%z = bitcast i{w} %c to {name}", f"%r = call {name} @llvm.fma.f{w}({name} %x, {name} %y, {name} %z)"] + canon)
        m.declare(f"declare {name} @llvm.fma.f{w}({name}, {name}, {name})")
        for a in values[::3]:
            for b in values[1::3]:
                m.call(f, [w, w, w], [a, b, values[4]], w, f"fma {name} {a:#x} {b:#x}")
        for mask in [1, 2, 3, 4, 8, 16, 32, 64, 128, 256, 512, 1023, 0x60, 0x90]:
            f = m.probe(f"fpclass_{mask}_{name}", [w], 1, [f"%x = bitcast i{w} %a to {name}", f"%out = call i1 @llvm.is.fpclass.f{w}({name} %x, i32 {mask})"])
            m.declare(f"declare i1 @llvm.is.fpclass.f{w}({name}, i32)")
            for a in values:
                m.call(f, [w], [a], 1, f"is.fpclass {mask} {name} {a:#x}")
        f = m.probe(f"ldexp_{name}", [w, 32], w, [f"%x = bitcast i{w} %a to {name}", f"%r = call {name} @llvm.ldexp.f{w}.i32({name} %x, i32 %b)"] + canon)
        m.declare(f"declare {name} @llvm.ldexp.f{w}.i32({name}, i32)")
        for a in values[::2]:
            for e in [0, 1, -1, 10, -1074, 2000, -2000]:
                m.call(f, [w, 32], [a, e & 0xffffffff], w, f"ldexp {name} {a:#x} {e}")
        for target in [32, 64, 128]:
            for cast, low, high in [("fptosi", -(2 ** (target - 1)), 2 ** (target - 1)), ("fptoui", 0, 2 ** target)]:
                f = m.probe(f"{cast}_{name}_i{target}", [w], target, [f"%x = bitcast i{w} %a to {name}", f"%out = {cast} {name} %x to i{target}"])
                for a in values:
                    value = struct.unpack(FLOATS[w][1], struct.pack(FLOATS[w][2], a))[0]
                    if value != value or value in (float("inf"), float("-inf")) or not (low - 1 < value < high):
                        continue  # poison
                    m.call(f, [w], [a], target, f"{cast} {name}->i{target} {a:#x}")
                saturating = f"{cast}.sat"
                f = m.probe(f"{cast}_sat_{name}_i{target}", [w], target, [f"%x = bitcast i{w} %a to {name}", f"%out = call i{target} @llvm.{saturating}.i{target}.f{w}({name} %x)"])
                m.declare(f"declare i{target} @llvm.{saturating}.i{target}.f{w}({name})")
                for a in values:
                    m.call(f, [w], [a], target, f"{saturating} {name}->i{target} {a:#x}")
            for cast in ["sitofp", "uitofp"]:
                f = m.probe(f"{cast}_i{target}_{name}", [target], w, [f"%r = {cast} i{target} %a to {name}", f"%out = bitcast {name} %r to i{w}"])
                for a in ints(target) + [(1 << 53) + 1, (1 << 24) + 1, (1 << 63) + 1025]:
                    a &= (1 << target) - 1
                    m.call(f, [target], [a], w, f"{cast} i{target}->{name} {a:#x}")
    f = m.probe("fptrunc", [64], 32, ["%x = bitcast i64 %a to double", "%r = fptrunc double %x to float"] +
                ["%isnan = fcmp uno float %r, %r", "%bits = bitcast float %r to i32", f"%out = select i1 %isnan, i32 {signed(0x7fc00000, 32)}, i32 %bits"])
    for a in float_values(64) + [float_bits(64, 1 + 2.0 ** -24), float_bits(64, 1 + 3 * 2.0 ** -25), float_bits(64, 1e-40), float_bits(64, 3.5e38)]:
        m.call(f, [64], [a], 32, f"fptrunc {a:#x}")
    f = m.probe("fpext", [32], 64, ["%x = bitcast i32 %a to float", "%r = fpext float %x to double"] +
                ["%isnan = fcmp uno double %r, %r", "%bits = bitcast double %r to i64", f"%out = select i1 %isnan, i64 {signed(0x7ff8000000000000, 64)}, i64 %bits"])
    for a in float_values(32):
        m.call(f, [32], [a], 64, f"fpext {a:#x}")


def x87(value):
    """Exact x87 80-bit encoding of a double."""
    bits = struct.unpack("<Q", struct.pack("<d", value))[0]
    sign, exponent, fraction = bits >> 63, (bits >> 52) & 0x7ff, bits & ((1 << 52) - 1)
    if exponent == 0x7ff:
        stored, significand = 0x7fff, (1 << 63) | (fraction << 11)
    elif exponent == 0:
        if fraction == 0:
            stored, significand = 0, 0
        else:
            significand = fraction << (64 - fraction.bit_length())
            stored = fraction.bit_length() - 1 - 1074 + 16383
    else:
        stored, significand = exponent - 1023 + 16383, (1 << 63) | (fraction << 11)
    return sign << 79 | stored << 64 | significand


def extended_module(m):
    """x86_fp80 (long double on the portable ABI): software Float80 arithmetic in llvmnet."""
    name, canonical = "x86_fp80", 0x7fffc000000000000000
    values = [x87(v) for v in [0.0, -0.0, 1.0, -1.0, 0.5, 1.5, -2.5, 3.0, 0.1, 1e300, -7.25e-3, 123456.789, float("inf"), float("-inf")]]
    values += [16383 << 64 | 0x8000000000000001, 0x7ffe << 64 | 0xffffffffffffffff, 1, 1 << 64 | 1 << 63,
                0x3ffd << 64 | 0xaaaaaaaaaaaaaaab, canonical, canonical | 1 << 79]
    canon = [f"%isnan = fcmp uno {name} %r, %r", f"%bits = bitcast {name} %r to i80", f"%out = select i1 %isnan, i80 {signed(canonical, 80)}, i80 %bits"]
    load2 = [f"%x = bitcast i80 %a to {name}", f"%y = bitcast i80 %b to {name}"]
    for op in ["fadd", "fsub", "fmul", "fdiv"]:
        f = m.probe(f"{op}_f80", [80, 80], 80, load2 + [f"%r = {op} {name} %x, %y"] + canon)
        for a in values:
            for b in values[::2]:
                m.call(f, [80, 80], [a, b], 80, f"{op} {name} {a:#x} {b:#x}")
    f = m.probe("fneg_f80", [80], 80, [f"%x = bitcast i80 %a to {name}", f"%r = fneg {name} %x", f"%out = bitcast {name} %r to i80"])
    for a in values:
        m.call(f, [80], [a], 80, f"fneg {name} {a:#x}")
    for predicate in ["oeq", "ogt", "oge", "olt", "ole", "one", "ord", "ueq", "ugt", "uge", "ult", "ule", "une", "uno"]:
        f = m.probe(f"fcmp_{predicate}_f80", [80, 80], 1, load2 + [f"%out = fcmp {predicate} {name} %x, %y"])
        for a in values[::2]:
            for b in values[1::2]:
                m.call(f, [80, 80], [a, b], 1, f"fcmp {predicate} {name} {a:#x} {b:#x}")
    for intrinsic in ["fabs", "floor"]:
        f = m.probe(f"{intrinsic}_f80", [80], 80, [f"%x = bitcast i80 %a to {name}", f"%r = call {name} @llvm.{intrinsic}.f80({name} %x)"] + canon)
        m.declare(f"declare {name} @llvm.{intrinsic}.f80({name})")
        for a in values:
            m.call(f, [80], [a], 80, f"{intrinsic} {name} {a:#x}")
    f = m.probe("fma_f80", [80, 80, 80], 80, load2 + [f"%z = bitcast i80 %c to {name}", f"%r = call {name} @llvm.fma.f80({name} %x, {name} %y, {name} %z)"] + canon)
    m.declare(f"declare {name} @llvm.fma.f80({name}, {name}, {name})")
    for a in values[::3]:
        for b in values[1::3]:
            m.call(f, [80, 80, 80], [a, b, values[4]], 80, f"fma {name} {a:#x} {b:#x}")
    for mask in [1, 2, 3, 4, 8, 16, 32, 64, 128, 256, 512, 1023, 0x60, 0x90]:
        f = m.probe(f"fpclass_{mask}_f80", [80], 1, [f"%x = bitcast i80 %a to {name}", f"%out = call i1 @llvm.is.fpclass.f80({name} %x, i32 {mask})"])
        m.declare(f"declare i1 @llvm.is.fpclass.f80({name}, i32)")
        for a in values:
            m.call(f, [80], [a], 1, f"is.fpclass {mask} {name} {a:#x}")
    for intrinsic in ["lround", "llrint"]:
        f = m.probe(f"{intrinsic}_f80", [80], 64, [f"%x = bitcast i80 %a to {name}", f"%out = call i64 @llvm.{intrinsic}.i64.f80({name} %x)"])
        m.declare(f"declare i64 @llvm.{intrinsic}.i64.f80({name})")
        for a in values + [x87(2.5), x87(-2.5), x87(3.5), x87(-0.5), x87(1e18), 16447 << 64 | 0xc000000000000000]:
            exponent = a >> 64 & 0x7fff
            if exponent == 0x7fff or exponent >= 16383 + 62:
                continue  # NaN, infinity and out-of-range results are unspecified
            m.call(f, [80], [a], 64, f"{intrinsic} {name} {a:#x}")
    for width, short in [(32, "float"), (64, "double")]:
        f = m.probe(f"fptrunc_f80_{short}", [80], width, [f"%x = bitcast i80 %a to {name}", f"%r = fptrunc {name} %x to {short}",
            f"%isnan = fcmp uno {short} %r, %r", f"%bits = bitcast {short} %r to i{width}", f"%out = select i1 %isnan, i{width} {signed(FLOATS[width][3], width)}, i{width} %bits"])
        for a in values:
            m.call(f, [80], [a], width, f"fptrunc {name}->{short} {a:#x}")
        f = m.probe(f"fpext_{short}_f80", [width], 80, [f"%x = bitcast i{width} %a to {short}", f"%r = fpext {short} %x to {name}"] + canon)
        for a in float_values(width):
            m.call(f, [width], [a], 80, f"fpext {short}->{name} {a:#x}")
    for target in [32, 64]:
        for cast in ["sitofp", "uitofp"]:
            f = m.probe(f"{cast}_i{target}_f80", [target], 80, [f"%r = {cast} i{target} %a to {name}", f"%out = bitcast {name} %r to i80"])
            for a in ints(target):
                m.call(f, [target], [a], 80, f"{cast} i{target}->{name} {a:#x}")
        for cast, low, high in [("fptosi", -(2 ** (target - 1)), 2 ** (target - 1)), ("fptoui", 0, 2 ** target)]:
            f = m.probe(f"{cast}_f80_i{target}", [80], target, [f"%x = bitcast i80 %a to {name}", f"%out = {cast} {name} %x to i{target}"])
            for a in values:
                exponent, significand = a >> 64 & 0x7fff, a & ((1 << 64) - 1)
                if exponent == 0x7fff:
                    continue
                magnitude = significand * 2.0 ** (max(exponent, 1) - 16383 - 63) if exponent < 16383 + 200 else float("inf")
                value = -magnitude if a >> 79 else magnitude
                if not (low - 1 < value < high):
                    continue
                m.call(f, [80], [a], target, f"{cast} {name}->i{target} {a:#x}")


def declare(self, line):
    if not hasattr(self, "declarations"):
        self.declarations = set()
    self.declarations.add(line)


Module.declare = declare
original_write = Module.write


def write(self, path):
    original_write(self, path)
    with open(path, "a") as out:
        out.write("\n".join(sorted(getattr(self, "declarations", set()))) + "\n")


Module.write = write

if __name__ == "__main__":
    directory = sys.argv[1]
    abi = sys.argv[2] if len(sys.argv) > 2 else ""
    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, "cases.tsv"), "w") as cases:
        for name, build in [("integer", integer_module), ("switch", switch_module), ("float", float_module), ("extended", extended_module)]:
            module = Module(abi)
            build(module)
            module.write(os.path.join(directory, f"{name}.ll"))
            for index, description in enumerate(module.cases):
                cases.write(f"{name}\t{index}\t{description}\n")
            print(f"{name}: {len(module.cases)} cases, {len(module.functions)} probes")
