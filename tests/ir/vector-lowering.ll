; The typed vector lowering (ADR-16 Level 1) on what C vector extensions do not reach: every fcmp predicate with
; NaN, pointer vectors, i1 vectors shuffled and indexed across lane widths, masks in memory, one-lane and odd vectors,
; reductions in an optnone function (expand-reductions skips those), and intrinsics without a lowering, whose
; functions are scalarized first. Each check adds a distinct bit to the failure mask main returns.
target triple = "x86_64-pc-linux-gnu"

@nan = global float 0x7FF8000000000000
@words = global [4 x i32] [i32 10, i32 20, i32 30, i32 40]
@records = global [2 x { i8, [3 x i16] }] [{ i8, [3 x i16] } { i8 1, [3 x i16] [i16 2, i16 3, i16 4] }, { i8, [3 x i16] } { i8 5, [3 x i16] [i16 6, i16 7, i16 8] }]
@triples = global [2 x [3 x i16]] [[3 x i16] [i16 1, i16 2, i16 3], [3 x i16] [i16 4, i16 5, i16 6]]
@one = global i64 1
@sink = global <3 x i32> zeroinitializer, align 16

define i32 @predicates() {
entry:
  %nan = load float, ptr @nan
  %a0 = insertelement <4 x float> <float 1.0, float 2.0, float 3.0, float -0.0>, float %nan, i32 1
  %b0 = insertelement <4 x float> <float 1.0, float 1.0, float 4.0, float 0.0>, float %nan, i32 2
  ; lanes: (1, 1) equal, (NaN, 1) unordered, (3, NaN) unordered, (-0, +0) equal
  %oeq = fcmp oeq <4 x float> %a0, %b0
  %ogt = fcmp ogt <4 x float> %a0, %b0
  %oge = fcmp oge <4 x float> %a0, %b0
  %olt = fcmp olt <4 x float> %a0, %b0
  %ole = fcmp ole <4 x float> %a0, %b0
  %one = fcmp one <4 x float> %a0, %b0
  %ord = fcmp ord <4 x float> %a0, %b0
  %uno = fcmp uno <4 x float> %a0, %b0
  %ueq = fcmp ueq <4 x float> %a0, %b0
  %ugt = fcmp ugt <4 x float> %a0, %b0
  %uge = fcmp uge <4 x float> %a0, %b0
  %ult = fcmp ult <4 x float> %a0, %b0
  %ule = fcmp ule <4 x float> %a0, %b0
  %une = fcmp une <4 x float> %a0, %b0
  %true = fcmp true <4 x float> %a0, %b0
  %false = fcmp false <4 x float> %a0, %b0
  %m0 = bitcast <4 x i1> %oeq to i4
  %m1 = bitcast <4 x i1> %ogt to i4
  %m2 = bitcast <4 x i1> %oge to i4
  %m3 = bitcast <4 x i1> %olt to i4
  %m4 = bitcast <4 x i1> %ole to i4
  %m5 = bitcast <4 x i1> %one to i4
  %m6 = bitcast <4 x i1> %ord to i4
  %m7 = bitcast <4 x i1> %uno to i4
  %m8 = bitcast <4 x i1> %ueq to i4
  %m9 = bitcast <4 x i1> %ugt to i4
  %m10 = bitcast <4 x i1> %uge to i4
  %m11 = bitcast <4 x i1> %ult to i4
  %m12 = bitcast <4 x i1> %ule to i4
  %m13 = bitcast <4 x i1> %une to i4
  %m14 = bitcast <4 x i1> %true to i4
  %m15 = bitcast <4 x i1> %false to i4
  ; Expected, bit i for lane i: oeq 1001, ogt 0000, oge 1001, olt 0000, ole 1001, one 0000, ord 1001, uno 0110,
  ; ueq 1111, ugt 0110, uge 1111, ult 0110, ule 1111, une 0110, true 1111, false 0000.
  %c0 = icmp ne i4 %m0, 9
  %c1 = icmp ne i4 %m1, 0
  %c2 = icmp ne i4 %m2, 9
  %c3 = icmp ne i4 %m3, 0
  %c4 = icmp ne i4 %m4, 9
  %c5 = icmp ne i4 %m5, 0
  %c6 = icmp ne i4 %m6, 9
  %c7 = icmp ne i4 %m7, 6
  %c8 = icmp ne i4 %m8, 15
  %c9 = icmp ne i4 %m9, 6
  %c10 = icmp ne i4 %m10, 15
  %c11 = icmp ne i4 %m11, 6
  %c12 = icmp ne i4 %m12, 15
  %c13 = icmp ne i4 %m13, 6
  %c14 = icmp ne i4 %m14, 15
  %c15 = icmp ne i4 %m15, 0
  %v = insertelement <16 x i1> poison, i1 %c0, i32 0
  %v1 = insertelement <16 x i1> %v, i1 %c1, i32 1
  %v2 = insertelement <16 x i1> %v1, i1 %c2, i32 2
  %v3 = insertelement <16 x i1> %v2, i1 %c3, i32 3
  %v4 = insertelement <16 x i1> %v3, i1 %c4, i32 4
  %v5 = insertelement <16 x i1> %v4, i1 %c5, i32 5
  %v6 = insertelement <16 x i1> %v5, i1 %c6, i32 6
  %v7 = insertelement <16 x i1> %v6, i1 %c7, i32 7
  %v8 = insertelement <16 x i1> %v7, i1 %c8, i32 8
  %v9 = insertelement <16 x i1> %v8, i1 %c9, i32 9
  %v10 = insertelement <16 x i1> %v9, i1 %c10, i32 10
  %v11 = insertelement <16 x i1> %v10, i1 %c11, i32 11
  %v12 = insertelement <16 x i1> %v11, i1 %c12, i32 12
  %v13 = insertelement <16 x i1> %v12, i1 %c13, i32 13
  %v14 = insertelement <16 x i1> %v13, i1 %c14, i32 14
  %v15 = insertelement <16 x i1> %v14, i1 %c15, i32 15
  %bits = bitcast <16 x i1> %v15 to i16
  %any = icmp ne i16 %bits, 0
  %result = zext i1 %any to i32
  ret i32 %result
}

define i32 @pointers() {
entry:
  %first = getelementptr [4 x i32], ptr @words, i64 0, i64 1
  %second = getelementptr [4 x i32], ptr @words, i64 0, i64 3
  %p0 = insertelement <2 x ptr> poison, ptr %first, i32 0
  %p = insertelement <2 x ptr> %p0, ptr %second, i32 1
  %swapped = shufflevector <2 x ptr> %p, <2 x ptr> poison, <2 x i32> <i32 1, i32 0>
  %less = icmp ult <2 x ptr> %p, %swapped
  %lessbits = bitcast <2 x i1> %less to i2
  %c0 = icmp ne i2 %lessbits, 1
  %addresses = ptrtoint <2 x ptr> %p to <2 x i64>
  %difference = sub <2 x i64> %addresses, <i64 0, i64 8>
  %lane0 = extractelement <2 x i64> %difference, i32 0
  %lane1 = extractelement <2 x i64> %difference, i32 1
  %c1 = icmp ne i64 %lane0, %lane1
  %back = inttoptr <2 x i64> %addresses to <2 x ptr>
  %loaded = extractelement <2 x ptr> %back, i32 1
  %value = load i32, ptr %loaded
  %c2 = icmp ne i32 %value, 40
  %low = trunc <2 x i64> %addresses to <2 x i32>
  %gap = extractelement <2 x i32> %low, i32 1
  %start = extractelement <2 x i32> %low, i32 0
  %distance = sub i32 %gap, %start
  %c3 = icmp ne i32 %distance, 8
  ; Vector getelementptrs (SV-25), which the driver expands into address arithmetic: a vector base with a scalar index,
  ; a scalar base with vector indices, a struct field between vector indices, and a variable index with a stride of 6.
  %previous = getelementptr i32, <2 x ptr> %p, i64 -1
  %previous1 = extractelement <2 x ptr> %previous, i32 1
  %value30 = load i32, ptr %previous1
  %c4 = icmp ne i32 %value30, 30
  %spread = getelementptr [4 x i32], ptr @words, i64 0, <2 x i64> <i64 3, i64 2>
  %spread0 = extractelement <2 x ptr> %spread, i32 0
  %value40 = load i32, ptr %spread0
  %c5 = icmp ne i32 %value40, 40
  %fields = getelementptr { i8, [3 x i16] }, ptr @records, <2 x i64> <i64 0, i64 1>, i32 1, <2 x i64> <i64 2, i64 1>
  %field0 = extractelement <2 x ptr> %fields, i32 0
  %field1 = extractelement <2 x ptr> %fields, i32 1
  %value4 = load i16, ptr %field0
  %value7 = load i16, ptr %field1
  %fieldsum = add i16 %value4, %value7
  %c6 = icmp ne i16 %fieldsum, 11
  %one = load i64, ptr @one
  %rowsof = insertelement <2 x i64> <i64 0, i64 poison>, i64 %one, i32 1
  %rows = getelementptr [3 x i16], ptr @triples, <2 x i64> %rowsof, i64 2
  %row1 = extractelement <2 x ptr> %rows, i32 1
  %value6 = load i16, ptr %row1
  %c7 = icmp ne i16 %value6, 6
  %f01 = or i1 %c0, %c1
  %f23 = or i1 %c2, %c3
  %f45 = or i1 %c4, %c5
  %f67 = or i1 %c6, %c7
  %f03 = or i1 %f01, %f23
  %f47 = or i1 %f45, %f67
  %failed = or i1 %f03, %f47
  %result = zext i1 %failed to i32
  ret i32 %result
}

define i32 @masks(i32 %index) {
entry:
  %wide = icmp sgt <8 x i32> <i32 1, i32 -1, i32 2, i32 -2, i32 3, i32 -3, i32 4, i32 -4>, zeroinitializer
  ; i1 shuffles between mask lane widths: 8 lanes (16-bit) to 4 (32-bit) and to 16 (8-bit).
  %odd = shufflevector <8 x i1> %wide, <8 x i1> poison, <4 x i32> <i32 1, i32 3, i32 5, i32 6>
  %oddbits = bitcast <4 x i1> %odd to i4
  %c0 = icmp ne i4 %oddbits, 8
  %twice = shufflevector <8 x i1> %wide, <8 x i1> zeroinitializer, <16 x i32> <i32 0, i32 8, i32 1, i32 9, i32 2, i32 10, i32 3, i32 11, i32 4, i32 12, i32 5, i32 13, i32 6, i32 14, i32 7, i32 15>
  %twicebits = bitcast <16 x i1> %twice to i16
  %c1 = icmp ne i16 %twicebits, 4369
  ; Variable lane indices on a mask and on a 256-bit vector.
  %lane = extractelement <8 x i1> %wide, i32 %index
  %c2 = icmp ne i1 %lane, true
  %cleared = insertelement <8 x i1> %wide, i1 false, i32 %index
  %clearedbits = bitcast <8 x i1> %cleared to i8
  %c3 = icmp ne i8 %clearedbits, 81
  %vector = insertelement <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>, i32 99, i32 %index
  %plus = add i32 %index, 3
  %element = extractelement <8 x i32> %vector, i32 %plus
  %c4 = icmp ne i32 %element, 5
  %same = extractelement <8 x i32> %vector, i32 %index
  %c5 = icmp ne i32 %same, 99
  ; Masks in memory are bits: <3 x i1> stores three bits of one byte.
  %slot = alloca i8
  store i8 255, ptr %slot
  store <3 x i1> <i1 true, i1 false, i1 true>, ptr %slot
  %raw = load i8, ptr %slot
  %rawbits = and i8 %raw, 7
  %c6 = icmp ne i8 %rawbits, 5
  %again = load <3 x i1>, ptr %slot
  %againbits = bitcast <3 x i1> %again to i3
  %c7 = icmp ne i3 %againbits, -3
  ; A mask carried around a loop, and a select on a scalar condition.
  br label %loop
loop:
  %count = phi i32 [ 0, %entry ], [ %next, %loop ]
  %carried = phi <4 x i1> [ zeroinitializer, %entry ], [ %updated, %loop ]
  %step = insertelement <4 x i32> poison, i32 %count, i32 0
  %splat = shufflevector <4 x i32> %step, <4 x i32> poison, <4 x i32> zeroinitializer
  %hit = icmp eq <4 x i32> %splat, <i32 0, i32 2, i32 9, i32 3>
  %updated = or <4 x i1> %carried, %hit
  %next = add i32 %count, 1
  %done = icmp eq i32 %next, 5
  br i1 %done, label %exit, label %loop
exit:
  %carriedbits = bitcast <4 x i1> %updated to i4
  %c8 = icmp ne i4 %carriedbits, 11
  %chosen = select i1 %c8, <2 x double> <double 1.0, double 2.0>, <2 x double> <double 3.0, double 4.0>
  %choice = extractelement <2 x double> %chosen, i32 1
  %c9 = fcmp une double %choice, 4.0
  %r0 = zext i1 %c0 to i32
  %r1 = zext i1 %c1 to i32
  %r2 = zext i1 %c2 to i32
  %r3 = zext i1 %c3 to i32
  %r4 = zext i1 %c4 to i32
  %r5 = zext i1 %c5 to i32
  %r6 = zext i1 %c6 to i32
  %r7 = zext i1 %c7 to i32
  %r8 = zext i1 %c8 to i32
  %r9 = zext i1 %c9 to i32
  %s1 = shl i32 %r1, 1
  %s2 = shl i32 %r2, 2
  %s3 = shl i32 %r3, 3
  %s4 = shl i32 %r4, 4
  %s5 = shl i32 %r5, 5
  %s6 = shl i32 %r6, 6
  %s7 = shl i32 %r7, 7
  %s8 = shl i32 %r8, 8
  %s9 = shl i32 %r9, 9
  %o1 = or i32 %r0, %s1
  %o2 = or i32 %o1, %s2
  %o3 = or i32 %o2, %s3
  %o4 = or i32 %o3, %s4
  %o5 = or i32 %o4, %s5
  %o6 = or i32 %o5, %s6
  %o7 = or i32 %o6, %s7
  %o8 = or i32 %o7, %s8
  %o9 = or i32 %o8, %s9
  ret i32 %o9
}

define i32 @shapes() {
entry:
  ; One-lane vectors, 64-bit vectors as integers and doubles, and a volatile three-lane store.
  %one = insertelement <1 x i64> poison, i64 41, i32 0
  %incremented = add <1 x i64> %one, <i64 1>
  %scalar = bitcast <1 x i64> %incremented to i64
  %c0 = icmp ne i64 %scalar, 42
  %pair = bitcast i64 4294967298 to <2 x i32>
  %bytes = bitcast <2 x i32> %pair to <8 x i8>
  %byte4 = extractelement <8 x i8> %bytes, i32 4
  %c1 = icmp ne i8 %byte4, 1
  %pairsum = add <2 x i32> %pair, %pair
  %asdouble = bitcast <2 x i32> %pairsum to double
  %asint = bitcast double %asdouble to i64
  %c2 = icmp ne i64 %asint, 8589934596
  store volatile <3 x i32> <i32 7, i32 8, i32 9>, ptr @sink
  %three = load volatile <3 x i32>, ptr @sink
  %third = extractelement <3 x i32> %three, i32 2
  %c3 = icmp ne i32 %third, 9
  %mask3 = icmp sgt <3 x i32> %three, <i32 7, i32 7, i32 7>
  %mask3bits = bitcast <3 x i1> %mask3 to i3
  %c4 = icmp ne i3 %mask3bits, 6
  %f01 = or i1 %c0, %c1
  %f23 = or i1 %c2, %c3
  %f = or i1 %f01, %f23
  %failed = or i1 %f, %c4
  %result = zext i1 %failed to i32
  ret i32 %result
}

define i32 @reductions(<16 x i8> %bytes, <4 x float> %floats, <8 x i32> %ints) noinline optnone {
entry:
  %umax = call i8 @llvm.vector.reduce.umax.v16i8(<16 x i8> %bytes)
  %c0 = icmp ne i8 %umax, -6
  %smin = call i8 @llvm.vector.reduce.smin.v16i8(<16 x i8> %bytes)
  %c1 = icmp ne i8 %smin, -6
  %sum = call i32 @llvm.vector.reduce.add.v8i32(<8 x i32> %ints)
  %c2 = icmp ne i32 %sum, 36
  %product = call i32 @llvm.vector.reduce.mul.v8i32(<8 x i32> %ints)
  %c3 = icmp ne i32 %product, 40320
  %ordered = call float @llvm.vector.reduce.fadd.v4f32(float 1.0e8, <4 x float> %floats)
  %c4 = fcmp une float %ordered, 1.0e8
  %reassociated = call reassoc float @llvm.vector.reduce.fadd.v4f32(float -0.0, <4 x float> %floats)
  %c5 = fcmp une float %reassociated, 4.0
  %positive = icmp sgt <8 x i32> %ints, <i32 4, i32 4, i32 4, i32 4, i32 4, i32 4, i32 4, i32 4>
  %some = call i1 @llvm.vector.reduce.or.v8i1(<8 x i1> %positive)
  %every = call i1 @llvm.vector.reduce.and.v8i1(<8 x i1> %positive)
  %c6 = icmp ne i1 %some, true
  %c7 = icmp ne i1 %every, false
  %f01 = or i1 %c0, %c1
  %f23 = or i1 %c2, %c3
  %f45 = or i1 %c4, %c5
  %f67 = or i1 %c6, %c7
  %f0123 = or i1 %f01, %f23
  %f4567 = or i1 %f45, %f67
  %failed = or i1 %f0123, %f4567
  %result = zext i1 %failed to i32
  ret i32 %result
}

define i32 @intrinsics(<4 x i32> %words, <4 x float> %floats, <2 x double> %doubles) {
entry:
  ; %words = <0x01020304, 1, -1, 0x00010000>, %floats = <300.0, -300.0, NaN, 5.7>, %doubles = <-5.0, 1.0e6>
  %swapped = call <4 x i32> @llvm.bswap.v4i32(<4 x i32> %words)
  %s0 = extractelement <4 x i32> %swapped, i32 0
  %c0 = icmp ne i32 %s0, 67305985
  %reversed = call <4 x i32> @llvm.vector.reverse.v4i32(<4 x i32> %words)
  %r0 = extractelement <4 x i32> %reversed, i32 0
  %c1 = icmp ne i32 %r0, 65536
  %upper = call <2 x i32> @llvm.vector.extract.v2i32.v4i32(<4 x i32> %words, i64 2)
  %u1 = extractelement <2 x i32> %upper, i32 1
  %c2 = icmp ne i32 %u1, 65536
  %wide = call <8 x i32> @llvm.vector.insert.v8i32.v4i32(<8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>, <4 x i32> %words, i64 4)
  %w5 = extractelement <8 x i32> %wide, i32 5
  %w3 = extractelement <8 x i32> %wide, i32 3
  %c3a = icmp ne i32 %w5, 1
  %c3b = icmp ne i32 %w3, 3
  %c3 = or i1 %c3a, %c3b
  ; Saturating conversions at the result's width: <127, -128, 0, 5> and <0, 65535>.
  %bytes = call <4 x i8> @llvm.fptosi.sat.v4i8.v4f32(<4 x float> %floats)
  %bytebits = bitcast <4 x i8> %bytes to i32
  %c4 = icmp ne i32 %bytebits, 83919999
  %halves = call <2 x i16> @llvm.fptoui.sat.v2i16.v2f64(<2 x double> %doubles)
  %halfbits = bitcast <2 x i16> %halves to i32
  %c5 = icmp ne i32 %halfbits, -65536
  ; A funnel shift with a count per lane: <2, 0x80000001, 2, 0xA0000000>.
  %funnel = call <4 x i32> @llvm.fshl.v4i32(<4 x i32> <i32 -2147483647, i32 -2147483647, i32 -2147483647, i32 -2147483647>, <4 x i32> <i32 1073741824, i32 1073741824, i32 1073741824, i32 1073741824>, <4 x i32> <i32 1, i32 0, i32 33, i32 31>)
  %f0 = extractelement <4 x i32> %funnel, i32 0
  %f1 = extractelement <4 x i32> %funnel, i32 1
  %f3 = extractelement <4 x i32> %funnel, i32 3
  %c6a = icmp ne i32 %f0, 2
  %c6b = icmp ne i32 %f1, -2147483647
  %c6c = icmp ne i32 %f3, -1610612736
  %c6ab = or i1 %c6a, %c6b
  %c6 = or i1 %c6ab, %c6c
  ; Bit counts of zero lanes are the width: ctlz <32, 31, 0, 15>, cttz <64, 3>.
  %leading = call <4 x i32> @llvm.ctlz.v4i32(<4 x i32> <i32 0, i32 1, i32 -1, i32 65536>, i1 false)
  %leadingsum = call i32 @llvm.vector.reduce.add.v4i32(<4 x i32> %leading)
  %c7 = icmp ne i32 %leadingsum, 78
  %trailing = call <2 x i64> @llvm.cttz.v2i64(<2 x i64> <i64 0, i64 8>, i1 false)
  %t0 = extractelement <2 x i64> %trailing, i32 0
  %t1 = extractelement <2 x i64> %trailing, i32 1
  %c8a = icmp ne i64 %t0, 64
  %c8b = icmp ne i64 %t1, 3
  %c8 = or i1 %c8a, %c8b
  ; Reductions of three lanes (lane by lane), of two (shuffled), and NaN in maxnum and maximum.
  %three = shufflevector <4 x i32> %words, <4 x i32> poison, <3 x i32> <i32 1, i32 2, i32 3>
  %umax = call i32 @llvm.vector.reduce.umax.v3i32(<3 x i32> %three)
  %c9 = icmp ne i32 %umax, -1
  %pair = shufflevector <4 x i32> %words, <4 x i32> poison, <2 x i32> <i32 1, i32 3>
  %pairsum = call i32 @llvm.vector.reduce.add.v2i32(<2 x i32> %pair)
  %c10 = icmp ne i32 %pairsum, 65537
  %largest = call float @llvm.vector.reduce.fmax.v4f32(<4 x float> %floats)
  %c11 = fcmp une float %largest, 300.0
  %maximum = call float @llvm.vector.reduce.fmaximum.v4f32(<4 x float> %floats)
  %c12 = fcmp ord float %maximum, 0.0
  %o1 = or i1 %c0, %c1
  %o2 = or i1 %o1, %c2
  %o3 = or i1 %o2, %c3
  %o4 = or i1 %o3, %c4
  %o5 = or i1 %o4, %c5
  %o6 = or i1 %o5, %c6
  %o7 = or i1 %o6, %c7
  %o8 = or i1 %o7, %c8
  %o9 = or i1 %o8, %c9
  %o10 = or i1 %o9, %c10
  %o11 = or i1 %o10, %c11
  %o12 = or i1 %o11, %c12
  %result = zext i1 %o12 to i32
  ret i32 %result
}

define i32 @fallback(<4 x float> %floats, <4 x i24> %odd) {
entry:
  ; Neither has a typed lowering: the function is scalarized before translation.
  %saturated = call <4 x i32> @llvm.fptosi.sat.v4i32.v4f32(<4 x float> %floats)
  %lane3 = extractelement <4 x i32> %saturated, i32 3
  %c0 = icmp ne i32 %lane3, 2147483647
  %doubled = add <4 x i24> %odd, %odd
  %top = extractelement <4 x i24> %doubled, i32 1
  %c1 = icmp ne i24 %top, -2
  ; i1 lanes to floats: true is 1.0 unsigned and -1.0 signed.
  %positive = fcmp ogt <4 x float> %floats, zeroinitializer
  %unsigned = uitofp <4 x i1> %positive to <4 x float>
  %signed = sitofp <4 x i1> %positive to <4 x float>
  %u3 = extractelement <4 x float> %unsigned, i32 3
  %s3 = extractelement <4 x float> %signed, i32 3
  %u1 = extractelement <4 x float> %unsigned, i32 1
  %c2 = fcmp une float %u3, 1.0
  %c3 = fcmp une float %s3, -1.0
  %c4 = fcmp une float %u1, 0.0
  %c01 = or i1 %c0, %c1
  %c23 = or i1 %c2, %c3
  %c234 = or i1 %c23, %c4
  %failed = or i1 %c01, %c234
  %result = zext i1 %failed to i32
  ret i32 %result
}

define i32 @main() {
entry:
  %p = call i32 @predicates()
  %q = call i32 @pointers()
  %m = call i32 @masks(i32 2)
  %s = call i32 @shapes()
  %r = call i32 @reductions(<16 x i8> <i8 1, i8 2, i8 3, i8 -6, i8 5, i8 6, i8 7, i8 8, i8 9, i8 10, i8 11, i8 12, i8 13, i8 14, i8 15, i8 16>,
                            <4 x float> <float 1.0, float 1.0, float 1.0, float 1.0>,
                            <8 x i32> <i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7, i32 8>)
  %f = call i32 @fallback(<4 x float> <float 1.0, float 0x7FF8000000000000, float 0xC200000000000000, float 0x4200000000000000>, <4 x i24> <i24 1, i24 -1, i24 2, i24 3>)
  %q1 = shl i32 %q, 1
  %m1 = shl i32 %m, 2
  %s1 = shl i32 %s, 12
  %r1 = shl i32 %r, 13
  %f1 = shl i32 %f, 14
  %i = call i32 @intrinsics(<4 x i32> <i32 16909060, i32 1, i32 -1, i32 65536>, <4 x float> <float 300.0, float -300.0, float 0x7FF8000000000000, float 0x4016CCCCC0000000>, <2 x double> <double -5.0, double 1.0e6>)
  %i1 = shl i32 %i, 15
  %a0 = or i32 %p, %i1
  %a = or i32 %a0, %q1
  %b = or i32 %a, %m1
  %c = or i32 %b, %s1
  %d = or i32 %c, %r1
  %e = or i32 %d, %f1
  ret i32 %e
}

declare i8 @llvm.vector.reduce.umax.v16i8(<16 x i8>)
declare i8 @llvm.vector.reduce.smin.v16i8(<16 x i8>)
declare i32 @llvm.vector.reduce.add.v8i32(<8 x i32>)
declare i32 @llvm.vector.reduce.mul.v8i32(<8 x i32>)
declare float @llvm.vector.reduce.fadd.v4f32(float, <4 x float>)
declare i1 @llvm.vector.reduce.or.v8i1(<8 x i1>)
declare i1 @llvm.vector.reduce.and.v8i1(<8 x i1>)
declare <4 x i32> @llvm.fptosi.sat.v4i32.v4f32(<4 x float>)
declare <4 x i32> @llvm.bswap.v4i32(<4 x i32>)
declare <4 x i32> @llvm.vector.reverse.v4i32(<4 x i32>)
declare <2 x i32> @llvm.vector.extract.v2i32.v4i32(<4 x i32>, i64)
declare <8 x i32> @llvm.vector.insert.v8i32.v4i32(<8 x i32>, <4 x i32>, i64)
declare <4 x i8> @llvm.fptosi.sat.v4i8.v4f32(<4 x float>)
declare <2 x i16> @llvm.fptoui.sat.v2i16.v2f64(<2 x double>)
declare <4 x i32> @llvm.fshl.v4i32(<4 x i32>, <4 x i32>, <4 x i32>)
declare <4 x i32> @llvm.ctlz.v4i32(<4 x i32>, i1)
declare <2 x i64> @llvm.cttz.v2i64(<2 x i64>, i1)
declare i32 @llvm.vector.reduce.add.v4i32(<4 x i32>)
declare i32 @llvm.vector.reduce.umax.v3i32(<3 x i32>)
declare i32 @llvm.vector.reduce.add.v2i32(<2 x i32>)
declare float @llvm.vector.reduce.fmax.v4f32(<4 x float>)
declare float @llvm.vector.reduce.fmaximum.v4f32(<4 x float>)
