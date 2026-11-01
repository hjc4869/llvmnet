# CMake platform module for llvmnet, which the toolchain file selects (cmake/llvmnet.cmake: CMAKE_SYSTEM_NAME LlvmNet).
#
# Programs run on a POSIX system, musl and the host layer in portable mode and the host's C library in system mode, so
# UNIX is set and project code takes its Unix paths. None of the host's directories are searched, unlike CMake's Linux
# platform: host libraries are native code, which a bitcode link cannot use, so find_library and find_package see only
# the prefixes the build names (CMAKE_PREFIX_PATH, CMAKE_FIND_ROOT_PATH, <Package>_ROOT).
#
# A CIL library cannot contain the C library (docs/user/linking.md), so a SHARED or MODULE target is built as an archive
# of bitcode under its Linux name (lib<name>.so, with the version links): its objects and the members of the static
# libraries it links, which a native shared library would contain (llvmnet-rules.cmake). A program links it as it links
# any archive. Its shared dependencies are named again where it is linked (CMAKE_LINK_DEPENDENT_LIBRARY_FILES), as a
# native link resolves them through the library.
set(UNIX 1)
set_property(GLOBAL PROPERTY TARGET_SUPPORTS_SHARED_LIBS TRUE)
set(CMAKE_LINK_DEPENDENT_LIBRARY_FILES TRUE)
foreach(_llvmnet_language C CXX)
    # With an soname flag CMake names a versioned library as on Linux (libz.so.1.3.2, libz.so.1, libz.so); the archive
    # rule has no use for the flag itself.
    set(CMAKE_SHARED_LIBRARY_SONAME_${_llvmnet_language}_FLAG "-Wl,-soname,")
    set(CMAKE_${_llvmnet_language}_CREATE_SHARED_LIBRARY
        "\"${CMAKE_COMMAND}\" -DLLVMNET_ARCHIVE=<TARGET> -DLLVMNET_AR=<CMAKE_AR> -DLLVMNET_RANLIB=<CMAKE_RANLIB> -P \"${CMAKE_CURRENT_LIST_DIR}/../llvmnet-rules.cmake\" -- <OBJECTS> <LINK_FLAGS> <LINK_LIBRARIES>")
    set(CMAKE_${_llvmnet_language}_CREATE_SHARED_MODULE "${CMAKE_${_llvmnet_language}_CREATE_SHARED_LIBRARY}")
endforeach()
