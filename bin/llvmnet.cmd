@echo off
rem The llvmnet driver for Windows (bin/llvmnet is the Unix version). Copies of this file named llvmnet-clang.cmd,
rem llvmnet-clang++.cmd and llvmnet-flang.cmd are those drivers; llvmnet-ar.cmd, llvmnet-ranlib.cmd and llvmnet-nm.cmd
rem run the toolchain's LLVM tool of that name. It runs the compiler (lib\llvmnet\llvmnet.dll) on the .NET 10 runtime,
rem which it finds through LLVMNET_DOTNET, DOTNET_ROOT, PATH or the usual install location, and says what to install
rem when it is missing.
setlocal
for %%r in ("%~dp0..") do set "llvmnet_root=%%~fr"
if not defined LLVMNET_ROOT set "LLVMNET_ROOT=%llvmnet_root%"
set "llvmnet_name=%~n0"
set "llvmnet_tool="
if /i "%llvmnet_name%"=="llvmnet-ar" set "llvmnet_tool=llvm-ar-22"
if /i "%llvmnet_name%"=="llvmnet-ranlib" set "llvmnet_tool=llvm-ranlib-22"
if /i "%llvmnet_name%"=="llvmnet-nm" set "llvmnet_tool=llvm-nm-22"
rem A package precompiled for Windows has the tools as NativeAOT executables, which need no .NET runtime.
if defined llvmnet_tool if exist "%llvmnet_root%\libexec\llvm\bin\%llvmnet_tool%.exe" goto native_tool
if defined LLVMNET_DOTNET goto found
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "LLVMNET_DOTNET=%DOTNET_ROOT%\dotnet.exe"
if not defined LLVMNET_DOTNET for %%d in (dotnet.exe) do set "LLVMNET_DOTNET=%%~$PATH:d"
if not defined LLVMNET_DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "LLVMNET_DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined LLVMNET_DOTNET (
    echo llvmnet: the .NET 10 runtime is required, and no dotnet.exe was found on PATH, in DOTNET_ROOT or in "%ProgramFiles%\dotnet". 1>&2
    echo Install it from https://dotnet.microsoft.com/download/dotnet/10.0 ^(or with winget install Microsoft.DotNet.Runtime.10^), or set DOTNET_ROOT or LLVMNET_DOTNET to its location. 1>&2
    exit /b 9009
)
for %%d in ("%LLVMNET_DOTNET%") do set "llvmnet_frameworks=%%~dpdshared\Microsoft.NETCore.App"
if exist "%llvmnet_frameworks%\" if not exist "%llvmnet_frameworks%\10.*" (
    echo llvmnet: the .NET 10 runtime ^(Microsoft.NETCore.App 10.x^) is required; "%LLVMNET_DOTNET%" does not have it. 1>&2
    echo Install it from https://dotnet.microsoft.com/download/dotnet/10.0 ^(or with winget install Microsoft.DotNet.Runtime.10^), or set DOTNET_ROOT or LLVMNET_DOTNET to a dotnet that has it. 1>&2
    exit /b 150
)
:found
if defined llvmnet_tool goto tool
if /i "%llvmnet_name%"=="llvmnet-clang++" (set "LLVMNET_DRIVER=clang++") else if /i "%llvmnet_name%"=="llvmnet-flang" (set "LLVMNET_DRIVER=flang") else if not defined LLVMNET_DRIVER set "LLVMNET_DRIVER=clang"
set "llvmnet_compiler=%llvmnet_root%\lib\llvmnet\llvmnet.dll"
if not exist "%llvmnet_compiler%" set "llvmnet_compiler=%llvmnet_root%\src\LlvmNet.Compiler\bin\Release\net10.0\llvmnet.dll"
if not exist "%llvmnet_compiler%" (
    echo Build llvmnet first: dotnet build "%llvmnet_root%\src\LlvmNet.Compiler" -c Release 1>&2
    exit /b 1
)
"%LLVMNET_DOTNET%" "%llvmnet_compiler%" %*
exit /b %ERRORLEVEL%
:tool
rem The toolchain's llvmnet-built tool, through the launcher of its name.
set "llvmnet_launcher=%llvmnet_root%\libexec\llvm\bin\%llvmnet_tool%.cmd"
if not exist "%llvmnet_launcher%" (
    echo llvmnet: this toolchain has no %llvmnet_launcher% 1>&2
    exit /b 1
)
call "%llvmnet_launcher%" %*
exit /b %ERRORLEVEL%
:native_tool
"%llvmnet_root%\libexec\llvm\bin\%llvmnet_tool%.exe" %*
exit /b %ERRORLEVEL%
