target triple = "x86_64-pc-linux-gnu"

@ones = global i80 -1
@one = global i80 1
@zero = global i128 0
declare i80 @llvm.ctpop.i80(i80)
declare i80 @llvm.ctlz.i80(i80, i1)
declare i80 @llvm.cttz.i80(i80, i1)
declare i128 @llvm.ctpop.i128(i128)
declare i128 @llvm.ctlz.i128(i128, i1)
declare i128 @llvm.cttz.i128(i128, i1)

define i32 @main() {
entry:
  %ones = load i80, ptr @ones
  %population = call i80 @llvm.ctpop.i80(i80 %ones)
  %check0 = icmp eq i80 %population, 80
  %one = load i80, ptr @one
  %high = shl i80 %one, 79
  %leading = call i80 @llvm.ctlz.i80(i80 %one, i1 false)
  %trailing = call i80 @llvm.cttz.i80(i80 %high, i1 false)
  %check1 = icmp eq i80 %leading, 79
  %check2 = icmp eq i80 %trailing, 79
  %empty80 = xor i80 %ones, %ones
  %leadingEmpty80 = call i80 @llvm.ctlz.i80(i80 %empty80, i1 false)
  %trailingEmpty80 = call i80 @llvm.cttz.i80(i80 %empty80, i1 false)
  %check3 = icmp eq i80 %leadingEmpty80, 80
  %check4 = icmp eq i80 %trailingEmpty80, 80
  %zero = load i128, ptr @zero
  %leadingEmpty128 = call i128 @llvm.ctlz.i128(i128 %zero, i1 false)
  %trailingEmpty128 = call i128 @llvm.cttz.i128(i128 %zero, i1 false)
  %check5 = icmp eq i128 %leadingEmpty128, 128
  %check6 = icmp eq i128 %trailingEmpty128, 128
  %ones128 = xor i128 %zero, -1
  %population128 = call i128 @llvm.ctpop.i128(i128 %ones128)
  %check7 = icmp eq i128 %population128, 128
  %pair0 = and i1 %check0, %check1
  %pair1 = and i1 %check2, %check3
  %pair2 = and i1 %check4, %check5
  %pair3 = and i1 %check6, %check7
  %half0 = and i1 %pair0, %pair1
  %half1 = and i1 %pair2, %pair3
  %valid = and i1 %half0, %half1
  %exit = select i1 %valid, i32 0, i32 1
  ret i32 %exit
}