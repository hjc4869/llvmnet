// llvmnet port of libc++abi: catch matching for the managed exception bridge (plan item CX-03,
// docs/dev/cxx-eh-bridge.md). The CLR unwinds the stack, so libc++abi's personality routine never runs; the
// bridge asks this function whether a catch clause or exception specification accepts a thrown type, which
// is the question the personality routine asks private_typeinfo.cpp natively.
#include "private_typeinfo.h"

// Returns 1 when catch_type accepts an exception of thrown_type; *adjusted is the thrown object on entry and the
// object the handler receives on exit (adjusted to a base class, or the pointer value for pointer types).
extern "C" _LIBCXXABI_FUNC_VIS int __llvmnet_cxx_can_catch(const std::type_info* catch_type, const std::type_info* thrown_type,
                                                           void** adjusted) {
  void* object = *adjusted;
  auto catcher = static_cast<const __cxxabiv1::__shim_type_info*>(catch_type);
  if (!catcher->can_catch(static_cast<const __cxxabiv1::__shim_type_info*>(thrown_type), object))
    return 0;
  *adjusted = object;
  return 1;
}

// Every function with exception handling names this personality routine, which links this object into each
// C++ program that throws or catches. The backend implements the personality semantics, so it is never called.
extern "C" _LIBCXXABI_FUNC_VIS int __gxx_personality_v0() { __builtin_trap(); }
