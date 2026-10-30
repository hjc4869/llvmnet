# Native bridge between the llvmnet compiler and LLVM. scripts/build-native.sh compiles these sources
# directly on Linux; this file describes the same build for hosts without llvm-config.
cmake_minimum_required(VERSION 3.20)
project(llvmnet_llvm LANGUAGES CXX)
# LLVM's package version file only accepts exact versions, so check the major version explicitly.
find_package(LLVM REQUIRED CONFIG)
if(NOT LLVM_VERSION_MAJOR EQUAL 22)
  message(FATAL_ERROR "llvmnet requires LLVM 22; found ${LLVM_PACKAGE_VERSION} in ${LLVM_DIR}")
endif()
add_library(llvmnet-llvm SHARED constants.cpp linker.cpp abi.cpp fp.cpp)
set_target_properties(llvmnet-llvm PROPERTIES CXX_STANDARD 17 CXX_STANDARD_REQUIRED ON POSITION_INDEPENDENT_CODE ON)
target_include_directories(llvmnet-llvm SYSTEM PRIVATE ${LLVM_INCLUDE_DIRS})
target_compile_definitions(llvmnet-llvm PRIVATE ${LLVM_DEFINITIONS})
llvm_map_components_to_libnames(llvmnet_llvm_libraries core bitreader bitwriter irreader linker object)
target_link_libraries(llvmnet-llvm PRIVATE ${llvmnet_llvm_libraries})
