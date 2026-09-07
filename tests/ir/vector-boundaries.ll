target triple = "x86_64-pc-linux-gnu"

@input = global <2 x i64> <i64 1234567890123, i64 -42>, align 16
@lane = global i64 1
@references = global <2 x i64> <i64 ptrtoint (ptr @input to i64), i64 ptrtoint (ptr @lane to i64)>, align 16

define <2 x i64> @replace(<2 x i64> %vector, i64 %lane) {
entry:
  %previous = extractelement <2 x i64> %vector, i64 %lane
  %incremented = add i64 %previous, 7
  %replaced = insertelement <2 x i64> %vector, i64 %incremented, i64 %lane
  %swapped = shufflevector <2 x i64> %replaced, <2 x i64> poison, <2 x i32> <i32 1, i32 0>
  ret <2 x i64> %swapped
}

define i32 @main() {
entry:
  %vector = load <2 x i64>, ptr @input, align 16
  %lane = load i64, ptr @lane
  %result = call <2 x i64> @replace(<2 x i64> %vector, i64 %lane)
  %first = extractelement <2 x i64> %result, i64 0
  %second = extractelement <2 x i64> %result, i64 1
  %original = extractelement <2 x i64> %vector, i64 1
  %check0 = icmp eq i64 %first, -35
  %check1 = icmp eq i64 %second, 1234567890123
  %check2 = icmp eq i64 %original, -42
  %references = load <2 x i64>, ptr @references, align 16
  %reference = extractelement <2 x i64> %references, i64 1
  %expected = ptrtoint ptr @lane to i64
  %check3 = icmp eq i64 %reference, %expected
  %pair = and i1 %check0, %check1
  %extra = and i1 %check2, %check3
  %valid = and i1 %pair, %extra
  %exit = select i1 %valid, i32 0, i32 1
  ret i32 %exit
}