@_ZTIi = external constant ptr

declare i32 @__gxx_personality_v0(...)
declare ptr @__cxa_allocate_exception(i64)
declare void @__cxa_throw(ptr, ptr, ptr)
declare ptr @__cxa_begin_catch(ptr)
declare void @__cxa_end_catch()

define <2 x float> @produce(i1 %fail) {
entry:
  br i1 %fail, label %throw, label %success
success:
  ret <2 x float> <float 1.250000e+00, float 2.500000e+00>
throw:
  %exception = call ptr @__cxa_allocate_exception(i64 4)
  store i32 19, ptr %exception, align 4
  call void @__cxa_throw(ptr %exception, ptr @_ZTIi, ptr null)
  unreachable
}

define i32 @check(i1 %fail) personality ptr @__gxx_personality_v0 {
entry:
  %vector = invoke <2 x float> @produce(i1 %fail) to label %normal unwind label %caught
normal:
  %copy = phi <2 x float> [ %vector, %entry ]
  %first = extractelement <2 x float> %copy, i32 0
  %second = extractelement <2 x float> %copy, i32 1
  %first_ok = fcmp oeq float %first, 1.250000e+00
  %second_ok = fcmp oeq float %second, 2.500000e+00
  %values_ok = and i1 %first_ok, %second_ok
  %expected = xor i1 %fail, true
  %ok = and i1 %values_ok, %expected
  %result = select i1 %ok, i32 0, i32 1
  ret i32 %result
caught:
  %landing = landingpad { ptr, i32 } catch ptr null
  %handle = extractvalue { ptr, i32 } %landing, 0
  %object = call ptr @__cxa_begin_catch(ptr %handle)
  %value = load i32, ptr %object, align 4
  %value_ok = icmp eq i32 %value, 19
  call void @__cxa_end_catch()
  %caught_ok = and i1 %value_ok, %fail
  %caught_result = select i1 %caught_ok, i32 0, i32 2
  ret i32 %caught_result
}

define i32 @main() {
entry:
  %normal = call i32 @check(i1 false)
  %exception = call i32 @check(i1 true)
  %result = or i32 %normal, %exception
  ret i32 %result
}