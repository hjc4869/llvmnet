; Shifts of vector lanes by counts per lane (SV-24): variable counts of every lane width, which run as AVX2's or
; AdvSimd's shifts where the host has them and lane by lane elsewhere; the SLP vectorizer's partial splats, a constant
; count in some lanes and poison in the others, which are one count for the vector; and constant counts per lane in
; 16- and 32-bit lanes, which are multiplications by powers of two. Each check adds a bit to the failure mask main
; returns; lanes a poison count leaves undefined are frozen and masked.
target triple = "x86_64-pc-linux-gnu"

define i32 @variable(<16 x i8> %a8, <16 x i8> %c8, <8 x i16> %a16, <8 x i16> %c16, <4 x i32> %a32, <4 x i32> %c32, <2 x i64> %a64, <2 x i64> %c64) {
entry:
  %shl8 = shl <16 x i8> %a8, %c8
  %shl8.x = xor <16 x i8> %shl8, <i8 -128, i8 0, i8 -2, i8 3, i8 1, i8 0, i8 -64, i8 6, i8 8, i8 -128, i8 127, i8 12, i8 2, i8 0, i8 -8, i8 48>
  %shl8.r = call i8 @llvm.vector.reduce.or.v16i8(<16 x i8> %shl8.x)
  %shl8.c = icmp ne i8 %shl8.r, 0
  %lshr8 = lshr <16 x i8> %a8, %c8
  %lshr8.x = xor <16 x i8> %lshr8, <i8 0, i8 1, i8 63, i8 3, i8 1, i8 64, i8 1, i8 1, i8 0, i8 -128, i8 127, i8 0, i8 0, i8 32, i8 15, i8 0>
  %lshr8.r = call i8 @llvm.vector.reduce.or.v16i8(<16 x i8> %lshr8.x)
  %lshr8.c = icmp ne i8 %lshr8.r, 0
  %ashr8 = ashr <16 x i8> %a8, %c8
  %ashr8.x = xor <16 x i8> %ashr8, <i8 0, i8 -1, i8 63, i8 3, i8 1, i8 -64, i8 1, i8 1, i8 0, i8 -128, i8 127, i8 0, i8 0, i8 -32, i8 15, i8 0>
  %ashr8.r = call i8 @llvm.vector.reduce.or.v16i8(<16 x i8> %ashr8.x)
  %ashr8.c = icmp ne i8 %ashr8.r, 0
  %shl16 = shl <8 x i16> %a16, %c16
  %shl16.x = xor <8 x i16> %shl16, <i16 1, i16 -4, i16 12, i16 -32, i16 -32768, i16 0, i16 -256, i16 -32768>
  %shl16.r = call i16 @llvm.vector.reduce.or.v8i16(<8 x i16> %shl16.x)
  %shl16.c = icmp ne i16 %shl16.r, 0
  %lshr16 = lshr <8 x i16> %a16, %c16
  %lshr16.x = xor <8 x i16> %lshr16, <i16 1, i16 32767, i16 0, i16 8191, i16 8192, i16 1, i16 0, i16 0>
  %lshr16.r = call i16 @llvm.vector.reduce.or.v8i16(<8 x i16> %lshr16.x)
  %lshr16.c = icmp ne i16 %lshr16.r, 0
  %ashr16 = ashr <8 x i16> %a16, %c16
  %ashr16.x = xor <8 x i16> %ashr16, <i16 1, i16 -1, i16 0, i16 -1, i16 8192, i16 -1, i16 0, i16 0>
  %ashr16.r = call i16 @llvm.vector.reduce.or.v8i16(<8 x i16> %ashr16.x)
  %ashr16.c = icmp ne i16 %ashr16.r, 0
  %shl32 = shl <4 x i32> %a32, %c32
  %shl32.x = xor <4 x i32> %shl32, <i32 -2147483648, i32 -32, i32 591751040, i32 5>
  %shl32.r = call i32 @llvm.vector.reduce.or.v4i32(<4 x i32> %shl32.x)
  %shl32.c = icmp ne i32 %shl32.r, 0
  %lshr32 = lshr <4 x i32> %a32, %c32
  %lshr32.x = xor <4 x i32> %lshr32, <i32 0, i32 1073741822, i32 19088743, i32 5>
  %lshr32.r = call i32 @llvm.vector.reduce.or.v4i32(<4 x i32> %lshr32.x)
  %lshr32.c = icmp ne i32 %lshr32.r, 0
  %ashr32 = ashr <4 x i32> %a32, %c32
  %ashr32.x = xor <4 x i32> %ashr32, <i32 0, i32 -2, i32 19088743, i32 5>
  %ashr32.r = call i32 @llvm.vector.reduce.or.v4i32(<4 x i32> %ashr32.x)
  %ashr32.c = icmp ne i32 %ashr32.r, 0
  %shl64 = shl <2 x i64> %a64, %c64
  %shl64.x = xor <2 x i64> %shl64, <i64 -9223372036854775808, i64 6>
  %shl64.r = call i64 @llvm.vector.reduce.or.v2i64(<2 x i64> %shl64.x)
  %shl64.c = icmp ne i64 %shl64.r, 0
  %lshr64 = lshr <2 x i64> %a64, %c64
  %lshr64.x = xor <2 x i64> %lshr64, <i64 1, i64 1>
  %lshr64.r = call i64 @llvm.vector.reduce.or.v2i64(<2 x i64> %lshr64.x)
  %lshr64.c = icmp ne i64 %lshr64.r, 0
  %ashr64 = ashr <2 x i64> %a64, %c64
  %ashr64.x = xor <2 x i64> %ashr64, <i64 -1, i64 1>
  %ashr64.r = call i64 @llvm.vector.reduce.or.v2i64(<2 x i64> %ashr64.x)
  %ashr64.c = icmp ne i64 %ashr64.r, 0
  %v.z0 = zext i1 %shl8.c to i32
  %v.s0 = shl i32 %v.z0, 0
  %v.o0 = or i32 0, %v.s0
  %v.z1 = zext i1 %lshr8.c to i32
  %v.s1 = shl i32 %v.z1, 1
  %v.o1 = or i32 %v.o0, %v.s1
  %v.z2 = zext i1 %ashr8.c to i32
  %v.s2 = shl i32 %v.z2, 2
  %v.o2 = or i32 %v.o1, %v.s2
  %v.z3 = zext i1 %shl16.c to i32
  %v.s3 = shl i32 %v.z3, 3
  %v.o3 = or i32 %v.o2, %v.s3
  %v.z4 = zext i1 %lshr16.c to i32
  %v.s4 = shl i32 %v.z4, 4
  %v.o4 = or i32 %v.o3, %v.s4
  %v.z5 = zext i1 %ashr16.c to i32
  %v.s5 = shl i32 %v.z5, 5
  %v.o5 = or i32 %v.o4, %v.s5
  %v.z6 = zext i1 %shl32.c to i32
  %v.s6 = shl i32 %v.z6, 6
  %v.o6 = or i32 %v.o5, %v.s6
  %v.z7 = zext i1 %lshr32.c to i32
  %v.s7 = shl i32 %v.z7, 7
  %v.o7 = or i32 %v.o6, %v.s7
  %v.z8 = zext i1 %ashr32.c to i32
  %v.s8 = shl i32 %v.z8, 8
  %v.o8 = or i32 %v.o7, %v.s8
  %v.z9 = zext i1 %shl64.c to i32
  %v.s9 = shl i32 %v.z9, 9
  %v.o9 = or i32 %v.o8, %v.s9
  %v.z10 = zext i1 %lshr64.c to i32
  %v.s10 = shl i32 %v.z10, 10
  %v.o10 = or i32 %v.o9, %v.s10
  %v.z11 = zext i1 %ashr64.c to i32
  %v.s11 = shl i32 %v.z11, 11
  %v.o11 = or i32 %v.o10, %v.s11
  ret i32 %v.o11
}

define i32 @constant(<8 x i16> %a16, <4 x i32> %a32, <16 x i8> %a8) {
entry:
  %k0 = shl <8 x i16> %a16, <i16 poison, i16 poison, i16 poison, i16 poison, i16 1, i16 1, i16 1, i16 1>
  %k0.f = freeze <8 x i16> %k0
  %k0.m = and <8 x i16> %k0.f, <i16 0, i16 0, i16 0, i16 0, i16 -1, i16 -1, i16 -1, i16 -1>
  %k0.x = xor <8 x i16> %k0.m, <i16 0, i16 0, i16 0, i16 0, i16 -32768, i16 0, i16 510, i16 14>
  %k0.r = call i16 @llvm.vector.reduce.or.v8i16(<8 x i16> %k0.x)
  %k0.c = icmp ne i16 %k0.r, 0
  %k1 = lshr <8 x i16> %a16, <i16 poison, i16 3, i16 poison, i16 3, i16 3, i16 3, i16 poison, i16 3>
  %k1.f = freeze <8 x i16> %k1
  %k1.m = and <8 x i16> %k1.f, <i16 0, i16 -1, i16 0, i16 -1, i16 -1, i16 -1, i16 0, i16 -1>
  %k1.x = xor <8 x i16> %k1.m, <i16 0, i16 8191, i16 0, i16 8191, i16 2048, i16 4096, i16 0, i16 0>
  %k1.r = call i16 @llvm.vector.reduce.or.v8i16(<8 x i16> %k1.x)
  %k1.c = icmp ne i16 %k1.r, 0
  %k2 = ashr <4 x i32> %a32, <i32 poison, i32 3, i32 3, i32 poison>
  %k2.f = freeze <4 x i32> %k2
  %k2.m = and <4 x i32> %k2.f, <i32 0, i32 -1, i32 -1, i32 0>
  %k2.x = xor <4 x i32> %k2.m, <i32 0, i32 -1, i32 38177487, i32 0>
  %k2.r = call i32 @llvm.vector.reduce.or.v4i32(<4 x i32> %k2.x)
  %k2.c = icmp ne i32 %k2.r, 0
  %k3 = lshr <4 x i32> %a32, <i32 3, i32 poison, i32 3, i32 3>
  %k3.f = freeze <4 x i32> %k3
  %k3.m = and <4 x i32> %k3.f, <i32 -1, i32 0, i32 -1, i32 -1>
  %k3.x = xor <4 x i32> %k3.m, <i32 0, i32 0, i32 38177487, i32 0>
  %k3.r = call i32 @llvm.vector.reduce.or.v4i32(<4 x i32> %k3.x)
  %k3.c = icmp ne i32 %k3.r, 0
  %k4 = shl <8 x i16> %a16, <i16 0, i16 1, i16 2, i16 3, i16 4, i16 5, i16 15, i16 poison>
  %k4.f = freeze <8 x i16> %k4
  %k4.m = and <8 x i16> %k4.f, <i16 -1, i16 -1, i16 -1, i16 -1, i16 -1, i16 -1, i16 -1, i16 0>
  %k4.x = xor <8 x i16> %k4.m, <i16 1, i16 -4, i16 12, i16 -32, i16 0, i16 0, i16 -32768, i16 0>
  %k4.r = call i16 @llvm.vector.reduce.or.v8i16(<8 x i16> %k4.x)
  %k4.c = icmp ne i16 %k4.r, 0
  %k5 = shl <4 x i32> %a32, <i32 31, i32 0, i32 7, i32 16>
  %k5.f = freeze <4 x i32> %k5
  %k5.m = and <4 x i32> %k5.f, <i32 -1, i32 -1, i32 -1, i32 -1>
  %k5.x = xor <4 x i32> %k5.m, <i32 -2147483648, i32 -8, i32 439041024, i32 327680>
  %k5.r = call i32 @llvm.vector.reduce.or.v4i32(<4 x i32> %k5.x)
  %k5.c = icmp ne i32 %k5.r, 0
  %k6 = shl <16 x i8> %a8, <i8 poison, i8 poison, i8 poison, i8 poison, i8 poison, i8 poison, i8 poison, i8 poison, i8 2, i8 2, i8 2, i8 2, i8 2, i8 2, i8 2, i8 2>
  %k6.f = freeze <16 x i8> %k6
  %k6.m = and <16 x i8> %k6.f, <i8 0, i8 0, i8 0, i8 0, i8 0, i8 0, i8 0, i8 0, i8 -1, i8 -1, i8 -1, i8 -1, i8 -1, i8 -1, i8 -1, i8 -1>
  %k6.x = xor <16 x i8> %k6.m, <i8 0, i8 0, i8 0, i8 0, i8 0, i8 0, i8 0, i8 0, i8 4, i8 0, i8 -4, i8 12, i8 4, i8 0, i8 -4, i8 12>
  %k6.r = call i8 @llvm.vector.reduce.or.v16i8(<16 x i8> %k6.x)
  %k6.c = icmp ne i8 %k6.r, 0
  %k.z0 = zext i1 %k0.c to i32
  %k.s0 = shl i32 %k.z0, 0
  %k.o0 = or i32 0, %k.s0
  %k.z1 = zext i1 %k1.c to i32
  %k.s1 = shl i32 %k.z1, 1
  %k.o1 = or i32 %k.o0, %k.s1
  %k.z2 = zext i1 %k2.c to i32
  %k.s2 = shl i32 %k.z2, 2
  %k.o2 = or i32 %k.o1, %k.s2
  %k.z3 = zext i1 %k3.c to i32
  %k.s3 = shl i32 %k.z3, 3
  %k.o3 = or i32 %k.o2, %k.s3
  %k.z4 = zext i1 %k4.c to i32
  %k.s4 = shl i32 %k.z4, 4
  %k.o4 = or i32 %k.o3, %k.s4
  %k.z5 = zext i1 %k5.c to i32
  %k.s5 = shl i32 %k.z5, 5
  %k.o5 = or i32 %k.o4, %k.s5
  %k.z6 = zext i1 %k6.c to i32
  %k.s6 = shl i32 %k.z6, 6
  %k.o6 = or i32 %k.o5, %k.s6
  ret i32 %k.o6
}

define i32 @main() {
entry:
  %v = call i32 @variable(<16 x i8> <i8 1, i8 -128, i8 127, i8 3, i8 1, i8 -128, i8 127, i8 3, i8 1, i8 -128, i8 127, i8 3, i8 1, i8 -128, i8 127, i8 3>, <16 x i8> <i8 7, i8 7, i8 1, i8 0, i8 0, i8 1, i8 6, i8 1, i8 3, i8 0, i8 0, i8 2, i8 1, i8 2, i8 3, i8 4>, <8 x i16> <i16 1, i16 -2, i16 3, i16 -4, i16 16384, i16 -32768, i16 255, i16 7>, <8 x i16> <i16 0, i16 1, i16 2, i16 3, i16 1, i16 15, i16 8, i16 15>, <4 x i32> <i32 1, i32 -8, i32 305419896, i32 5>, <4 x i32> <i32 31, i32 2, i32 4, i32 0>, <2 x i64> <i64 -1, i64 3>, <2 x i64> <i64 63, i64 1>)
  %k = call i32 @constant(<8 x i16> <i16 1, i16 -2, i16 3, i16 -4, i16 16384, i16 -32768, i16 255, i16 7>, <4 x i32> <i32 1, i32 -8, i32 305419896, i32 5>, <16 x i8> <i8 1, i8 -128, i8 127, i8 3, i8 1, i8 -128, i8 127, i8 3, i8 1, i8 -128, i8 127, i8 3, i8 1, i8 -128, i8 127, i8 3>)
  %k1 = shl i32 %k, 12
  %r = or i32 %v, %k1
  ret i32 %r
}

declare i8 @llvm.vector.reduce.or.v16i8(<16 x i8>)
declare i16 @llvm.vector.reduce.or.v8i16(<8 x i16>)
declare i32 @llvm.vector.reduce.or.v4i32(<4 x i32>)
declare i64 @llvm.vector.reduce.or.v2i64(<2 x i64>)
