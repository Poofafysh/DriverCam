@echo off
rem Builds UnrealLinkNative.dll (the game-side D3D11 helper) into Native\bin\ with the VS 2022 x64 compiler.
rem Called by UnrealLink.csproj before every build; can be run by hand. Exit code 0 = built.
rem VCVARS overrides where vcvars64.bat is (default: VS 2022 Community; vswhere is tried first).
setlocal
set "HERE=%~dp0"
set "OUT=%HERE%bin"
if not exist "%OUT%" mkdir "%OUT%"
where cl >nul 2>nul
if %errorlevel%==0 goto build
if defined VCVARS goto vcvars
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"
if not defined VCVARS set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
:vcvars
if not exist "%VCVARS%" (
  echo UnrealLinkNative: no C++ compiler found ^(install VS 2022 with "Desktop development with C++", or set VCVARS^)
  exit /b 3
)
call "%VCVARS%" >nul 2>nul
:build
pushd "%OUT%"
cl /nologo /LD /O2 /EHsc /W3 /MT "%HERE%UnrealLinkNative.cpp" /Fe:UnrealLinkNative.dll /link d3d11.lib dxgi.lib >build.log 2>&1
set ERR=%errorlevel%
popd
if not %ERR%==0 (
  type "%OUT%\build.log"
  echo UnrealLinkNative: build FAILED
  exit /b %ERR%
)
echo UnrealLinkNative: built %OUT%\UnrealLinkNative.dll
exit /b 0
