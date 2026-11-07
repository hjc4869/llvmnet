; Strided gathers (SV-40): insertelement chains of loads a stride apart in one range, which the typed lowering reads as
; wide loads and one shuffle (llvmnet_strided_gathers). The loads read bytes around the lanes only where the block
; shows them dereferenceable: a global's or a dereferenceable argument's size (known, deref), another access of the
; same range in the same stretch of the block (covered, shared), but not one after a call (called) or through another
; root (plain's lanes are not inbounds of one base and are no gather); a gather whose lanes see a store between them
; is no gather (stored). Otherwise the pieces start at the first lane and end at the last one (isolated, halves,
; doubles). tests/vector-lowering.sh checks each function's -v line, "N strided gathers from M wide loads"; main
; returns a mask of the results that differ from what the lanes hold.
target triple = "x86_64-pc-linux-gnu"

@table = internal global [24 x i8] c"\03\0A\11\18\1F\26\2D\34\3B\42\49\50\57\5E\65\6C\73\7A\81\88\8F\96\9D\A4", align 1
@values = internal global [8 x double] [double 0.5, double 1.5, double 2.5, double 3.5, double 4.5, double 5.5, double 6.5, double 7.5], align 8

define void @known(ptr %out) {
  %a0 = load i8, ptr getelementptr inbounds (i8, ptr @table, i64 1)
  %a1 = load i8, ptr getelementptr inbounds (i8, ptr @table, i64 3)
  %a2 = load i8, ptr getelementptr inbounds (i8, ptr @table, i64 5)
  %a3 = load i8, ptr getelementptr inbounds (i8, ptr @table, i64 7)
  %a4 = load i8, ptr getelementptr inbounds (i8, ptr @table, i64 9)
  %a5 = load i8, ptr getelementptr inbounds (i8, ptr @table, i64 11)
  %a6 = load i8, ptr getelementptr inbounds (i8, ptr @table, i64 13)
  %a7 = load i8, ptr getelementptr inbounds (i8, ptr @table, i64 15)
  %av0 = insertelement <8 x i8> poison, i8 %a0, i64 0
  %av1 = insertelement <8 x i8> %av0, i8 %a1, i64 1
  %av2 = insertelement <8 x i8> %av1, i8 %a2, i64 2
  %av3 = insertelement <8 x i8> %av2, i8 %a3, i64 3
  %av4 = insertelement <8 x i8> %av3, i8 %a4, i64 4
  %av5 = insertelement <8 x i8> %av4, i8 %a5, i64 5
  %av6 = insertelement <8 x i8> %av5, i8 %a6, i64 6
  %av7 = insertelement <8 x i8> %av6, i8 %a7, i64 7
  store <8 x i8> %av7, ptr %out
  ret void
}

define void @deref(ptr dereferenceable(16) %p, ptr %out) {
  %ap1 = getelementptr inbounds i8, ptr %p, i64 2
  %ap2 = getelementptr inbounds i8, ptr %p, i64 4
  %ap3 = getelementptr inbounds i8, ptr %p, i64 6
  %ap4 = getelementptr inbounds i8, ptr %p, i64 8
  %ap5 = getelementptr inbounds i8, ptr %p, i64 10
  %ap6 = getelementptr inbounds i8, ptr %p, i64 12
  %ap7 = getelementptr inbounds i8, ptr %p, i64 14
  %a0 = load i8, ptr %p
  %a1 = load i8, ptr %ap1
  %a2 = load i8, ptr %ap2
  %a3 = load i8, ptr %ap3
  %a4 = load i8, ptr %ap4
  %a5 = load i8, ptr %ap5
  %a6 = load i8, ptr %ap6
  %a7 = load i8, ptr %ap7
  %av0 = insertelement <8 x i8> poison, i8 %a0, i64 0
  %av1 = insertelement <8 x i8> %av0, i8 %a1, i64 1
  %av2 = insertelement <8 x i8> %av1, i8 %a2, i64 2
  %av3 = insertelement <8 x i8> %av2, i8 %a3, i64 3
  %av4 = insertelement <8 x i8> %av3, i8 %a4, i64 4
  %av5 = insertelement <8 x i8> %av4, i8 %a5, i64 5
  %av6 = insertelement <8 x i8> %av5, i8 %a6, i64 6
  %av7 = insertelement <8 x i8> %av6, i8 %a7, i64 7
  store <8 x i8> %av7, ptr %out
  ret void
}

define void @isolated(ptr %p, ptr %out) {
  %ap1 = getelementptr inbounds i8, ptr %p, i64 2
  %ap2 = getelementptr inbounds i8, ptr %p, i64 4
  %ap3 = getelementptr inbounds i8, ptr %p, i64 6
  %ap4 = getelementptr inbounds i8, ptr %p, i64 8
  %ap5 = getelementptr inbounds i8, ptr %p, i64 10
  %ap6 = getelementptr inbounds i8, ptr %p, i64 12
  %ap7 = getelementptr inbounds i8, ptr %p, i64 14
  %a0 = load i8, ptr %p
  %a1 = load i8, ptr %ap1
  %a2 = load i8, ptr %ap2
  %a3 = load i8, ptr %ap3
  %a4 = load i8, ptr %ap4
  %a5 = load i8, ptr %ap5
  %a6 = load i8, ptr %ap6
  %a7 = load i8, ptr %ap7
  %av0 = insertelement <8 x i8> poison, i8 %a0, i64 0
  %av1 = insertelement <8 x i8> %av0, i8 %a1, i64 1
  %av2 = insertelement <8 x i8> %av1, i8 %a2, i64 2
  %av3 = insertelement <8 x i8> %av2, i8 %a3, i64 3
  %av4 = insertelement <8 x i8> %av3, i8 %a4, i64 4
  %av5 = insertelement <8 x i8> %av4, i8 %a5, i64 5
  %av6 = insertelement <8 x i8> %av5, i8 %a6, i64 6
  %av7 = insertelement <8 x i8> %av6, i8 %a7, i64 7
  store <8 x i8> %av7, ptr %out
  ret void
}

define void @covered(ptr %p, ptr %out, ptr %extra) {
  %tail.p = getelementptr inbounds i8, ptr %p, i64 15
  %tail = load i8, ptr %tail.p
  %ap1 = getelementptr inbounds i8, ptr %p, i64 2
  %ap2 = getelementptr inbounds i8, ptr %p, i64 4
  %ap3 = getelementptr inbounds i8, ptr %p, i64 6
  %ap4 = getelementptr inbounds i8, ptr %p, i64 8
  %ap5 = getelementptr inbounds i8, ptr %p, i64 10
  %ap6 = getelementptr inbounds i8, ptr %p, i64 12
  %ap7 = getelementptr inbounds i8, ptr %p, i64 14
  %a0 = load i8, ptr %p
  %a1 = load i8, ptr %ap1
  %a2 = load i8, ptr %ap2
  %a3 = load i8, ptr %ap3
  %a4 = load i8, ptr %ap4
  %a5 = load i8, ptr %ap5
  %a6 = load i8, ptr %ap6
  %a7 = load i8, ptr %ap7
  %av0 = insertelement <8 x i8> poison, i8 %a0, i64 0
  %av1 = insertelement <8 x i8> %av0, i8 %a1, i64 1
  %av2 = insertelement <8 x i8> %av1, i8 %a2, i64 2
  %av3 = insertelement <8 x i8> %av2, i8 %a3, i64 3
  %av4 = insertelement <8 x i8> %av3, i8 %a4, i64 4
  %av5 = insertelement <8 x i8> %av4, i8 %a5, i64 5
  %av6 = insertelement <8 x i8> %av5, i8 %a6, i64 6
  %av7 = insertelement <8 x i8> %av6, i8 %a7, i64 7
  store <8 x i8> %av7, ptr %out
  store i8 %tail, ptr %extra
  ret void
}

define internal void @opaque(ptr %p) noinline {
  store i8 1, ptr %p
  ret void
}

define void @called(ptr %p, ptr %out, ptr %extra) {
  %ap1 = getelementptr inbounds i8, ptr %p, i64 2
  %ap2 = getelementptr inbounds i8, ptr %p, i64 4
  %ap3 = getelementptr inbounds i8, ptr %p, i64 6
  %ap4 = getelementptr inbounds i8, ptr %p, i64 8
  %ap5 = getelementptr inbounds i8, ptr %p, i64 10
  %ap6 = getelementptr inbounds i8, ptr %p, i64 12
  %ap7 = getelementptr inbounds i8, ptr %p, i64 14
  %a0 = load i8, ptr %p
  %a1 = load i8, ptr %ap1
  %a2 = load i8, ptr %ap2
  %a3 = load i8, ptr %ap3
  %a4 = load i8, ptr %ap4
  %a5 = load i8, ptr %ap5
  %a6 = load i8, ptr %ap6
  %a7 = load i8, ptr %ap7
  %av0 = insertelement <8 x i8> poison, i8 %a0, i64 0
  %av1 = insertelement <8 x i8> %av0, i8 %a1, i64 1
  %av2 = insertelement <8 x i8> %av1, i8 %a2, i64 2
  %av3 = insertelement <8 x i8> %av2, i8 %a3, i64 3
  %av4 = insertelement <8 x i8> %av3, i8 %a4, i64 4
  %av5 = insertelement <8 x i8> %av4, i8 %a5, i64 5
  %av6 = insertelement <8 x i8> %av5, i8 %a6, i64 6
  %av7 = insertelement <8 x i8> %av6, i8 %a7, i64 7
  call void @opaque(ptr %extra)
  %tail.p = getelementptr inbounds i8, ptr %p, i64 15
  %tail = load i8, ptr %tail.p
  store <8 x i8> %av7, ptr %out
  store i8 %tail, ptr %extra
  ret void
}

define void @shared(ptr %p, ptr %out) {
  %ap1 = getelementptr inbounds i8, ptr %p, i64 2
  %ap2 = getelementptr inbounds i8, ptr %p, i64 4
  %ap3 = getelementptr inbounds i8, ptr %p, i64 6
  %ap4 = getelementptr inbounds i8, ptr %p, i64 8
  %ap5 = getelementptr inbounds i8, ptr %p, i64 10
  %ap6 = getelementptr inbounds i8, ptr %p, i64 12
  %ap7 = getelementptr inbounds i8, ptr %p, i64 14
  %a0 = load i8, ptr %p
  %a1 = load i8, ptr %ap1
  %a2 = load i8, ptr %ap2
  %a3 = load i8, ptr %ap3
  %a4 = load i8, ptr %ap4
  %a5 = load i8, ptr %ap5
  %a6 = load i8, ptr %ap6
  %a7 = load i8, ptr %ap7
  %av0 = insertelement <8 x i8> poison, i8 %a0, i64 0
  %av1 = insertelement <8 x i8> %av0, i8 %a1, i64 1
  %av2 = insertelement <8 x i8> %av1, i8 %a2, i64 2
  %av3 = insertelement <8 x i8> %av2, i8 %a3, i64 3
  %av4 = insertelement <8 x i8> %av3, i8 %a4, i64 4
  %av5 = insertelement <8 x i8> %av4, i8 %a5, i64 5
  %av6 = insertelement <8 x i8> %av5, i8 %a6, i64 6
  %av7 = insertelement <8 x i8> %av6, i8 %a7, i64 7
  %bp0 = getelementptr inbounds i8, ptr %p, i64 1
  %bp1 = getelementptr inbounds i8, ptr %p, i64 3
  %bp2 = getelementptr inbounds i8, ptr %p, i64 5
  %bp3 = getelementptr inbounds i8, ptr %p, i64 7
  %bp4 = getelementptr inbounds i8, ptr %p, i64 9
  %bp5 = getelementptr inbounds i8, ptr %p, i64 11
  %bp6 = getelementptr inbounds i8, ptr %p, i64 13
  %bp7 = getelementptr inbounds i8, ptr %p, i64 15
  %b0 = load i8, ptr %bp0
  %b1 = load i8, ptr %bp1
  %b2 = load i8, ptr %bp2
  %b3 = load i8, ptr %bp3
  %b4 = load i8, ptr %bp4
  %b5 = load i8, ptr %bp5
  %b6 = load i8, ptr %bp6
  %b7 = load i8, ptr %bp7
  %bv0 = insertelement <8 x i8> poison, i8 %b0, i64 0
  %bv1 = insertelement <8 x i8> %bv0, i8 %b1, i64 1
  %bv2 = insertelement <8 x i8> %bv1, i8 %b2, i64 2
  %bv3 = insertelement <8 x i8> %bv2, i8 %b3, i64 3
  %bv4 = insertelement <8 x i8> %bv3, i8 %b4, i64 4
  %bv5 = insertelement <8 x i8> %bv4, i8 %b5, i64 5
  %bv6 = insertelement <8 x i8> %bv5, i8 %b6, i64 6
  %bv7 = insertelement <8 x i8> %bv6, i8 %b7, i64 7
  %sum = add <8 x i8> %av7, %bv7
  store <8 x i8> %sum, ptr %out
  ret void
}

define void @plain(ptr %p, ptr %out) {
  %ap0 = getelementptr i8, ptr %p, i64 0
  %ap1 = getelementptr i8, ptr %p, i64 2
  %ap2 = getelementptr i8, ptr %p, i64 4
  %ap3 = getelementptr i8, ptr %p, i64 6
  %ap4 = getelementptr i8, ptr %p, i64 8
  %ap5 = getelementptr i8, ptr %p, i64 10
  %ap6 = getelementptr i8, ptr %p, i64 12
  %ap7 = getelementptr i8, ptr %p, i64 14
  %a0 = load i8, ptr %ap0
  %a1 = load i8, ptr %ap1
  %a2 = load i8, ptr %ap2
  %a3 = load i8, ptr %ap3
  %a4 = load i8, ptr %ap4
  %a5 = load i8, ptr %ap5
  %a6 = load i8, ptr %ap6
  %a7 = load i8, ptr %ap7
  %av0 = insertelement <8 x i8> poison, i8 %a0, i64 0
  %av1 = insertelement <8 x i8> %av0, i8 %a1, i64 1
  %av2 = insertelement <8 x i8> %av1, i8 %a2, i64 2
  %av3 = insertelement <8 x i8> %av2, i8 %a3, i64 3
  %av4 = insertelement <8 x i8> %av3, i8 %a4, i64 4
  %av5 = insertelement <8 x i8> %av4, i8 %a5, i64 5
  %av6 = insertelement <8 x i8> %av5, i8 %a6, i64 6
  %av7 = insertelement <8 x i8> %av6, i8 %a7, i64 7
  store <8 x i8> %av7, ptr %out
  ret void
}

define void @stored(ptr %p, ptr %out) {
  %ap1 = getelementptr inbounds i8, ptr %p, i64 2
  %ap2 = getelementptr inbounds i8, ptr %p, i64 4
  %ap3 = getelementptr inbounds i8, ptr %p, i64 6
  %ap4 = getelementptr inbounds i8, ptr %p, i64 8
  %ap5 = getelementptr inbounds i8, ptr %p, i64 10
  %ap6 = getelementptr inbounds i8, ptr %p, i64 12
  %ap7 = getelementptr inbounds i8, ptr %p, i64 14
  %a0 = load i8, ptr %p
  %a1 = load i8, ptr %ap1
  store i8 0, ptr %ap2
  %a2 = load i8, ptr %ap2
  %a3 = load i8, ptr %ap3
  %a4 = load i8, ptr %ap4
  %a5 = load i8, ptr %ap5
  %a6 = load i8, ptr %ap6
  %a7 = load i8, ptr %ap7
  %av0 = insertelement <8 x i8> poison, i8 %a0, i64 0
  %av1 = insertelement <8 x i8> %av0, i8 %a1, i64 1
  %av2 = insertelement <8 x i8> %av1, i8 %a2, i64 2
  %av3 = insertelement <8 x i8> %av2, i8 %a3, i64 3
  %av4 = insertelement <8 x i8> %av3, i8 %a4, i64 4
  %av5 = insertelement <8 x i8> %av4, i8 %a5, i64 5
  %av6 = insertelement <8 x i8> %av5, i8 %a6, i64 6
  %av7 = insertelement <8 x i8> %av6, i8 %a7, i64 7
  store <8 x i8> %av7, ptr %out
  ret void
}

define void @halves(ptr %p, ptr %out) {
  %ap1 = getelementptr inbounds i16, ptr %p, i64 3
  %ap2 = getelementptr inbounds i16, ptr %p, i64 6
  %ap3 = getelementptr inbounds i16, ptr %p, i64 9
  %ap4 = getelementptr inbounds i16, ptr %p, i64 12
  %ap5 = getelementptr inbounds i16, ptr %p, i64 15
  %ap6 = getelementptr inbounds i16, ptr %p, i64 18
  %ap7 = getelementptr inbounds i16, ptr %p, i64 21
  %a0 = load i16, ptr %p
  %a1 = load i16, ptr %ap1
  %a2 = load i16, ptr %ap2
  %a3 = load i16, ptr %ap3
  %a4 = load i16, ptr %ap4
  %a5 = load i16, ptr %ap5
  %a6 = load i16, ptr %ap6
  %a7 = load i16, ptr %ap7
  %av0 = insertelement <8 x i16> poison, i16 %a0, i64 0
  %av1 = insertelement <8 x i16> %av0, i16 %a1, i64 1
  %av2 = insertelement <8 x i16> %av1, i16 %a2, i64 2
  %av3 = insertelement <8 x i16> %av2, i16 %a3, i64 3
  %av4 = insertelement <8 x i16> %av3, i16 %a4, i64 4
  %av5 = insertelement <8 x i16> %av4, i16 %a5, i64 5
  %av6 = insertelement <8 x i16> %av5, i16 %a6, i64 6
  %av7 = insertelement <8 x i16> %av6, i16 %a7, i64 7
  store <8 x i16> %av7, ptr %out
  ret void
}

define void @doubles(ptr %p, ptr %out) {
  %ap1 = getelementptr inbounds double, ptr %p, i64 2
  %ap2 = getelementptr inbounds double, ptr %p, i64 4
  %ap3 = getelementptr inbounds double, ptr %p, i64 6
  %a0 = load double, ptr %p
  %a1 = load double, ptr %ap1
  %a2 = load double, ptr %ap2
  %a3 = load double, ptr %ap3
  %av0 = insertelement <4 x double> poison, double %a0, i64 0
  %av1 = insertelement <4 x double> %av0, double %a1, i64 1
  %av2 = insertelement <4 x double> %av1, double %a2, i64 2
  %av3 = insertelement <4 x double> %av2, double %a3, i64 3
  store <4 x double> %av3, ptr %out
  ret void
}

define i32 @main() {
entry:
  %buf = alloca [64 x i8], align 16
  %out = alloca [32 x i8], align 16
  %extra = alloca i8
  br label %fill
fill:
  %i = phi i64 [ 0, %entry ], [ %next, %fill ]
  %i8 = trunc i64 %i to i8
  %m = mul i8 %i8, 7
  %val = add i8 %m, 3
  %slot = getelementptr inbounds i8, ptr %buf, i64 %i
  store volatile i8 %val, ptr %slot
  %next = add i64 %i, 1
  %done = icmp eq i64 %next, 64
  br i1 %done, label %run, label %fill
run:
  %buf1 = getelementptr inbounds i8, ptr %buf, i64 1
  %buf2 = getelementptr inbounds i8, ptr %buf, i64 2
  %buf32 = getelementptr inbounds i8, ptr %buf, i64 32
  call void @known(ptr %out)
  %known.r = load <8 x i8>, ptr %out
  %known.ne = icmp ne <8 x i8> %known.r, <i8 10, i8 24, i8 38, i8 52, i8 66, i8 80, i8 94, i8 108>
  %known.any = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %known.ne)
  %known.bit = select i1 %known.any, i32 1, i32 0
  %mask0 = or i32 0, %known.bit
  call void @deref(ptr %buf, ptr %out)
  %deref.r = load <8 x i8>, ptr %out
  %deref.ne = icmp ne <8 x i8> %deref.r, <i8 3, i8 17, i8 31, i8 45, i8 59, i8 73, i8 87, i8 101>
  %deref.any = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %deref.ne)
  %deref.bit = select i1 %deref.any, i32 2, i32 0
  %mask1 = or i32 %mask0, %deref.bit
  call void @isolated(ptr %buf1, ptr %out)
  %isolated.r = load <8 x i8>, ptr %out
  %isolated.ne = icmp ne <8 x i8> %isolated.r, <i8 10, i8 24, i8 38, i8 52, i8 66, i8 80, i8 94, i8 108>
  %isolated.any = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %isolated.ne)
  %isolated.bit = select i1 %isolated.any, i32 4, i32 0
  %mask2 = or i32 %mask1, %isolated.bit
  call void @covered(ptr %buf2, ptr %out, ptr %extra)
  %covered.r = load <8 x i8>, ptr %out
  %covered.ne = icmp ne <8 x i8> %covered.r, <i8 17, i8 31, i8 45, i8 59, i8 73, i8 87, i8 101, i8 115>
  %covered.any = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %covered.ne)
  %covered.bit = select i1 %covered.any, i32 8, i32 0
  %mask3 = or i32 %mask2, %covered.bit
  %covered.x = load i8, ptr %extra
  %covered.xne = icmp ne i8 %covered.x, 122
  %covered.xbit = select i1 %covered.xne, i32 524288, i32 0
  %maskx3 = or i32 %mask3, %covered.xbit
  call void @called(ptr %buf, ptr %out, ptr %extra)
  %called.r = load <8 x i8>, ptr %out
  %called.ne = icmp ne <8 x i8> %called.r, <i8 3, i8 17, i8 31, i8 45, i8 59, i8 73, i8 87, i8 101>
  %called.any = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %called.ne)
  %called.bit = select i1 %called.any, i32 16, i32 0
  %mask4 = or i32 %maskx3, %called.bit
  %called.x = load i8, ptr %extra
  %called.xne = icmp ne i8 %called.x, 108
  %called.xbit = select i1 %called.xne, i32 1048576, i32 0
  %maskx4 = or i32 %mask4, %called.xbit
  call void @shared(ptr %buf, ptr %out)
  %shared.r = load <8 x i8>, ptr %out
  %shared.ne = icmp ne <8 x i8> %shared.r, <i8 13, i8 41, i8 69, i8 97, i8 125, i8 -103, i8 -75, i8 -47>
  %shared.any = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %shared.ne)
  %shared.bit = select i1 %shared.any, i32 32, i32 0
  %mask5 = or i32 %maskx4, %shared.bit
  call void @plain(ptr %buf, ptr %out)
  %plain.r = load <8 x i8>, ptr %out
  %plain.ne = icmp ne <8 x i8> %plain.r, <i8 3, i8 17, i8 31, i8 45, i8 59, i8 73, i8 87, i8 101>
  %plain.any = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %plain.ne)
  %plain.bit = select i1 %plain.any, i32 64, i32 0
  %mask6 = or i32 %mask5, %plain.bit
  call void @halves(ptr %buf, ptr %out)
  %halves.r = load <8 x i16>, ptr %out
  %halves.ne = icmp ne <8 x i16> %halves.r, <i16 2563, i16 13357, i16 24151, i16 -30591, i16 -19797, i16 -9003, i16 1791, i16 12329>
  %halves.any = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %halves.ne)
  %halves.bit = select i1 %halves.any, i32 128, i32 0
  %mask7 = or i32 %mask6, %halves.bit
  call void @stored(ptr %buf32, ptr %out)
  %stored.r = load <8 x i8>, ptr %out
  %stored.ne = icmp ne <8 x i8> %stored.r, <i8 -29, i8 -15, i8 0, i8 13, i8 27, i8 41, i8 55, i8 69>
  %stored.any = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %stored.ne)
  %stored.bit = select i1 %stored.any, i32 256, i32 0
  %mask8 = or i32 %mask7, %stored.bit
  call void @doubles(ptr @values, ptr %out)
  %doubles.r = load <4 x double>, ptr %out
  %doubles.ne = fcmp une <4 x double> %doubles.r, <double 0.5, double 2.5, double 4.5, double 6.5>
  %doubles.any = call i1 @llvm.vector.reduce.or.v4i1(<4 x i1> %doubles.ne)
  %doubles.bit = select i1 %doubles.any, i32 512, i32 0
  %result = or i32 %mask8, %doubles.bit
  ret i32 %result
}

declare i1 @llvm.vector.reduce.or.v8i1(<8 x i1>)
declare i1 @llvm.vector.reduce.or.v4i1(<4 x i1>)
