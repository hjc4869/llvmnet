#!/usr/bin/env python3
"""Generate docs/user/support-matrix.md by compiling one small LLVM IR probe per construct.

A construct is "translated" when llvmnet compiles a module using it and every emitted method passes the JIT
(LlvmNet.Checks jit-all); tests/semantics.sh and the fixture tests check execution correctness. "rejected" rows
show the diagnostic llvmnet gives.

Usage: support-matrix.py <llvmnet> <work-directory> <output.md>
"""
import concurrent.futures
import os
import re
import subprocess
import sys

ABI = '!llvm.module.flags = !{!0}\n!0 = !{i32 1, !"llvmnet.abi", !"dotnet64-v2"}\n'


def unary(name, ty, extra_decl=None, args="", ret=None):
    ret = ret or ty
    decl = extra_decl or f"declare {ret} @llvm.{name}({ty}{', ' + args.split(' ', 1)[0] if args else ''})"
    call_args = f"{ty} %a" + (f", {args}" if args else "")
    return (f"define {ret} @probe({ty} %a) {{\n  %r = call {ret} @llvm.{name}({call_args})\n  ret {ret} %r\n}}\n{decl}\n")


def binary(name, ty, ret=None):
    ret = ret or ty
    return (f"define {ret} @probe({ty} %a, {ty} %b) {{\n  %r = call {ret} @llvm.{name}({ty} %a, {ty} %b)\n  ret {ret} %r\n}}\n"
            f"declare {ret} @llvm.{name}({ty}, {ty})\n")


def body(text, signature="void @probe(ptr %p)"):
    return f"define {signature} {{\n{text}\n}}\n"


PROBES = []


def probe(category, construct, ir):
    PROBES.append((category, construct, ir))


# Types
for ty in ["i1", "i8", "i16", "i32", "i64", "i128", "i65", "i256", "half", "bfloat", "float", "double", "x86_fp80", "fp128", "ppc_fp128",
           "<4 x i32>", "<8 x float>", "<16 x i8>", "<vscale x 4 x i32>", "{ i32, double }", "[4 x i16]", "ptr addrspace(1)"]:
    probe("Types", ty, body(f"  %v = load {ty}, ptr %p\n  store {ty} %v, ptr %p\n  ret void"))
# Instructions
instructions = {
    "ret / br / unreachable": "  br label %next\nnext:\n  ret void",
    "switch": "  %v = load i32, ptr %p\n  switch i32 %v, label %d [ i32 1, label %a  i32 7, label %a ]\na:\n  ret void\nd:\n  ret void",
    "indirectbr (computed goto)": "  %t = select i1 true, ptr blockaddress(@probe, %a), ptr blockaddress(@probe, %b)\n  indirectbr ptr %t, [label %a, label %b]\na:\n  ret void\nb:\n  ret void",
    "integer arithmetic and bitwise": "  %v = load i32, ptr %p\n  %w = add nsw i32 %v, 3\n  %x = sdiv exact i32 %w, 2\n  %y = xor i32 %x, %v\n  store i32 %y, ptr %p\n  ret void",
    "floating arithmetic and fneg": "  %v = load double, ptr %p\n  %w = fadd fast double %v, 1.0\n  %x = frem double %w, 3.0\n  %y = fneg double %x\n  store double %y, ptr %p\n  ret void",
    "alloca (static and dynamic)": "  %n = load i32, ptr %p\n  %a = alloca i64, i32 %n, align 32\n  store i64 1, ptr %a\n  ret void",
    "fence": "  fence seq_cst\n  fence syncscope(\"singlethread\") acquire\n  ret void",
    "cmpxchg / atomicrmw": "  %r = cmpxchg weak ptr %p, i32 0, i32 1 acq_rel monotonic\n  %s = atomicrmw umax ptr %p, i32 9 seq_cst\n  ret void",
    "atomicrmw uinc_wrap": "  %s = atomicrmw uinc_wrap ptr %p, i32 9 seq_cst\n  ret void",
    "getelementptr / casts": "  %q = getelementptr inbounds [4 x i32], ptr %p, i64 0, i64 2\n  %i = ptrtoint ptr %q to i64\n  %b = bitcast i64 %i to double\n  %t = fptrunc double %b to float\n  store float %t, ptr %p\n  ret void",
    "addrspacecast": "  %q = addrspacecast ptr %p to ptr addrspace(1)\n  store i32 0, ptr addrspace(1) %q\n  ret void",
    "icmp / fcmp / select / phi": "  %v = load i32, ptr %p\n  %c = icmp ult i32 %v, 4\n  br i1 %c, label %a, label %b\na:\n  br label %b\nb:\n  %m = phi i32 [ 1, %a ], [ 2, %0 ]\n  %s = select i1 %c, i32 %m, i32 %v\n  store i32 %s, ptr %p\n  ret void",
    "extractelement / insertelement / shufflevector": "  %v = load <4 x i32>, ptr %p\n  %e = extractelement <4 x i32> %v, i32 1\n  %w = insertelement <4 x i32> %v, i32 %e, i32 3\n  %s = shufflevector <4 x i32> %w, <4 x i32> %v, <4 x i32> <i32 7, i32 0, i32 poison, i32 2>\n  store <4 x i32> %s, ptr %p\n  ret void",
    "extractvalue / insertvalue": "  %v = load { i32, i64 }, ptr %p\n  %e = extractvalue { i32, i64 } %v, 1\n  %w = insertvalue { i32, i64 } %v, i64 %e, 1\n  store { i32, i64 } %w, ptr %p\n  ret void",
    "freeze": "  %v = load i32, ptr %p\n  %f = freeze i32 %v\n  store i32 %f, ptr %p\n  ret void",
    "va_arg instruction": None,
    "inline asm (empty barrier)": "  call void asm sideeffect \"\", \"~{memory}\"()\n  ret void",
    "inline asm (instructions)": "  call void asm sideeffect \"nop\", \"\"()\n  ret void",
    "callbr (asm goto)": "  callbr void asm \"\", \"!i\"() to label %a [label %b]\na:\n  ret void\nb:\n  ret void",
}
for name, text in instructions.items():
    if text is not None:
        probe("Instructions", name, body(text))
probe("Instructions", "va_arg instruction", "define i32 @probe(ptr %list) {\n  %v = va_arg ptr %list, i32\n  ret i32 %v\n}\n")
probe("Instructions", "invoke / landingpad / resume (C++)",
      "@_ZTIi = external constant ptr\ndeclare void @may_throw()\ndeclare i32 @__gxx_personality_v0(...)\n"
      "define void @probe(ptr %p) personality ptr @__gxx_personality_v0 {\n  invoke void @may_throw() to label %ok unwind label %bad\nok:\n  ret void\nbad:\n"
      "  %l = landingpad { ptr, i32 } catch ptr @_ZTIi\n  resume { ptr, i32 } %l\n}\ndefine void @may_throw() {\n  ret void\n}\n".replace("declare void @may_throw()\n", ""))
probe("Instructions", "catchswitch / catchpad (Windows EH)",
      "declare i32 @__CxxFrameHandler3(...)\ndeclare void @may_throw()\ndefine void @probe(ptr %p) personality ptr @__CxxFrameHandler3 {\n"
      "  invoke void @may_throw() to label %ok unwind label %dispatch\ndispatch:\n  %cs = catchswitch within none [label %handler] unwind to caller\n"
      "handler:\n  %cp = catchpad within %cs [ptr null, i32 64, ptr null]\n  catchret from %cp to label %ok\nok:\n  ret void\n}\n")
# Intrinsics
F, D = "float", "double"
intrinsics = [
    ("Memory", "llvm.memcpy / memmove / memset", body("  call void @llvm.memcpy.p0.p0.i64(ptr %p, ptr %p, i64 8, i1 false)\n  call void @llvm.memmove.p0.p0.i64(ptr %p, ptr %p, i64 8, i1 true)\n  call void @llvm.memset.p0.i64(ptr %p, i8 0, i64 8, i1 false)\n  ret void") +
     "declare void @llvm.memcpy.p0.p0.i64(ptr, ptr, i64, i1)\ndeclare void @llvm.memmove.p0.p0.i64(ptr, ptr, i64, i1)\ndeclare void @llvm.memset.p0.i64(ptr, i8, i64, i1)\n"),
    ("Memory", "llvm.memcpy.inline", body("  call void @llvm.memcpy.inline.p0.p0.i64(ptr %p, ptr %p, i64 8, i1 false)\n  ret void") + "declare void @llvm.memcpy.inline.p0.p0.i64(ptr, ptr, i64, i1)\n"),
    ("Memory", "llvm.memset.inline", body("  call void @llvm.memset.inline.p0.i64(ptr %p, i8 0, i64 8, i1 false)\n  ret void") + "declare void @llvm.memset.inline.p0.i64(ptr, i8, i64, i1)\n"),
    ("Memory", "llvm.stacksave / stackrestore", body("  %s = call ptr @llvm.stacksave.p0()\n  call void @llvm.stackrestore.p0(ptr %s)\n  ret void") + "declare ptr @llvm.stacksave.p0()\ndeclare void @llvm.stackrestore.p0(ptr)\n"),
    ("Memory", "llvm.lifetime / assume / expect / prefetch", body("  call void @llvm.lifetime.start.p0(i64 4, ptr %p)\n  call void @llvm.assume(i1 true)\n  call void @llvm.prefetch.p0(ptr %p, i32 0, i32 3, i32 1)\n  ret void") +
     "declare void @llvm.lifetime.start.p0(i64, ptr)\ndeclare void @llvm.assume(i1)\ndeclare void @llvm.prefetch.p0(ptr, i32, i32, i32)\n"),
    ("Memory", "llvm.objectsize", "define i64 @probe(ptr %p) {\n  %r = call i64 @llvm.objectsize.i64.p0(ptr %p, i1 false, i1 true, i1 false)\n  ret i64 %r\n}\ndeclare i64 @llvm.objectsize.i64.p0(ptr, i1, i1, i1)\n"),
    ("Memory", "llvm.ptrmask", "define ptr @probe(ptr %p) {\n  %r = call ptr @llvm.ptrmask.p0.i64(ptr %p, i64 -16)\n  ret ptr %r\n}\ndeclare ptr @llvm.ptrmask.p0.i64(ptr, i64)\n"),
    ("Memory", "llvm.threadlocal.address", "@t = thread_local global i32 0\ndefine ptr @probe() {\n  %r = call ptr @llvm.threadlocal.address.p0(ptr @t)\n  ret ptr %r\n}\ndeclare ptr @llvm.threadlocal.address.p0(ptr)\n"),
    ("Memory", "llvm.masked.load / store", "define <4 x i32> @probe(ptr %p, <4 x i1> %m) {\n  %v = call <4 x i32> @llvm.masked.load.v4i32.p0(ptr %p, i32 4, <4 x i1> %m, <4 x i32> zeroinitializer)\n  call void @llvm.masked.store.v4i32.p0(<4 x i32> %v, ptr %p, i32 4, <4 x i1> %m)\n  ret <4 x i32> %v\n}\n"
     "declare <4 x i32> @llvm.masked.load.v4i32.p0(ptr, i32, <4 x i1>, <4 x i32>)\ndeclare void @llvm.masked.store.v4i32.p0(<4 x i32>, ptr, i32, <4 x i1>)\n"),
    ("Memory", "llvm.masked.gather / scatter", "define <4 x i32> @probe(<4 x ptr> %p, <4 x i1> %m) {\n  %v = call <4 x i32> @llvm.masked.gather.v4i32.v4p0(<4 x ptr> %p, i32 4, <4 x i1> %m, <4 x i32> zeroinitializer)\n  call void @llvm.masked.scatter.v4i32.v4p0(<4 x i32> %v, <4 x ptr> %p, i32 4, <4 x i1> %m)\n  ret <4 x i32> %v\n}\n"
     "declare <4 x i32> @llvm.masked.gather.v4i32.v4p0(<4 x ptr>, i32, <4 x i1>, <4 x i32>)\ndeclare void @llvm.masked.scatter.v4i32.v4p0(<4 x i32>, <4 x ptr>, i32, <4 x i1>)\n"),
    ("Control", "llvm.trap / debugtrap / ubsantrap", body("  call void @llvm.ubsantrap(i8 1)\n  ret void") + "declare void @llvm.ubsantrap(i8)\n"),
    ("Control", "llvm.va_start / va_copy / va_end", "define i32 @probe(i32 %n, ...) {\n  %l = alloca [24 x i8]\n  %c = alloca [24 x i8]\n  call void @llvm.va_start.p0(ptr %l)\n  call void @llvm.va_copy.p0(ptr %c, ptr %l)\n  call void @llvm.va_end.p0(ptr %c)\n  call void @llvm.va_end.p0(ptr %l)\n  ret i32 %n\n}\n"
     "declare void @llvm.va_start.p0(ptr)\ndeclare void @llvm.va_copy.p0(ptr, ptr)\ndeclare void @llvm.va_end.p0(ptr)\n"),
    ("Control", "llvm.returnaddress / frameaddress", "define ptr @probe() {\n  %r = call ptr @llvm.returnaddress(i32 0)\n  ret ptr %r\n}\ndeclare ptr @llvm.returnaddress(i32)\n"),
    ("Control", "llvm.readcyclecounter", "define i64 @probe() {\n  %r = call i64 @llvm.readcyclecounter()\n  ret i64 %r\n}\ndeclare i64 @llvm.readcyclecounter()\n"),
    ("Control", "llvm.get.rounding", "define i32 @probe() {\n  %r = call i32 @llvm.get.rounding()\n  ret i32 %r\n}\ndeclare i32 @llvm.get.rounding()\n"),
    ("Control", "llvm.set.rounding", body("  call void @llvm.set.rounding(i32 0)\n  ret void") + "declare void @llvm.set.rounding(i32)\n"),
    ("Control", "llvm.is.constant", "define i1 @probe(i32 %a) {\n  %r = call i1 @llvm.is.constant.i32(i32 %a)\n  ret i1 %r\n}\ndeclare i1 @llvm.is.constant.i32(i32)\n"),
    ("Control", "llvm.experimental.constrained.fadd (strictfp)", "define double @probe(double %a) strictfp {\n  %r = call double @llvm.experimental.constrained.fadd.f64(double %a, double %a, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp\n  ret double %r\n}\ndeclare double @llvm.experimental.constrained.fadd.f64(double, double, metadata, metadata)\n"),
]
for category, name, ir in intrinsics:
    probe(f"Intrinsics: {category.lower()}", name, ir)
for name in ["ctpop", "ctlz", "cttz", "bitreverse", "bswap", "abs"]:
    for ty in ["i32", "i128"]:
        extra = {"ctlz": "i1 false", "cttz": "i1 false", "abs": "i1 false"}.get(name, "")
        decl = f"declare {ty} @llvm.{name}.{ty}({ty}{', i1' if extra else ''})"
        probe("Intrinsics: integer", f"llvm.{name}.{ty}", unary(f"{name}.{ty}", ty, decl, extra))
for name in ["smax", "umin", "sadd.sat", "usub.sat", "sshl.sat", "ushl.sat"]:
    for ty in ["i32", "i128"]:
        probe("Intrinsics: integer", f"llvm.{name}.{ty}", binary(f"{name}.{ty}", ty))
for name in ["fshl", "fshr"]:
    probe("Intrinsics: integer", f"llvm.{name}.i64", f"define i64 @probe(i64 %a) {{\n  %r = call i64 @llvm.{name}.i64(i64 %a, i64 %a, i64 3)\n  ret i64 %r\n}}\ndeclare i64 @llvm.{name}.i64(i64, i64, i64)\n")
probe("Intrinsics: integer", "llvm.smul.fix.i32", "define i32 @probe(i32 %a) {\n  %r = call i32 @llvm.smul.fix.i32(i32 %a, i32 %a, i32 4)\n  ret i32 %r\n}\ndeclare i32 @llvm.smul.fix.i32(i32, i32, i32)\n")
for name in ["sadd", "umul"]:
    for ty in ["i64", "i128"]:
        probe("Intrinsics: integer", f"llvm.{name}.with.overflow.{ty}", f"define {{ {ty}, i1 }} @probe({ty} %a) {{\n  %r = call {{ {ty}, i1 }} @llvm.{name}.with.overflow.{ty}({ty} %a, {ty} %a)\n  ret {{ {ty}, i1 }} %r\n}}\ndeclare {{ {ty}, i1 }} @llvm.{name}.with.overflow.{ty}({ty}, {ty})\n")
probe("Intrinsics: integer", "llvm.scmp / llvm.ucmp", "define i8 @probe(i64 %a) {\n  %r = call i8 @llvm.scmp.i8.i64(i64 %a, i64 3)\n  ret i8 %r\n}\ndeclare i8 @llvm.scmp.i8.i64(i64, i64)\n")
for ty, suffix in [(F, "f32"), (D, "f64"), ("x86_fp80", "f80")]:
    for name in ["sqrt", "fabs", "floor", "ceil", "trunc", "rint", "nearbyint", "round", "roundeven", "sin", "cos", "tan", "exp", "exp2", "exp10", "log", "log2", "log10", "canonicalize"]:
        probe(f"Intrinsics: floating ({ty})", f"llvm.{name}.{suffix}", unary(f"{name}.{suffix}", ty))
    for name in ["pow", "copysign", "minnum", "maxnum", "minimum", "maximum", "minimumnum", "maximumnum"]:
        probe(f"Intrinsics: floating ({ty})", f"llvm.{name}.{suffix}", binary(f"{name}.{suffix}", ty))
    probe(f"Intrinsics: floating ({ty})", f"llvm.fma / fmuladd.{suffix}", f"define {ty} @probe({ty} %a) {{\n  %r = call {ty} @llvm.fma.{suffix}({ty} %a, {ty} %a, {ty} %a)\n  %s = call {ty} @llvm.fmuladd.{suffix}({ty} %r, {ty} %a, {ty} %a)\n  ret {ty} %s\n}}\ndeclare {ty} @llvm.fma.{suffix}({ty}, {ty}, {ty})\ndeclare {ty} @llvm.fmuladd.{suffix}({ty}, {ty}, {ty})\n")
    probe(f"Intrinsics: floating ({ty})", f"llvm.powi.{suffix}.i32", f"define {ty} @probe({ty} %a) {{\n  %r = call {ty} @llvm.powi.{suffix}.i32({ty} %a, i32 3)\n  ret {ty} %r\n}}\ndeclare {ty} @llvm.powi.{suffix}.i32({ty}, i32)\n")
    probe(f"Intrinsics: floating ({ty})", f"llvm.ldexp / frexp.{suffix}", f"define {ty} @probe({ty} %a) {{\n  %r = call {ty} @llvm.ldexp.{suffix}.i32({ty} %a, i32 3)\n  %f = call {{ {ty}, i32 }} @llvm.frexp.{suffix}.i32({ty} %r)\n  %v = extractvalue {{ {ty}, i32 }} %f, 0\n  ret {ty} %v\n}}\ndeclare {ty} @llvm.ldexp.{suffix}.i32({ty}, i32)\ndeclare {{ {ty}, i32 }} @llvm.frexp.{suffix}.i32({ty})\n")
    probe(f"Intrinsics: floating ({ty})", f"llvm.lround / llrint.{suffix}", f"define i64 @probe({ty} %a) {{\n  %r = call i64 @llvm.lround.i64.{suffix}({ty} %a)\n  %s = call i64 @llvm.llrint.i64.{suffix}({ty} %a)\n  %t = add i64 %r, %s\n  ret i64 %t\n}}\ndeclare i64 @llvm.lround.i64.{suffix}({ty})\ndeclare i64 @llvm.llrint.i64.{suffix}({ty})\n")
    probe(f"Intrinsics: floating ({ty})", f"llvm.is.fpclass.{suffix}", f"define i1 @probe({ty} %a) {{\n  %r = call i1 @llvm.is.fpclass.{suffix}({ty} %a, i32 3)\n  ret i1 %r\n}}\ndeclare i1 @llvm.is.fpclass.{suffix}({ty}, i32)\n")
    probe(f"Intrinsics: floating ({ty})", f"llvm.fptosi.sat.i32.{suffix}", f"define i32 @probe({ty} %a) {{\n  %r = call i32 @llvm.fptosi.sat.i32.{suffix}({ty} %a)\n  ret i32 %r\n}}\ndeclare i32 @llvm.fptosi.sat.i32.{suffix}({ty})\n")
for name in ["add", "mul", "and", "or", "xor", "smax", "umin"]:
    probe("Intrinsics: vector reductions", f"llvm.vector.reduce.{name}.v8i32", f"define i32 @probe(<8 x i32> %a) {{\n  %r = call i32 @llvm.vector.reduce.{name}.v8i32(<8 x i32> %a)\n  ret i32 %r\n}}\ndeclare i32 @llvm.vector.reduce.{name}.v8i32(<8 x i32>)\n")
probe("Intrinsics: vector reductions", "llvm.vector.reduce.fadd.v4f32 (ordered)", "define float @probe(<4 x float> %a) {\n  %r = call float @llvm.vector.reduce.fadd.v4f32(float 0.0, <4 x float> %a)\n  ret float %r\n}\ndeclare float @llvm.vector.reduce.fadd.v4f32(float, <4 x float>)\n")
probe("Intrinsics: vector reductions", "llvm.vector.reduce.fmax.v4f64", "define double @probe(<4 x double> %a) {\n  %r = call double @llvm.vector.reduce.fmax.v4f64(<4 x double> %a)\n  ret double %r\n}\ndeclare double @llvm.vector.reduce.fmax.v4f64(<4 x double>)\n")
probe("Intrinsics: vector", "llvm.vector.reverse", "define <4 x i32> @probe(<4 x i32> %a) {\n  %r = call <4 x i32> @llvm.vector.reverse.v4i32(<4 x i32> %a)\n  ret <4 x i32> %r\n}\ndeclare <4 x i32> @llvm.vector.reverse.v4i32(<4 x i32>)\n")
probe("Intrinsics: vector", "llvm.get.active.lane.mask", "define <4 x i1> @probe(i64 %a) {\n  %r = call <4 x i1> @llvm.get.active.lane.mask.v4i1.i64(i64 %a, i64 10)\n  ret <4 x i1> %r\n}\ndeclare <4 x i1> @llvm.get.active.lane.mask.v4i1.i64(i64, i64)\n")
probe("Intrinsics: vector", "vector integer arithmetic (scalarized)", "define <8 x i16> @probe(<8 x i16> %a) {\n  %r = mul <8 x i16> %a, %a\n  %s = call <8 x i16> @llvm.umax.v8i16(<8 x i16> %r, <8 x i16> %a)\n  ret <8 x i16> %s\n}\ndeclare <8 x i16> @llvm.umax.v8i16(<8 x i16>, <8 x i16>)\n")
# Linkage
linkage = [
    ("global alias", "define i32 @f() {\n  ret i32 1\n}\n@g = alias i32 (), ptr @f\ndefine i32 @probe() {\n  %r = call i32 @g()\n  ret i32 %r\n}\n"),
    ("weak / linkonce_odr definitions", "define weak i32 @w() {\n  ret i32 1\n}\ndefine linkonce_odr i32 @l() {\n  ret i32 2\n}\ndefine i32 @probe() {\n  %a = call i32 @w()\n  %b = call i32 @l()\n  %r = add i32 %a, %b\n  ret i32 %r\n}\n"),
    ("undefined extern_weak", "declare extern_weak i32 @missing()\ndefine i1 @probe() {\n  %r = icmp eq ptr @missing, null\n  ret i1 %r\n}\n"),
    ("common / appending globals", "@c = common global i32 0\n@llvm.used = appending global [1 x ptr] [ptr @c], section \"llvm.metadata\"\ndefine i32 @probe() {\n  %r = load i32, ptr @c\n  ret i32 %r\n}\n"),
    ("ifunc", "define ptr @resolve() {\n  ret ptr @impl\n}\ndefine i32 @impl() {\n  ret i32 1\n}\n@i = ifunc i32 (), ptr @resolve\ndefine i32 @probe() {\n  %r = call i32 @i()\n  ret i32 %r\n}\n"),
    ("thread_local global", "@t = thread_local global i32 5\ndefine i32 @probe() {\n  %r = load i32, ptr @t\n  ret i32 %r\n}\n"),
    ("external thread_local global", "@t = external thread_local global i32\ndefine i32 @probe() {\n  %r = load i32, ptr @t\n  ret i32 %r\n}\n"),
    ("global_ctors / global_dtors", "define void @c() {\n  ret void\n}\n@llvm.global_ctors = appending global [1 x { i32, ptr, ptr }] [{ i32, ptr, ptr } { i32 101, ptr @c, ptr null }]\ndefine void @probe() {\n  ret void\n}\n"),
]
for name, ir in linkage:
    probe("Linkage", name, ir)


def compile_probe(item, llvmnet, work):
    index, (category, construct, ir) = item
    directory = os.path.join(work, f"probe{index:03d}")
    os.makedirs(directory, exist_ok=True)
    source = os.path.join(directory, "probe.ll")
    with open(source, "w") as out:
        out.write('target datalayout = "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"\n')
        out.write('target triple = "x86_64-unknown-none"\n' + ir)
        if "@main(" not in ir:
            out.write("define i32 @main() {\n  ret i32 0\n}\n")
        out.write(ABI)
    result = subprocess.run([llvmnet, "--runtime=portable", "--emit-library", source, "-o", os.path.join(directory, "Probe.dll")],
                            capture_output=True, text=True, timeout=300)
    diagnostic = ""
    code = result.returncode
    if code == 0:
        # A translated probe must also contain valid IL; force the JIT to compile every method.
        check = subprocess.run(["dotnet", CHECKS, "jit-all", os.path.join(directory, "Probe.dll")], capture_output=True, text=True, timeout=300)
        if check.returncode != 0:
            code, diagnostic = 4, (check.stderr.strip().splitlines() or ["invalid IL"])[0].split(": ", 2)[-1]
            return category, construct, code, diagnostic
    if result.returncode != 0:
        lines = [line for line in (result.stderr + result.stdout).splitlines() if "error" in line]
        diagnostic = (lines[0] if lines else result.stderr.strip().splitlines()[-1]).split("error:", 1)[-1].strip()
        # The long double math intrinsics become calls to the C library (logl, __powixf2), which a probe built as a
        # library does not link; a program links them from the sysroot.
        libcall = re.fullmatch(r"Unresolved function: (\w+l|__powixf2)", diagnostic)
        if libcall and construct.endswith("f80") or libcall and construct.endswith("f80.i32"):
            code, diagnostic = 0, f"as a call to `{libcall.group(1)}` from the C library"
    return category, construct, code, diagnostic


CHECKS = ""


def main():
    global CHECKS
    llvmnet, work, output = sys.argv[1:4]
    CHECKS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(llvmnet))), "tests", "LlvmNet.Checks", "bin", "Release", "net10.0", "LlvmNet.Checks.dll")
    with concurrent.futures.ThreadPoolExecutor(max_workers=os.cpu_count() or 4) as pool:
        rows = list(pool.map(lambda item: compile_probe(item, llvmnet, work), enumerate(PROBES)))
    translated = sum(1 for row in rows if row[2] == 0)
    with open(output, "w") as out:
        out.write("# LLVM IR support matrix\n\n")
        out.write("Generated by `scripts/support-matrix.py`; do not edit by hand. After changing the backend, run `tests/run.sh support-matrix`, which fails and regenerates this page when it is out of date.\n\n")
        out.write("Each row is a small LLVM IR module compiled with `llvmnet --runtime=portable`. **Translated** means llvmnet produced CIL for it and the JIT accepts every method. Execution correctness is checked separately by `tests/semantics.sh`, which compares generated operations bit for bit with native code, and by the fixture tests. **Rejected** rows show the diagnostic a user sees.\n\n")
        out.write(f"{translated} of {len(rows)} constructs are translated.\n")
        category = None
        for row_category, construct, code, diagnostic in rows:
            if row_category != category:
                category = row_category
                out.write(f"\n## {category}\n\n| Construct | Status | Diagnostic |\n| --- | --- | --- |\n")
            status = {0: "translated", 1: "rejected", 4: "**invalid IL (bug)**"}.get(code, f"**internal error ({code})**")
            cell = diagnostic.replace("|", "\\|")
            out.write(f"| `{construct}` | {status} | {cell} |\n")
    print(f"{translated} of {len(rows)} constructs translated; wrote {output}")
    return 0 if all(row[2] in (0, 1) for row in rows) else 1  # internal errors and invalid IL are bugs


if __name__ == "__main__":
    sys.exit(main())
