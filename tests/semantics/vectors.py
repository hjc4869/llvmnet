#!/usr/bin/env python3
"""Generate the vector forms of the LLVM IR semantics probes (SV-04).

The scalar probes of generate.py, instantiated per element type and lane count: 64-, 96-, 128-, 256- and 512-bit
vectors of i8 to i64, float and double, plus i1 masks, shuffles, lane access and reductions. Each probe is a noinline
optnone function that loads its operands (integer vectors of bit patterns) from memory, computes and stores its result,
so no pipeline can fold it and every width can be passed; main() calls every probe on constants and prints
"<case> <result bytes>". Lanes take the scalar edge values. Undefined behavior (division by zero, sdiv overflow) is
never generated; poison lanes (shifts by the lane width or more, out-of-range fptosi/fptoui) are computed and then
frozen and masked to zero, which checks that they do not disturb the other lanes. NaN results are canonicalized and
the sign of a zero minnum/maxnum result is cleared, as in generate.py.

Usage: vectors.py <output-directory> [abi] [seed]   writes vector-integer.ll, vector-float.ll and vector-cases.tsv
"""
import os
import random
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from generate import FLOATS, float_values, ints, signed  # noqa: E402

INTEGER_SHAPES = {8: [8, 16, 32, 64], 16: [4, 8, 16], 32: [2, 3, 4, 8], 64: [1, 2, 4]}
FLOAT_SHAPES = {32: [2, 3, 4, 8, 16], 64: [2, 4]}


def vector(lanes, width):
    return f"<{lanes} x i{width}>"


def constant(values, width):
    return "<" + ", ".join(f"i{width} {signed(v & ((1 << width) - 1), width)}" for v in values) + ">"


def pack(rows, lanes):
    """Operand lane values for vectors of `lanes` lanes from rows of scalar operands, the last vector padded."""
    for start in range(0, len(rows), lanes):
        chunk = rows[start:start + lanes]
        chunk += [rows[(start + index) % len(rows)] for index in range(lanes - len(chunk))]
        yield [list(column) for column in zip(*chunk)]


def store_size(type_text):
    if type_text.startswith("<"):
        lanes, element = type_text[1:-1].split(" x ")
        bits = {"float": 32, "double": 64}.get(element) or int(element[1:])
        return (int(lanes) * bits + 7) // 8
    return {"float": 4, "double": 8}.get(type_text) or (int(type_text[1:]) + 7) // 8


class Module:
    def __init__(self, abi):
        self.abi, self.functions, self.globals, self.calls, self.cases, self.declarations = abi, [], [], [], [], set()
        self.names = set()

    def probe(self, name, operands, result, body):
        """operands: the types loaded from %pa, %pb and %pc; body sets %r of type `result`, which is stored."""
        if name not in self.names:
            self.names.add(name)
            loads = [f"%{letter} = load {type_text}, ptr %p{letter}" for letter, type_text in zip("abc", operands)]
            self.functions.append(f"define internal void @{name}(ptr %pa, ptr %pb, ptr %pc, ptr %out) #0 {{\nentry:\n  " +
                                  "\n  ".join(loads + body) + f"\n  store {result} %r, ptr %out\n  ret void\n}}\n")
        return (name, operands, result)

    def call(self, probe, values, description):
        name, operands, result = probe
        index = len(self.cases)
        self.cases.append(description)
        pointers = []
        for letter, type_text, lanes in zip("abc", operands, values):
            width = int(type_text[1:-1].split(" x ")[1][1:])
            self.globals.append(f"@k{index}{letter} = private constant {type_text} {constant(lanes, width)}, align 64")
            pointers.append(f"ptr @k{index}{letter}")
        pointers += ["ptr @zero"] * (3 - len(pointers))
        self.calls.append(f"call void @{name}({', '.join(pointers)}, ptr %buffer)")
        self.calls.append(f"call void @show(i32 {index}, ptr %buffer, i32 {store_size(result)})")

    def declare(self, line):
        self.declarations.add(line)

    def write(self, path):
        with open(path, "w") as out:
            out.write('target triple = "x86_64-pc-linux-gnu"\n')
            out.write('@case = private constant [3 x i8] c"%d\\00"\n@byte = private constant [6 x i8] c" %02x\\00"\n')
            out.write('@newline = private constant [2 x i8] c"\\0A\\00"\n@zero = private constant [64 x i8] zeroinitializer, align 64\n')
            out.write("declare i32 @printf(ptr, ...)\n")
            out.write("\n".join(self.globals) + "\n\n")
            out.write("""define internal void @show(i32 %case, ptr %p, i32 %n) {
entry:
  call i32 (ptr, ...) @printf(ptr @case, i32 %case)
  br label %loop
loop:
  %i = phi i32 [ 0, %entry ], [ %next, %body ]
  %done = icmp eq i32 %i, %n
  br i1 %done, label %exit, label %body
body:
  %q = getelementptr i8, ptr %p, i32 %i
  %v = load i8, ptr %q
  %z = zext i8 %v to i32
  call i32 (ptr, ...) @printf(ptr @byte, i32 %z)
  %next = add i32 %i, 1
  br label %loop
exit:
  call i32 (ptr, ...) @printf(ptr @newline)
  ret void
}

""")
            out.write("".join(self.functions))
            out.write("define i32 @main() {\nentry:\n  %buffer = alloca [1024 x i8], align 64\n  " + "\n  ".join(self.calls) + "\n  ret i32 0\n}\n")
            out.write("attributes #0 = { noinline optnone }\n")
            out.write("\n".join(sorted(self.declarations)) + "\n")
            if self.abi:
                out.write(f'!llvm.module.flags = !{{!0}}\n!0 = !{{i32 1, !"llvmnet.abi", !"{self.abi}"}}\n')


def integer_module(m, rng):
    for w, shapes in INTEGER_SHAPES.items():
        values = ints(w)
        for n in shapes:
            t = vector(n, w)
            suffix = f"v{n}i{w}"
            pairs = [(a, b) for a in values for b in values]
            for op in ["add", "sub", "mul", "and", "or", "xor", "udiv", "sdiv", "urem", "srem"]:
                f = m.probe(f"{op}_{suffix}", [t, t], t, [f"%r = {op} {t} %a, %b"])
                rows = [(a, b) for a, b in pairs if not (op in ("udiv", "sdiv", "urem", "srem") and b == 0) and
                        not (op in ("sdiv", "srem") and a == 1 << (w - 1) and b == (1 << w) - 1)]
                for a, b in pack(rows[::2] if op in ("add", "sub", "and", "or", "xor") else rows, n):
                    m.call(f, [a, b], f"{op} {t} {a} {b}")
            # Shift counts at and above the width are poison in their lane only.
            counts = [0, 1, w // 2, w - 1, w, w + 1, (1 << w) - 1]
            for op in ["shl", "lshr", "ashr"]:
                f = m.probe(f"{op}_{suffix}", [t, t, t], t, [f"%s = {op} {t} %a, %b", f"%f = freeze {t} %s", f"%r = and {t} %f, %c"])
                rows = [(a, c & ((1 << w) - 1), (1 << w) - 1 if c < w else 0) for a in values for c in counts]
                for a, b, keep in pack(rows, n):
                    m.call(f, [a, b, keep], f"{op} {t} {a} {b}")
                # One count for every lane, which the lowering shifts by a scalar.
                for c in [0, 1, w - 1]:
                    f = m.probe(f"{op}_splat{c}_{suffix}", [t], t, [f"%r = {op} {t} %a, splat (i{w} {c})"])
                    for (a,) in pack([(a,) for a in values], n):
                        m.call(f, [a], f"{op} {t} {a} splat {c}")
            for predicate in ["eq", "ne", "ugt", "uge", "ult", "ule", "sgt", "sge", "slt", "sle"]:
                f = m.probe(f"icmp_{predicate}_{suffix}", [t, t], vector(n, 8),
                            [f"%m = icmp {predicate} {t} %a, %b", f"%r = zext <{n} x i1> %m to {vector(n, 8)}"])
                for a, b in pack(pairs[::3], n):
                    m.call(f, [a, b], f"icmp {predicate} {t} {a} {b}")
            if n <= 64:
                # A mask as bits: bitcast to i<n>, widened to whole bytes.
                bits = 8 * ((n + 7) // 8)
                body = [f"%m = icmp slt {t} %a, %b", f"%i = bitcast <{n} x i1> %m to i{n}"]
                body += [f"%r = zext i{n} %i to i{bits}"] if bits != n else [f"%r = add i{n} %i, 0"]
                f = m.probe(f"maskbits_{suffix}", [t, t], f"i{bits}", body)
                for a, b in pack(pairs[::5], n):
                    m.call(f, [a, b], f"mask bits {t} {a} {b}")
            # Masks combined at other lane widths and selected.
            f = m.probe(f"select_{suffix}", [t, t, t], t,
                        [f"%m1 = icmp ult {t} %a, %b", f"%m2 = icmp sgt {t} %c, zeroinitializer", f"%m3 = xor <{n} x i1> %m1, %m2",
                         f"%m4 = or <{n} x i1> %m3, %m2", f"%s = select <{n} x i1> %m4, {t} %a, {t} %b", f"%x = sext <{n} x i1> %m3 to {t}",
                         f"%r = add {t} %s, %x"])
            for a, b, c in pack([(a, b, rng.choice(values)) for a, b in pairs[::4]], n):
                m.call(f, [a, b, c], f"select {t} {a} {b} {c}")
            for target in INTEGER_SHAPES:
                if target == w:
                    continue
                cast = "trunc" if target < w else rng.choice(["zext", "sext"])
                for kind in ([cast] if cast == "trunc" else ["zext", "sext"]):
                    f = m.probe(f"{kind}_{suffix}_i{target}", [t], vector(n, target), [f"%r = {kind} {t} %a to {vector(n, target)}"])
                    for (a,) in pack([(a,) for a in values], n):
                        m.call(f, [a], f"{kind} {t}->i{target} {a}")
            unary = [("ctpop", ""), ("ctlz", ", i1 false"), ("cttz", ", i1 false"), ("bitreverse", ""), ("abs", ", i1 false")]
            if w >= 16:
                unary.append(("bswap", ""))
            for name, extra in unary:
                f = m.probe(f"{name}_{suffix}", [t], t, [f"%r = call {t} @llvm.{name}.{suffix}({t} %a{extra})"])
                m.declare(f"declare {t} @llvm.{name}.{suffix}({t}{', i1' if extra else ''})")
                for (a,) in pack([(a,) for a in values], n):
                    m.call(f, [a], f"{name} {t} {a}")
            for name in ["smax", "smin", "umax", "umin", "sadd.sat", "uadd.sat", "ssub.sat", "usub.sat"]:
                f = m.probe(f"{name.replace('.', '_')}_{suffix}", [t, t], t, [f"%r = call {t} @llvm.{name}.{suffix}({t} %a, {t} %b)"])
                m.declare(f"declare {t} @llvm.{name}.{suffix}({t}, {t})")
                for a, b in pack(pairs[::2], n):
                    m.call(f, [a, b], f"{name} {t} {a} {b}")
            for name in ["fshl", "fshr"]:
                f = m.probe(f"{name}_{suffix}", [t, t, t], t, [f"%r = call {t} @llvm.{name}.{suffix}({t} %a, {t} %b, {t} %c)"])
                m.declare(f"declare {t} @llvm.{name}.{suffix}({t}, {t}, {t})")
                rows = [(a, values[-2], c & ((1 << w) - 1)) for a in values[::2] for c in [0, 1, w - 1, w, w + 3, (1 << w) - 1]]
                for a, b, c in pack(rows, n):
                    m.call(f, [a, b, c], f"{name} {t} {a} {b} {c}")
                f = m.probe(f"{name}_splat_{suffix}", [t, t], t, [f"%r = call {t} @llvm.{name}.{suffix}({t} %a, {t} %b, {t} splat (i{w} {w - 3}))"])
                for a, b in pack(pairs[::5], n):
                    m.call(f, [a, b], f"{name} {t} {a} {b} splat")
            for name in ["add", "mul", "and", "or", "xor", "smax", "smin", "umax", "umin"]:
                f = m.probe(f"reduce_{name}_{suffix}", [t], f"i{w}", [f"%r = call i{w} @llvm.vector.reduce.{name}.{suffix}({t} %a)"])
                m.declare(f"declare i{w} @llvm.vector.reduce.{name}.{suffix}({t})")
                for (a,) in pack([(rng.choice(values),) for _ in range(4 * n)], n):
                    m.call(f, [a], f"reduce.{name} {t} {a}")
            # Shuffles: random masks of other lengths, from either operand.
            for index in range(6):
                lanes = rng.choice([max(1, n // 2), n, 2 * n, 3]) if n > 1 else rng.choice([1, 2, 4])
                mask = [rng.randrange(2 * n) for _ in range(lanes)]
                f = m.probe(f"shuffle{index}_{suffix}", [t, t], vector(lanes, w),
                            [f"%r = shufflevector {t} %a, {t} %b, <{lanes} x i32> <" + ", ".join(f"i32 {lane}" for lane in mask) + ">"])
                for a, b in pack(pairs[::7], n):
                    m.call(f, [a, b], f"shuffle {mask} {t} {a} {b}")
            # A lane chosen at run time, read and written.
            f = m.probe(f"lane_{suffix}", [t, t, t], t,
                        [f"%i0 = extractelement {t} %c, i64 0", f"%i = zext i{w} %i0 to i64" if w < 64 else "%i = add i64 %i0, 0", f"%e = extractelement {t} %a, i64 %i",
                         f"%r = insertelement {t} %b, i{w} %e, i64 %i"])
            for a, b in list(pack(pairs[::9], n))[:8]:
                m.call(f, [a, b, [rng.randrange(n)] * n], f"lane {t} {a} {b}")
            # Reinterpreted as another lane width, then computed on.
            for other in INTEGER_SHAPES:
                if other == w or (n * w) % other or n * w // other > 64:
                    continue
                u = vector(n * w // other, other)
                f = m.probe(f"bitcast_{suffix}_i{other}", [t], u, [f"%x = bitcast {t} %a to {u}", f"%r = add {u} %x, %x"])
                for (a,) in pack([(a,) for a in values], n):
                    m.call(f, [a], f"bitcast {t}->{u} {a}")


def float_module(m, rng):
    for w, shapes in FLOAT_SHAPES.items():
        name, _, _, canonical = FLOATS[w]
        values = float_values(w)
        for n in shapes:
            t, f_type, suffix = vector(n, w), f"<{n} x {name}>", f"v{n}f{w}"
            load2 = [f"%x = bitcast {t} %a to {f_type}", f"%y = bitcast {t} %b to {f_type}"]
            canon = [f"%isnan = fcmp uno {f_type} %v, %v", f"%bits = bitcast {f_type} %v to {t}",
                     f"%r = select <{n} x i1> %isnan, {t} splat (i{w} {signed(canonical, w)}), {t} %bits"]
            pairs = [(a, b) for a in values for b in values[::2]]
            for op in ["fadd", "fsub", "fmul", "fdiv", "frem"]:
                f = m.probe(f"{op}_{suffix}", [t, t], t, load2 + [f"%v = {op} {f_type} %x, %y"] + canon)
                for a, b in pack(pairs, n):
                    m.call(f, [a, b], f"{op} {f_type} {a} {b}")
            f = m.probe(f"fneg_{suffix}", [t], t, [f"%x = bitcast {t} %a to {f_type}", f"%v = fneg {f_type} %x", f"%r = bitcast {f_type} %v to {t}"])
            for (a,) in pack([(a,) for a in values], n):
                m.call(f, [a], f"fneg {f_type} {a}")
            for intrinsic in ["fabs"]:
                f = m.probe(f"{intrinsic}_{suffix}", [t], t, [f"%x = bitcast {t} %a to {f_type}", f"%v = call {f_type} @llvm.{intrinsic}.{suffix}({f_type} %x)", f"%r = bitcast {f_type} %v to {t}"])
                m.declare(f"declare {f_type} @llvm.{intrinsic}.{suffix}({f_type})")
                for (a,) in pack([(a,) for a in values], n):
                    m.call(f, [a], f"{intrinsic} {f_type} {a}")
            f = m.probe(f"copysign_{suffix}", [t, t], t, load2 + [f"%v = call {f_type} @llvm.copysign.{suffix}({f_type} %x, {f_type} %y)", f"%r = bitcast {f_type} %v to {t}"])
            m.declare(f"declare {f_type} @llvm.copysign.{suffix}({f_type}, {f_type})")
            for a, b in pack(pairs[::3], n):
                m.call(f, [a, b], f"copysign {f_type} {a} {b}")
            for predicate in ["false", "oeq", "ogt", "oge", "olt", "ole", "one", "ord", "ueq", "ugt", "uge", "ult", "ule", "une", "uno", "true"]:
                f = m.probe(f"fcmp_{predicate}_{suffix}", [t, t], vector(n, 8), load2 + [f"%m = fcmp {predicate} {f_type} %x, %y", f"%r = zext <{n} x i1> %m to {vector(n, 8)}"])
                for a, b in pack(pairs[::2], n):
                    m.call(f, [a, b], f"fcmp {predicate} {f_type} {a} {b}")
            for intrinsic in ["floor", "ceil", "trunc", "rint", "nearbyint", "round", "roundeven", "sqrt"]:
                f = m.probe(f"{intrinsic}_{suffix}", [t], t, [f"%x = bitcast {t} %a to {f_type}", f"%v = call {f_type} @llvm.{intrinsic}.{suffix}({f_type} %x)"] + canon)
                m.declare(f"declare {f_type} @llvm.{intrinsic}.{suffix}({f_type})")
                for (a,) in pack([(a,) for a in values], n):
                    m.call(f, [a], f"{intrinsic} {f_type} {a}")
            for intrinsic in ["minnum", "maxnum", "minimum", "maximum"]:
                zero = [f"%v0 = call {f_type} @llvm.{intrinsic}.{suffix}({f_type} %x, {f_type} %y)", f"%iszero = fcmp oeq {f_type} %v0, zeroinitializer",
                        f"%v = select <{n} x i1> %iszero, {f_type} zeroinitializer, {f_type} %v0"] if intrinsic in ("minnum", "maxnum") else \
                       [f"%v = call {f_type} @llvm.{intrinsic}.{suffix}({f_type} %x, {f_type} %y)"]
                f = m.probe(f"{intrinsic}_{suffix}", [t, t], t, load2 + zero + canon)
                m.declare(f"declare {f_type} @llvm.{intrinsic}.{suffix}({f_type}, {f_type})")
                for a, b in pack(pairs[::2], n):
                    m.call(f, [a, b], f"{intrinsic} {f_type} {a} {b}")
            for intrinsic in ["fma", "fmuladd"]:
                f = m.probe(f"{intrinsic}_{suffix}", [t, t, t], t, load2 + [f"%z = bitcast {t} %c to {f_type}", f"%v = call {f_type} @llvm.{intrinsic}.{suffix}({f_type} %x, {f_type} %y, {f_type} %z)"] + canon)
                m.declare(f"declare {f_type} @llvm.{intrinsic}.{suffix}({f_type}, {f_type}, {f_type})")
                for a, b, c in pack([(a, b, rng.choice(values)) for a, b in pairs[::3]], n):
                    m.call(f, [a, b, c], f"{intrinsic} {f_type} {a} {b} {c}")
            # Conversions to and from every integer width; out-of-range lanes of fptosi/fptoui are masked.
            for target in [8, 16, 32, 64]:
                it = vector(n, target)
                for cast, low, high in [("fptosi", -(2 ** (target - 1)), 2 ** (target - 1)), ("fptoui", 0, 2 ** target)]:
                    f = m.probe(f"{cast}_{suffix}_i{target}", [t, it], it, [f"%x = bitcast {t} %a to {f_type}", f"%v = {cast} {f_type} %x to {it}",
                                                                           f"%f = freeze {it} %v", f"%r = and {it} %f, %b"])
                    rows = []
                    for a in values:
                        value = struct.unpack(FLOATS[w][1], struct.pack(FLOATS[w][2], a))[0]
                        valid = value == value and low - 1 < value < high
                        rows.append((a, (1 << target) - 1 if valid else 0))
                    for a, keep in pack(rows, n):
                        m.call(f, [a, keep], f"{cast} {f_type}->i{target} {a}")
                    saturating = f"{cast}.sat"
                    f = m.probe(f"{cast}_sat_{suffix}_i{target}", [t], it, [f"%x = bitcast {t} %a to {f_type}", f"%r = call {it} @llvm.{saturating}.v{n}i{target}.{suffix}({f_type} %x)"])
                    m.declare(f"declare {it} @llvm.{saturating}.v{n}i{target}.{suffix}({f_type})")
                    for (a,) in pack([(a,) for a in values], n):
                        m.call(f, [a], f"{saturating} {f_type}->i{target} {a}")
                for cast in ["sitofp", "uitofp"]:
                    f = m.probe(f"{cast}_i{target}_{suffix}", [it], t, [f"%v = {cast} {it} %a to {f_type}", f"%r = bitcast {f_type} %v to {t}"])
                    rows = [(a & ((1 << target) - 1),) for a in ints(target) + [(1 << 53) + 1, (1 << 24) + 1, (1 << 63) + 1025]]
                    for (a,) in pack(rows, n):
                        m.call(f, [a], f"{cast} i{target}->{f_type} {a}")
            other = 64 if w == 32 else 32
            if n in FLOAT_SHAPES[other] or n <= 4:
                o_name = FLOATS[other][0]
                ot, of_type = vector(n, other), f"<{n} x {o_name}>"
                cast = "fpext" if other > w else "fptrunc"
                f = m.probe(f"{cast}_{suffix}", [t], ot, [f"%x = bitcast {t} %a to {f_type}", f"%v = {cast} {f_type} %x to {of_type}",
                                                        f"%isnan = fcmp uno {of_type} %v, %v", f"%bits = bitcast {of_type} %v to {ot}",
                                                        f"%r = select <{n} x i1> %isnan, {ot} splat (i{other} {signed(FLOATS[other][3], other)}), {ot} %bits"])
                for (a,) in pack([(a,) for a in values], n):
                    m.call(f, [a], f"{cast} {f_type} {a}")
            scalar_canon = [f"%isnan = fcmp uno {name} %s, %s", f"%iszero = fcmp oeq {name} %s, 0.0", f"%s1 = select i1 %iszero, {name} 0.0, {name} %s",
                            f"%bits = bitcast {name} %s1 to i{w}", f"%r = select i1 %isnan, i{w} {signed(canonical, w)}, i{w} %bits"]
            for reduction, flags, start in [("fadd", "", "0.5"), ("fadd", "reassoc ", "-0.0"), ("fmul", "", "1.5"), ("fmax", "", None), ("fmin", "", None),
                                            ("fmaximum", "", None), ("fminimum", "", None)]:
                arguments = f"{name} {start}, {f_type} %x" if start else f"{f_type} %x"
                f = m.probe(f"reduce_{reduction}{'_reassoc' if flags else ''}_{suffix}", [t], f"i{w}",
                            [f"%x = bitcast {t} %a to {f_type}", f"%s = call {flags}{name} @llvm.vector.reduce.{reduction}.{suffix}({arguments})"] + scalar_canon)
                m.declare(f"declare {name} @llvm.vector.reduce.{reduction}.{suffix}({name + ', ' if start else ''}{f_type})")
                for (a,) in pack([(rng.choice(values),) for _ in range(4 * n)], n):
                    m.call(f, [a], f"reduce.{flags}{reduction} {f_type} {a}")


if __name__ == "__main__":
    directory = sys.argv[1]
    abi = sys.argv[2] if len(sys.argv) > 2 else ""
    seed = int(sys.argv[3]) if len(sys.argv) > 3 else 20261103
    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, "vector-cases.tsv"), "w") as cases:
        for name, build in [("vector-integer", integer_module), ("vector-float", float_module)]:
            module = Module(abi)
            build(module, random.Random(f"{seed}-{name}"))
            module.write(os.path.join(directory, f"{name}.ll"))
            for index, description in enumerate(module.cases):
                cases.write(f"{name}\t{index}\t{description}\n")
            print(f"{name}: {len(module.cases)} cases, {len(module.functions)} probes")
