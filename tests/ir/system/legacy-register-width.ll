target triple = "x86_64-pc-linux-gnu"

@number = global i32 42

define i64 @wide_argument(i64 %value) noinline {
entry:
  ret i64 %value
}

define i64 @last_register(i64 %first, i64 %second, i64 %third, i64 %fourth, i64 %fifth, i64 %last) noinline {
entry:
  ret i64 %last
}

define i32 @hidden_length(ptr %first, ptr %second, i32 %length, i32 %other_length) noinline {
entry:
  %sum = add i32 %length, %other_length
  ret i32 %sum
}

define i32 @stack_lengths(ptr %first, ptr %second, ptr %third, ptr %fourth, ptr %fifth, i32 %length, i32 %other_length, i32 %last_length) noinline {
entry:
  %first_sum = add i32 %length, %other_length
  %sum = add i32 %first_sum, %last_length
  ret i32 %sum
}

define i32 @pointer_argument(ptr %address) noinline {
entry:
  %number = load i32, ptr %address
  ret i32 %number
}

define i8 @byte_boolean(i8 %value) noinline {
entry:
  ret i8 %value
}

define zeroext i1 @bit_boolean(i8 %value) noinline {
entry:
  %result = icmp ne i8 %value, 0
  ret i1 %result
}

define zeroext i1 @bit_argument(i1 %value) noinline {
entry:
  ret i1 %value
}

define i32 @mixed_boolean(ptr %first, ptr %second, ptr %third, float %scale, i1 %enabled) noinline {
entry:
  %scale_valid = fcmp oeq float %scale, 1.500000e+00
  %valid = and i1 %scale_valid, %enabled
  %result = select i1 %valid, i32 42, i32 0
  ret i32 %result
}

; Fortran passes character(1) by value as [1 x i8] where C takes char.
define i32 @byte_array_argument([1 x i8] %value) noinline {
entry:
  %byte = extractvalue [1 x i8] %value, 0
  %wide = zext i8 %byte to i32
  ret i32 %wide
}

define i32 @char_argument(i8 signext %value) noinline {
entry:
  %wide = zext i8 %value to i32
  ret i32 %wide
}

define [1 x i8] @byte_array_result() noinline {
entry:
  ret [1 x i8] [i8 65]
}

define i32 @main() {
entry:
  %positive = call i64 (i32) @wide_argument(i32 42)
  %negative = call i64 (i32) @wide_argument(i32 -1)
  %last = call i64 (i64, i64, i64, i64, i64, i32) @last_register(i64 0, i64 0, i64 0, i64 0, i64 0, i32 -2)
  %narrowed = call i32 (ptr, ptr, i64, i64) @hidden_length(ptr null, ptr null, i64 4294967338, i64 -1)
  %narrowed_valid = icmp eq i32 %narrowed, 41
  %stack_narrowed = call i32 (ptr, ptr, ptr, ptr, ptr, i64, i64, i64) @stack_lengths(ptr null, ptr null, ptr null, ptr null, ptr null, i64 4294967338, i64 -1, i64 2)
  %stack_valid = icmp eq i32 %stack_narrowed, 43
  %address = ptrtoint ptr @number to i64
  %pointer_bits = call i64 (ptr) @wide_argument(ptr @number)
  %pointer_valid = icmp eq i64 %pointer_bits, %address
  %pointer_value = call i32 (i64) @pointer_argument(i64 %address)
  %value_valid = icmp eq i32 %pointer_value, 42
  %returned_false = call i1 (i8) @byte_boolean(i8 0)
  %returned_true = call i1 (i8) @byte_boolean(i8 1)
  %returned_even = call i1 (i8) @byte_boolean(i8 2)
  %returned_odd = call i1 (i8) @byte_boolean(i8 3)
  %wide_false = call i8 (i8) @bit_boolean(i8 0)
  %wide_true = call i8 (i8) @bit_boolean(i8 7)
  %wide_false_valid = icmp eq i8 %wide_false, 0
  %wide_true_valid = icmp eq i8 %wide_true, 1
  %wide_booleans_valid = and i1 %wide_false_valid, %wide_true_valid
  %byte_argument = call i8 (i1) @byte_boolean(i1 true)
  %byte_argument_valid = icmp eq i8 %byte_argument, 1
  %bit_argument_even = call i1 (i8) @bit_argument(i8 2)
  %bit_argument_odd = call i1 (i8) @bit_argument(i8 3)
  %bit_argument_valid = icmp eq i1 %bit_argument_even, false
  %argument_bits_valid = and i1 %bit_argument_valid, %bit_argument_odd
  %arguments_valid = and i1 %argument_bits_valid, %byte_argument_valid
  %mixed = call i32 (ptr, ptr, ptr, float, i8) @mixed_boolean(ptr null, ptr null, ptr null, float 1.500000e+00, i8 1)
  %mixed_valid = icmp eq i32 %mixed, 42
  %false_valid = icmp eq i1 %returned_false, false
  %even_valid = icmp eq i1 %returned_even, false
  %truth_valid = and i1 %returned_true, %returned_odd
  %zero_valid = and i1 %false_valid, %even_valid
  %booleans_valid = and i1 %truth_valid, %zero_valid
  %positive_valid = icmp eq i64 %positive, 42
  %negative_valid = icmp eq i64 %negative, 4294967295
  %last_valid = icmp eq i64 %last, 4294967294
  %first_valid = and i1 %positive_valid, %negative_valid
  %second_valid = and i1 %first_valid, %last_valid
  %third_valid = and i1 %second_valid, %narrowed_valid
  %fourth_valid = and i1 %third_valid, %stack_valid
  %fifth_valid = and i1 %fourth_valid, %pointer_valid
  %sixth_valid = and i1 %fifth_valid, %value_valid
  %seventh_valid = and i1 %sixth_valid, %booleans_valid
  %eighth_valid = and i1 %seventh_valid, %wide_booleans_valid
  %ninth_valid = and i1 %eighth_valid, %arguments_valid
  %from_char = call i32 (i8) @byte_array_argument(i8 200)
  %from_char_valid = icmp eq i32 %from_char, 200
  %array = insertvalue [1 x i8] undef, i8 -3, 0
  %from_array = call i32 ([1 x i8]) @char_argument([1 x i8] %array)
  %from_array_valid = icmp eq i32 %from_array, 253
  %returned_byte = call i8 () @byte_array_result()
  %returned_byte_valid = icmp eq i8 %returned_byte, 65
  %bytes_valid = and i1 %from_char_valid, %from_array_valid
  %one_byte_valid = and i1 %bytes_valid, %returned_byte_valid
  %tenth_valid = and i1 %ninth_valid, %one_byte_valid
  %valid = and i1 %tenth_valid, %mixed_valid
  %result = select i1 %valid, i32 0, i32 1
  ret i32 %result
}