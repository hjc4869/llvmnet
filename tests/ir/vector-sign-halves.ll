; The sign of each half of a lane spread over that half, as x264's abs2 computes it: mul(and(lshr(x, h - 1), 1 | 1 << h),
; (1 << h) - 1) in lanes of 2h bits (16, 32 and 64), which runs as one arithmetic shift of the halves (SV-39), the and's
; operands either way round; and look-alikes that keep the multiplication: another factor, another shift, the factor
; first. Each result is compared with the halves' arithmetic shift computed directly; each check adds a bit to the
; failure mask main returns.
target triple = "x86_64-pc-linux-gnu"

define i32 @check(<8 x i16> %v16, <4 x i32> %v32, <2 x i64> %v64) {
entry:
  ; 32-bit lanes: the shape and its expected value.
  %h32 = bitcast <4 x i32> %v32 to <8 x i16>
  %a32 = ashr <8 x i16> %h32, splat (i16 15)
  %e32 = bitcast <8 x i16> %a32 to <4 x i32>
  %s32 = lshr <4 x i32> %v32, splat (i32 15)
  %m32 = and <4 x i32> %s32, splat (i32 65537)
  %r32 = mul <4 x i32> %m32, splat (i32 65535)
  %c32 = icmp ne <4 x i32> %r32, %e32
  %b32 = call i1 @llvm.vector.reduce.or.v4i1(<4 x i1> %c32)
  ; The and's mask first.
  %t32 = lshr <4 x i32> %v32, splat (i32 15)
  %n32 = and <4 x i32> splat (i32 65537), %t32
  %q32 = mul <4 x i32> %n32, splat (i32 65535)
  %d32 = icmp ne <4 x i32> %q32, %e32
  %f32 = call i1 @llvm.vector.reduce.or.v4i1(<4 x i1> %d32)
  ; Look-alike: a factor of 0xfffe leaves the low bit of each half clear.
  %u32 = lshr <4 x i32> %v32, splat (i32 15)
  %k32 = and <4 x i32> %u32, splat (i32 65537)
  %x32 = mul <4 x i32> %k32, splat (i32 65534)
  %w32 = and <4 x i32> %e32, splat (i32 -65538)
  %y32 = icmp ne <4 x i32> %x32, %w32
  %z32 = call i1 @llvm.vector.reduce.or.v4i1(<4 x i1> %y32)
  ; Look-alike: a shift by 14 spreads bit 14 of each half.
  %p32 = lshr <4 x i32> %v32, splat (i32 14)
  %j32 = and <4 x i32> %p32, splat (i32 65537)
  %g32 = mul <4 x i32> %j32, splat (i32 65535)
  %l32 = shl <8 x i16> %h32, splat (i16 1)
  %o32 = ashr <8 x i16> %l32, splat (i16 15)
  %i32e = bitcast <8 x i16> %o32 to <4 x i32>
  %ne32 = icmp ne <4 x i32> %g32, %i32e
  %bit32 = call i1 @llvm.vector.reduce.or.v4i1(<4 x i1> %ne32)
  ; Look-alike: the factor first.
  %v.s = lshr <4 x i32> %v32, splat (i32 15)
  %v.m = and <4 x i32> %v.s, splat (i32 65537)
  %v.r = mul <4 x i32> splat (i32 65535), %v.m
  %v.c = icmp ne <4 x i32> %v.r, %e32
  %v.b = call i1 @llvm.vector.reduce.or.v4i1(<4 x i1> %v.c)
  ; 64-bit lanes.
  %h64 = bitcast <2 x i64> %v64 to <4 x i32>
  %a64 = ashr <4 x i32> %h64, splat (i32 31)
  %e64 = bitcast <4 x i32> %a64 to <2 x i64>
  %s64 = lshr <2 x i64> %v64, splat (i64 31)
  %m64 = and <2 x i64> %s64, splat (i64 4294967297)
  %r64 = mul <2 x i64> %m64, splat (i64 4294967295)
  %c64 = icmp ne <2 x i64> %r64, %e64
  %b64 = call i1 @llvm.vector.reduce.or.v2i1(<2 x i1> %c64)
  ; 16-bit lanes, whose halves are bytes.
  %h16 = bitcast <8 x i16> %v16 to <16 x i8>
  %a16 = ashr <16 x i8> %h16, splat (i8 7)
  %e16 = bitcast <16 x i8> %a16 to <8 x i16>
  %s16 = lshr <8 x i16> %v16, splat (i16 7)
  %m16 = and <8 x i16> %s16, splat (i16 257)
  %r16 = mul <8 x i16> %m16, splat (i16 255)
  %c16 = icmp ne <8 x i16> %r16, %e16
  %b16 = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %c16)
  %w0 = zext i1 %b32 to i32
  %w1 = zext i1 %f32 to i32
  %w2 = zext i1 %z32 to i32
  %w3 = zext i1 %bit32 to i32
  %w4 = zext i1 %v.b to i32
  %w5 = zext i1 %b64 to i32
  %w6 = zext i1 %b16 to i32
  %x1 = shl i32 %w1, 1
  %x2 = shl i32 %w2, 2
  %x3 = shl i32 %w3, 3
  %x4 = shl i32 %w4, 4
  %x5 = shl i32 %w5, 5
  %x6 = shl i32 %w6, 6
  %o1 = or i32 %w0, %x1
  %o2 = or i32 %o1, %x2
  %o3 = or i32 %o2, %x3
  %o4 = or i32 %o3, %x4
  %o5 = or i32 %o4, %x5
  %o6 = or i32 %o5, %x6
  ret i32 %o6
}

define i32 @main() {
entry:
  %a = call i32 @check(<8 x i16> <i16 0, i16 -1, i16 128, i16 -128, i16 127, i16 -32768, i16 32767, i16 255>,
                       <4 x i32> <i32 0, i32 -1, i32 32768, i32 -2147450880>,
                       <2 x i64> <i64 0, i64 -9223372034707292160>)
  %b = call i32 @check(<8 x i16> <i16 -256, i16 256, i16 -32640, i16 16384, i16 -2, i16 1, i16 -129, i16 383>,
                       <4 x i32> <i32 2147483647, i32 65535, i32 -65536, i32 1073758208>,
                       <2 x i64> <i64 -1, i64 2147483648>)
  %c = shl i32 %b, 8
  %r = or i32 %a, %c
  ret i32 %r
}

declare i1 @llvm.vector.reduce.or.v2i1(<2 x i1>)
declare i1 @llvm.vector.reduce.or.v4i1(<4 x i1>)
declare i1 @llvm.vector.reduce.or.v8i1(<8 x i1>)
