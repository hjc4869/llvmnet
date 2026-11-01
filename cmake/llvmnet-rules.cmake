# Build steps of the llvmnet CMake platform (Platform/LlvmNet.cmake and llvmnet.cmake), run with cmake -P:
#
#   cmake -DLLVMNET_ARCHIVE=<library> -DLLVMNET_AR=<ar> -DLLVMNET_RANLIB=<ranlib> -P llvmnet-rules.cmake -- <inputs>
#     Archives a SHARED or MODULE target from the inputs of its link rule: its objects, then every member of the static
#     libraries it links, named by path or as -l<name> found as lib<name>.a in the -L directories. A native shared
#     library contains the code of those static libraries, and CMake does not name them again where the library is
#     linked. Shared libraries (linked again by CMake where the library is used), the C runtime's libraries, which need
#     no archive, and other options are left out.
#
#   cmake -DLLVMNET_LAUNCHER=<program> -P llvmnet-rules.cmake
#     Copies the INFO:... strings of a try_compile program's managed image into its launcher, after the command it runs.
#     check_type_size and CMake's ABI detection read such strings out of the file that try_compile produces.
if(DEFINED LLVMNET_LAUNCHER)
    if(EXISTS "${LLVMNET_LAUNCHER}.llvmnet/program.dll")
        file(STRINGS "${LLVMNET_LAUNCHER}.llvmnet/program.dll" _llvmnet_strings REGEX "INFO:")
        set(_llvmnet_text "")
        foreach(_llvmnet_string IN LISTS _llvmnet_strings)
            string(APPEND _llvmnet_text "\n# ${_llvmnet_string}")
        endforeach()
        file(APPEND "${LLVMNET_LAUNCHER}" "${_llvmnet_text}\n")
    endif()
    return()
endif()

set(_llvmnet_objects "")
set(_llvmnet_archives "")
set(_llvmnet_names "")
set(_llvmnet_directories "")
set(_llvmnet_inputs FALSE)
math(EXPR _llvmnet_last "${CMAKE_ARGC} - 1")
foreach(_llvmnet_index RANGE ${_llvmnet_last})
    set(_llvmnet_argument "${CMAKE_ARGV${_llvmnet_index}}")
    if(NOT _llvmnet_inputs)
        if(_llvmnet_argument STREQUAL "--")
            set(_llvmnet_inputs TRUE)
        endif()
    elseif(_llvmnet_argument MATCHES "^-L(.+)$")
        list(APPEND _llvmnet_directories "${CMAKE_MATCH_1}")
    elseif(_llvmnet_argument MATCHES "^-l(.+)$")
        list(APPEND _llvmnet_names "${CMAKE_MATCH_1}")
    elseif(_llvmnet_argument MATCHES "^-" OR _llvmnet_argument MATCHES "\\.so(\\.[0-9]+)*$")
        # Options, and shared libraries.
    elseif(_llvmnet_argument MATCHES "\\.a$")
        list(APPEND _llvmnet_archives "${_llvmnet_argument}")
    elseif(NOT _llvmnet_argument STREQUAL "")
        list(APPEND _llvmnet_objects "${_llvmnet_argument}")
    endif()
endforeach()
foreach(_llvmnet_name IN LISTS _llvmnet_names)
    foreach(_llvmnet_directory IN LISTS _llvmnet_directories)
        if(EXISTS "${_llvmnet_directory}/lib${_llvmnet_name}.a")
            list(APPEND _llvmnet_archives "${_llvmnet_directory}/lib${_llvmnet_name}.a")
            break()
        endif()
    endforeach()
endforeach()
file(REMOVE "${LLVMNET_ARCHIVE}")
execute_process(COMMAND "${LLVMNET_AR}" qc "${LLVMNET_ARCHIVE}" ${_llvmnet_objects} COMMAND_ERROR_IS_FATAL ANY)
if(_llvmnet_archives)
    list(REMOVE_DUPLICATES _llvmnet_archives)
    # llvm-ar's L modifier appends the members of an archive instead of the archive itself.
    execute_process(COMMAND "${LLVMNET_AR}" qcL "${LLVMNET_ARCHIVE}" ${_llvmnet_archives} COMMAND_ERROR_IS_FATAL ANY)
endif()
execute_process(COMMAND "${LLVMNET_RANLIB}" "${LLVMNET_ARCHIVE}" COMMAND_ERROR_IS_FATAL ANY)
