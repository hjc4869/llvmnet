; Values of the two fragment widths meeting (SV-41): with --vector-width=256 a vector of at most 128 bits is a Vector128
; and a wider one a Vector256, so concatenations, halves, interleaves, casts between lane widths, masks of another
; width (through a phi, from bits, shuffled, and of i16 lanes selecting i8 lanes), partial memory accesses, variable
; lane indices, reductions and the subvector intrinsics convert between them. Each case writes its result into @out;
; main returns 0 when the FNV-1a hash of @out is the one native code computes. ir.sh runs it at 128 bits, and
; vector-lowering.sh typed at 256 bits and as NativeAOT at x86-64-v3.
target triple = "x86_64-pc-linux-gnu"

@in = global [256 x i8] zeroinitializer, align 32
@out = global [1024 x i8] zeroinitializer, align 32

declare i8 @llvm.vector.reduce.add.v32i8(<32 x i8>)
declare i16 @llvm.vector.reduce.umax.v16i16(<16 x i16>)
declare i1 @llvm.vector.reduce.and.v8i1(<8 x i1>)
declare i32 @llvm.vector.reduce.smin.v8i32(<8 x i32>)
declare <4 x i32> @llvm.vector.extract.v4i32.v8i32(<8 x i32>, i64)
declare <8 x i32> @llvm.vector.insert.v8i32.v4i32(<8 x i32>, <4 x i32>, i64)
declare <16 x i16> @llvm.vector.reverse.v16i16(<16 x i16>)

define void @fill() {
entry:
  br label %loop
loop:
  %i = phi i32 [ 0, %entry ], [ %next, %loop ]
  %scaled = mul i32 %i, 73
  %shifted = lshr i32 %i, 3
  %mixed = add i32 %scaled, 41
  %value = xor i32 %mixed, %shifted
  %byte = trunc i32 %value to i8
  %slot = getelementptr [256 x i8], ptr @in, i32 0, i32 %i
  store i8 %byte, ptr %slot
  %next = add i32 %i, 1
  %done = icmp eq i32 %next, 256
  br i1 %done, label %exit, label %loop
exit:
  ret void
}

define void @shuffles(ptr %in, ptr %out) noinline {
entry:
  %a = load <16 x i8>, ptr %in, align 1
  %pb = getelementptr i8, ptr %in, i64 16
  %b = load <16 x i8>, ptr %pb, align 1
  %concat = shufflevector <16 x i8> %a, <16 x i8> %b, <32 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7, i32 8, i32 9, i32 10, i32 11, i32 12, i32 13, i32 14, i32 15, i32 16, i32 17, i32 18, i32 19, i32 20, i32 21, i32 22, i32 23, i32 24, i32 25, i32 26, i32 27, i32 28, i32 29, i32 30, i32 31>
  store <32 x i8> %concat, ptr %out, align 1
  %pw = getelementptr i8, ptr %in, i64 32
  %wide = load <32 x i8>, ptr %pw, align 1
  %upper = shufflevector <32 x i8> %wide, <32 x i8> poison, <16 x i32> <i32 16, i32 17, i32 18, i32 19, i32 20, i32 21, i32 22, i32 23, i32 24, i32 25, i32 26, i32 27, i32 28, i32 29, i32 30, i32 31>
  %o1 = getelementptr i8, ptr %out, i64 32
  store <16 x i8> %upper, ptr %o1, align 1
  %lower = shufflevector <32 x i8> %wide, <32 x i8> poison, <16 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7, i32 8, i32 9, i32 10, i32 11, i32 12, i32 13, i32 14, i32 15>
  %o2 = getelementptr i8, ptr %out, i64 48
  store <16 x i8> %lower, ptr %o2, align 1
  %pc = getelementptr i8, ptr %in, i64 64
  %c = load <16 x i8>, ptr %pc, align 1
  %pd = getelementptr i8, ptr %in, i64 80
  %d = load <16 x i8>, ptr %pd, align 1
  %bytes = shufflevector <16 x i8> %c, <16 x i8> %d, <32 x i32> <i32 0, i32 16, i32 1, i32 17, i32 2, i32 18, i32 3, i32 19, i32 4, i32 20, i32 5, i32 21, i32 6, i32 22, i32 7, i32 23, i32 8, i32 24, i32 9, i32 25, i32 10, i32 26, i32 11, i32 27, i32 12, i32 28, i32 13, i32 29, i32 14, i32 30, i32 15, i32 31>
  %o3 = getelementptr i8, ptr %out, i64 64
  store <32 x i8> %bytes, ptr %o3, align 1
  %pe = getelementptr i8, ptr %in, i64 96
  %e = load <4 x i32>, ptr %pe, align 1
  %pf = getelementptr i8, ptr %in, i64 112
  %f = load <4 x i32>, ptr %pf, align 1
  %words = shufflevector <4 x i32> %e, <4 x i32> %f, <8 x i32> <i32 0, i32 4, i32 1, i32 5, i32 2, i32 6, i32 3, i32 7>
  %o4 = getelementptr i8, ptr %out, i64 96
  store <8 x i32> %words, ptr %o4, align 1
  %pg = getelementptr i8, ptr %in, i64 128
  %g = load <8 x i32>, ptr %pg, align 1
  %even = shufflevector <8 x i32> %g, <8 x i32> poison, <4 x i32> <i32 0, i32 2, i32 4, i32 6>
  %o5 = getelementptr i8, ptr %out, i64 128
  store <4 x i32> %even, ptr %o5, align 1
  %odd = shufflevector <8 x i32> %g, <8 x i32> poison, <4 x i32> <i32 1, i32 3, i32 5, i32 7>
  %o6 = getelementptr i8, ptr %out, i64 144
  store <4 x i32> %odd, ptr %o6, align 1
  %ph = getelementptr i8, ptr %in, i64 144
  %h = load <8 x i16>, ptr %ph, align 1
  %pi = getelementptr i8, ptr %in, i64 160
  %i = load <8 x i16>, ptr %pi, align 1
  %mixed = shufflevector <8 x i16> %h, <8 x i16> %i, <16 x i32> <i32 0, i32 8, i32 2, i32 10, i32 4, i32 12, i32 6, i32 14, i32 1, i32 9, i32 3, i32 11, i32 5, i32 13, i32 7, i32 15>
  %o7 = getelementptr i8, ptr %out, i64 901
  store <16 x i16> %mixed, ptr %o7, align 1
  %pj = getelementptr i8, ptr %in, i64 176
  %j = load <16 x i16>, ptr %pj, align 1
  %across = shufflevector <16 x i16> %j, <16 x i16> poison, <8 x i32> <i32 15, i32 0, i32 14, i32 1, i32 13, i32 2, i32 12, i32 3>
  %o8 = getelementptr i8, ptr %out, i64 933
  store <8 x i16> %across, ptr %o8, align 1
  ret void
}

define void @casts(ptr %in, ptr %out) noinline {
entry:
  %a = load <16 x i8>, ptr %in, align 1
  %za = zext <16 x i8> %a to <16 x i16>
  %o1 = getelementptr i8, ptr %out, i64 160
  store <16 x i16> %za, ptr %o1, align 1
  %pb = getelementptr i8, ptr %in, i64 16
  %b = load <8 x i8>, ptr %pb, align 1
  %sb = sext <8 x i8> %b to <8 x i32>
  %o2 = getelementptr i8, ptr %out, i64 192
  store <8 x i32> %sb, ptr %o2, align 1
  %pc = getelementptr i8, ptr %in, i64 24
  %c = load <4 x i16>, ptr %pc, align 1
  %zc = zext <4 x i16> %c to <4 x i64>
  %o3 = getelementptr i8, ptr %out, i64 224
  store <4 x i64> %zc, ptr %o3, align 1
  %pd = getelementptr i8, ptr %in, i64 32
  %d = load <16 x i8>, ptr %pd, align 1
  %sd = sext <16 x i8> %d to <16 x i32>
  %o4 = getelementptr i8, ptr %out, i64 256
  store <16 x i32> %sd, ptr %o4, align 1
  %pe = getelementptr i8, ptr %in, i64 64
  %e = load <8 x i32>, ptr %pe, align 1
  %te = trunc <8 x i32> %e to <8 x i8>
  %o5 = getelementptr i8, ptr %out, i64 320
  store <8 x i8> %te, ptr %o5, align 1
  %pf = getelementptr i8, ptr %in, i64 96
  %f = load <16 x i16>, ptr %pf, align 1
  %tf = trunc <16 x i16> %f to <16 x i8>
  %o6 = getelementptr i8, ptr %out, i64 328
  store <16 x i8> %tf, ptr %o6, align 1
  %pg = getelementptr i8, ptr %in, i64 128
  %g = load <4 x i64>, ptr %pg, align 1
  %tg = trunc <4 x i64> %g to <4 x i16>
  %o7 = getelementptr i8, ptr %out, i64 344
  store <4 x i16> %tg, ptr %o7, align 1
  ret void
}

define void @floats(ptr %in, ptr %out) noinline {
entry:
  %a = load <4 x i32>, ptr %in, align 1
  %fa = sitofp <4 x i32> %a to <4 x float>
  %da = fpext <4 x float> %fa to <4 x double>
  %o1 = getelementptr i8, ptr %out, i64 352
  store <4 x double> %da, ptr %o1, align 1
  %pb = getelementptr i8, ptr %in, i64 16
  %b = load <4 x i32>, ptr %pb, align 1
  %db = sitofp <4 x i32> %b to <4 x double>
  %fb = fptrunc <4 x double> %db to <4 x float>
  %o2 = getelementptr i8, ptr %out, i64 384
  store <4 x float> %fb, ptr %o2, align 1
  %pc = getelementptr i8, ptr %in, i64 32
  %c = load <4 x i64>, ptr %pc, align 1
  %fc = sitofp <4 x i64> %c to <4 x float>
  %o3 = getelementptr i8, ptr %out, i64 400
  store <4 x float> %fc, ptr %o3, align 1
  %pd = getelementptr i8, ptr %in, i64 64
  %d = load <4 x i64>, ptr %pd, align 1
  %fd = uitofp <4 x i64> %d to <4 x float>
  %o4 = getelementptr i8, ptr %out, i64 416
  store <4 x float> %fd, ptr %o4, align 1
  %pe = getelementptr i8, ptr %in, i64 96
  %e = load <8 x i64>, ptr %pe, align 1
  %fe = sitofp <8 x i64> %e to <8 x float>
  %o5 = getelementptr i8, ptr %out, i64 432
  store <8 x float> %fe, ptr %o5, align 1
  %pf = getelementptr i8, ptr %in, i64 48
  %f = load <4 x i32>, ptr %pf, align 1
  %df = sitofp <4 x i32> %f to <4 x double>
  %half = fmul <4 x double> %df, <double 0.5, double 0.25, double -0.5, double 0.125>
  %if = fptosi <4 x double> %half to <4 x i32>
  %o6 = getelementptr i8, ptr %out, i64 464
  store <4 x i32> %if, ptr %o6, align 1
  %pg = getelementptr i8, ptr %in, i64 80
  %g = load <4 x i32>, ptr %pg, align 1
  %sg = ashr <4 x i32> %g, <i32 8, i32 8, i32 8, i32 8>
  %fg = sitofp <4 x i32> %sg to <4 x float>
  %lg = fptosi <4 x float> %fg to <4 x i64>
  %o7 = getelementptr i8, ptr %out, i64 480
  store <4 x i64> %lg, ptr %o7, align 1
  ret void
}

define void @masks(ptr %in, ptr %out, i32 %rounds) noinline {
entry:
  %a = load <4 x i32>, ptr %in, align 1
  %pb = getelementptr i8, ptr %in, i64 16
  %b = load <4 x i32>, ptr %pb, align 1
  %less = icmp slt <4 x i32> %a, %b
  br label %loop
loop:
  %n = phi i32 [ 0, %entry ], [ %next, %loop ]
  %m = phi <4 x i1> [ %less, %entry ], [ %flipped, %loop ]
  %flipped = xor <4 x i1> %m, <i1 true, i1 false, i1 true, i1 false>
  %next = add i32 %n, 1
  %done = icmp eq i32 %next, %rounds
  br i1 %done, label %exit, label %loop
exit:
  %chosen = select <4 x i1> %flipped, <4 x i32> %a, <4 x i32> %b
  %o1 = getelementptr i8, ptr %out, i64 512
  store <4 x i32> %chosen, ptr %o1, align 1
  %px = getelementptr i8, ptr %in, i64 32
  %x = load <8 x i16>, ptr %px, align 1
  %py = getelementptr i8, ptr %in, i64 48
  %y = load <8 x i16>, ptr %py, align 1
  %greater = icmp sgt <8 x i16> %x, %y
  %nonzero = icmp ne <8 x i16> %x, zeroinitializer
  %both = and <8 x i1> %greater, %nonzero
  %pp = getelementptr i8, ptr %in, i64 64
  %p = load <8 x i32>, ptr %pp, align 1
  %pq = getelementptr i8, ptr %in, i64 96
  %q = load <8 x i32>, ptr %pq, align 1
  %wide = select <8 x i1> %both, <8 x i32> %p, <8 x i32> %q
  %o2 = getelementptr i8, ptr %out, i64 528
  store <8 x i32> %wide, ptr %o2, align 1
  %c = load <16 x i8>, ptr %in, align 1
  %pd = getelementptr i8, ptr %in, i64 16
  %d = load <16 x i8>, ptr %pd, align 1
  %above = icmp ugt <16 x i8> %c, %d
  %bits = bitcast <16 x i1> %above to i16
  %o3 = getelementptr i8, ptr %out, i64 560
  store i16 %bits, ptr %o3, align 1
  %pk = getelementptr i8, ptr %in, i64 40
  %k = load i8, ptr %pk, align 1
  %kmask = bitcast i8 %k to <8 x i1>
  %fp = sitofp <8 x i32> %p to <8 x float>
  %fq = sitofp <8 x i32> %q to <8 x float>
  %fsel = select <8 x i1> %kmask, <8 x float> %fp, <8 x float> %fq
  %o4 = getelementptr i8, ptr %out, i64 564
  store <8 x float> %fsel, ptr %o4, align 1
  %pr = getelementptr i8, ptr %in, i64 128
  %r = load <8 x i32>, ptr %pr, align 1
  %negative = icmp slt <8 x i32> %r, zeroinitializer
  %zn = zext <8 x i1> %negative to <8 x i32>
  %o5 = getelementptr i8, ptr %out, i64 596
  store <8 x i32> %zn, ptr %o5, align 1
  %sn = sext <8 x i1> %negative to <8 x i16>
  %o6 = getelementptr i8, ptr %out, i64 628
  store <8 x i16> %sn, ptr %o6, align 1
  %pt = getelementptr i8, ptr %in, i64 64
  %t = load <8 x i16>, ptr %pt, align 1
  %low = trunc <8 x i16> %t to <8 x i1>
  %s1 = load <8 x i8>, ptr %in, align 1
  %ps2 = getelementptr i8, ptr %in, i64 8
  %s2 = load <8 x i8>, ptr %ps2, align 1
  %bsel = select <8 x i1> %low, <8 x i8> %s1, <8 x i8> %s2
  %o7 = getelementptr i8, ptr %out, i64 644
  store <8 x i8> %bsel, ptr %o7, align 1
  %a3 = and <4 x i32> %a, <i32 3, i32 3, i32 3, i32 3>
  %c1 = icmp eq <4 x i32> %a3, zeroinitializer
  %b3 = and <4 x i32> %b, <i32 3, i32 3, i32 3, i32 3>
  %c2 = icmp eq <4 x i32> %b3, zeroinitializer
  %joined = shufflevector <4 x i1> %c1, <4 x i1> %c2, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %jsel = select <8 x i1> %joined, <8 x float> %fq, <8 x float> %fp
  %o8 = getelementptr i8, ptr %out, i64 652
  store <8 x float> %jsel, ptr %o8, align 1
  %u = load <16 x i16>, ptr %in, align 1
  %pv = getelementptr i8, ptr %in, i64 32
  %v = load <16 x i16>, ptr %pv, align 1
  %halves = icmp sgt <16 x i16> %u, %v
  %pe = getelementptr i8, ptr %in, i64 64
  %e = load <16 x i8>, ptr %pe, align 1
  %pf = getelementptr i8, ptr %in, i64 80
  %f = load <16 x i8>, ptr %pf, align 1
  %narrow = select <16 x i1> %halves, <16 x i8> %e, <16 x i8> %f
  %o9 = getelementptr i8, ptr %out, i64 684
  store <16 x i8> %narrow, ptr %o9, align 1
  ret void
}

define void @memory(ptr %in, ptr %out) noinline {
entry:
  %a = load <24 x i8>, ptr %in, align 1
  %pb = getelementptr i8, ptr %in, i64 24
  %b = load <24 x i8>, ptr %pb, align 1
  %sum = add <24 x i8> %a, %b
  %o1 = getelementptr i8, ptr %out, i64 700
  store <24 x i8> %sum, ptr %o1, align 1
  %pc = getelementptr i8, ptr %in, i64 48
  %c = load <3 x i64>, ptr %pc, align 1
  %cc = add <3 x i64> %c, <i64 1, i64 -1, i64 3>
  %o2 = getelementptr i8, ptr %out, i64 724
  store <3 x i64> %cc, ptr %o2, align 1
  %d = load <8 x i32>, ptr %in, align 1
  %pi = getelementptr i8, ptr %in, i64 200
  %index = load i32, ptr %pi, align 1
  %lane = and i32 %index, 7
  %pv = getelementptr i8, ptr %in, i64 204
  %value = load i32, ptr %pv, align 1
  %inserted = insertelement <8 x i32> %d, i32 %value, i32 %lane
  %o3 = getelementptr i8, ptr %out, i64 748
  store <8 x i32> %inserted, ptr %o3, align 1
  %pe = getelementptr i8, ptr %in, i64 16
  %e = load <16 x i8>, ptr %pe, align 1
  %pj = getelementptr i8, ptr %in, i64 208
  %jndex = load i32, ptr %pj, align 1
  %jane = and i32 %jndex, 15
  %extracted = extractelement <16 x i8> %e, i32 %jane
  %o4 = getelementptr i8, ptr %out, i64 780
  store i8 %extracted, ptr %o4, align 1
  %f = load <32 x i8>, ptr %in, align 1
  %fl = bitcast <32 x i8> %f to <4 x i64>
  %fa = add <4 x i64> %fl, <i64 1, i64 1, i64 1, i64 1>
  %o5 = getelementptr i8, ptr %out, i64 869
  store <4 x i64> %fa, ptr %o5, align 1
  ret void
}

define void @reductions(ptr %in, ptr %out) noinline {
entry:
  %a = load <32 x i8>, ptr %in, align 1
  %sa = call i8 @llvm.vector.reduce.add.v32i8(<32 x i8> %a)
  %o1 = getelementptr i8, ptr %out, i64 781
  store i8 %sa, ptr %o1, align 1
  %pb = getelementptr i8, ptr %in, i64 32
  %b = load <16 x i16>, ptr %pb, align 1
  %mb = call i16 @llvm.vector.reduce.umax.v16i16(<16 x i16> %b)
  %o2 = getelementptr i8, ptr %out, i64 782
  store i16 %mb, ptr %o2, align 1
  %pc = getelementptr i8, ptr %in, i64 64
  %c = load <8 x i32>, ptr %pc, align 1
  %nc = icmp ne <8 x i32> %c, zeroinitializer
  %all = call i1 @llvm.vector.reduce.and.v8i1(<8 x i1> %nc)
  %za = zext i1 %all to i8
  %o3 = getelementptr i8, ptr %out, i64 784
  store i8 %za, ptr %o3, align 1
  %pd = getelementptr i8, ptr %in, i64 96
  %d = load <8 x i32>, ptr %pd, align 1
  %md = call i32 @llvm.vector.reduce.smin.v8i32(<8 x i32> %d)
  %o4 = getelementptr i8, ptr %out, i64 785
  store i32 %md, ptr %o4, align 1
  %upper = call <4 x i32> @llvm.vector.extract.v4i32.v8i32(<8 x i32> %d, i64 4)
  %o5 = getelementptr i8, ptr %out, i64 789
  store <4 x i32> %upper, ptr %o5, align 1
  %pe = getelementptr i8, ptr %in, i64 136
  %e = load <4 x i32>, ptr %pe, align 1
  %put = call <8 x i32> @llvm.vector.insert.v8i32.v4i32(<8 x i32> %c, <4 x i32> %e, i64 0)
  %o6 = getelementptr i8, ptr %out, i64 805
  store <8 x i32> %put, ptr %o6, align 1
  %reversed = call <16 x i16> @llvm.vector.reverse.v16i16(<16 x i16> %b)
  %o7 = getelementptr i8, ptr %out, i64 837
  store <16 x i16> %reversed, ptr %o7, align 1
  ret void
}

define i64 @digest() {
entry:
  call void @fill()
  call void @shuffles(ptr @in, ptr @out)
  call void @casts(ptr @in, ptr @out)
  call void @floats(ptr @in, ptr @out)
  call void @masks(ptr @in, ptr @out, i32 3)
  call void @memory(ptr @in, ptr @out)
  call void @reductions(ptr @in, ptr @out)
  br label %loop
loop:
  %i = phi i64 [ 0, %entry ], [ %next, %loop ]
  %hash = phi i64 [ -3750763034362895579, %entry ], [ %product, %loop ]
  %slot = getelementptr [1024 x i8], ptr @out, i64 0, i64 %i
  %byte = load i8, ptr %slot
  %wide = zext i8 %byte to i64
  %mixed = xor i64 %hash, %wide
  %product = mul i64 %mixed, 1099511628211
  %next = add i64 %i, 1
  %done = icmp eq i64 %next, 1024
  br i1 %done, label %exit, label %loop
exit:
  ret i64 %product
}

define i32 @main() {
entry:
  %hash = call i64 @digest()
  %wrong = icmp ne i64 %hash, -7103242195327863070
  %result = zext i1 %wrong to i32
  ret i32 %result
}
