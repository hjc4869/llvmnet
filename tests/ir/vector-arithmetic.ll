target triple = "x86_64-pc-linux-gnu"

@input = global <4 x i32> <i32 2147483647, i32 -1, i32 16, i32 -32>, align 16
@output = global <4 x i32> zeroinitializer, align 16

define i32 @main() {
entry:
  %input = load <4 x i32>, ptr @input, align 16
  %added = add <4 x i32> %input, <i32 1, i32 2, i32 3, i32 4>
  %shifted = ashr <4 x i32> %added, <i32 1, i32 0, i32 2, i32 2>
  %mask = icmp slt <4 x i32> %shifted, zeroinitializer
  %selected = select <4 x i1> %mask, <4 x i32> %shifted, <4 x i32> %added
  store <4 x i32> %selected, ptr @output, align 16
  %lane0 = extractelement <4 x i32> %selected, i32 0
  %lane1 = extractelement <4 x i32> %selected, i32 1
  %lane2 = extractelement <4 x i32> %selected, i32 2
  %lane3 = extractelement <4 x i32> %selected, i32 3
  %check0 = icmp eq i32 %lane0, -1073741824
  %check1 = icmp eq i32 %lane1, 1
  %check2 = icmp eq i32 %lane2, 19
  %check3 = icmp eq i32 %lane3, -7
  %low = and i1 %check0, %check1
  %high = and i1 %check2, %check3
  %valid = and i1 %low, %high
  %result = select i1 %valid, i32 0, i32 1
  ret i32 %result
}