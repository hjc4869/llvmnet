; musttail chains of 1,000,000 calls whose arguments and results are 16-byte values, which llvmnet passes as value
; types: <4 x float> through a function pointer, as Skia's skcms chains its stages, i128 and x86_fp80. A stack this
; deep holds them only as tail calls. main returns 0 when every chain computes what it should.
@next_vectors = global ptr @vectors

define <4 x float> @vectors(i64 %n, <4 x float> %a, <4 x float> %b) {
entry:
  %done = icmp eq i64 %n, 0
  br i1 %done, label %exit, label %next
next:
  %less = sub i64 %n, 1
  %b1 = fadd <4 x float> %a, <float 1.0, float 1.0, float 1.0, float 1.0>
  %f = load volatile ptr, ptr @next_vectors
  %r = musttail call <4 x float> %f(i64 %less, <4 x float> %b, <4 x float> %b1)
  ret <4 x float> %r
exit:
  ret <4 x float> %a
}

define i128 @wide(i64 %n, i128 %acc) {
entry:
  %done = icmp eq i64 %n, 0
  br i1 %done, label %exit, label %next
next:
  %less = sub i64 %n, 1
  %step = zext i64 %n to i128
  %shifted = shl i128 %step, 64
  %sum = add i128 %acc, %shifted
  %r = musttail call i128 @wide(i64 %less, i128 %sum)
  ret i128 %r
exit:
  ret i128 %acc
}

define x86_fp80 @extended(i64 %n, x86_fp80 %acc) {
entry:
  %done = icmp eq i64 %n, 0
  br i1 %done, label %exit, label %next
next:
  %less = sub i64 %n, 1
  %sum = fadd x86_fp80 %acc, 0xK3FFF8000000000000000
  %r = musttail call x86_fp80 @extended(i64 %less, x86_fp80 %sum)
  ret x86_fp80 %r
exit:
  ret x86_fp80 %acc
}

define i32 @main() {
entry:
  %v = call <4 x float> @vectors(i64 1000000, <4 x float> <float 1.0, float 2.0, float 3.0, float 4.0>, <4 x float> <float 5.0, float 6.0, float 7.0, float 8.0>)
  %lane0 = extractelement <4 x float> %v, i32 0
  %lane3 = extractelement <4 x float> %v, i32 3
  %v0 = fcmp une float %lane0, 500001.0
  %v3 = fcmp une float %lane3, 500004.0
  %w = call i128 @wide(i64 1000000, i128 7)
  %expected = shl i128 500000500000, 64
  %expected7 = add i128 %expected, 7
  %w0 = icmp ne i128 %w, %expected7
  %x = call x86_fp80 @extended(i64 1000000, x86_fp80 0xK00000000000000000000)
  %x0 = fcmp une x86_fp80 %x, 0xK4012F424000000000000
  %f1 = or i1 %v0, %v3
  %f2 = or i1 %f1, %w0
  %f3 = or i1 %f2, %x0
  %status = zext i1 %f3 to i32
  ret i32 %status
}
