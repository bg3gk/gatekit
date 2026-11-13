@echo off
rem Builds BG3ScriptExtender.dll ("Game Release", x64) from se-fork\bg3se.
rem Output: se-fork\bg3se\x64\Game Release\BG3ScriptExtender.dll
rem Load it with:  python stand\sefork.py install "se-fork\bg3se\x64\Game Release"
setlocal
rem Find the compiler, unless VCVARS already names a vcvars64.bat. vswhere lives under
rem "Program Files (x86)", and that path cannot go through a for /f in-clause: the ")" of
rem "(x86)" closes the clause and the command is cut short, whether it is written out or
rem reached through a variable. So vswhere writes to a file and we read the file.
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if defined VCVARS goto :gotvc
if not exist "%VSWHERE%" goto :gotvc
"%VSWHERE%" -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath > "%TEMP%\gatekit-vswhere.txt" 2>nul
set /p VSDIR=<"%TEMP%\gatekit-vswhere.txt"
del "%TEMP%\gatekit-vswhere.txt" >nul 2>&1
if defined VSDIR set "VCVARS=%VSDIR%\VC\Auxiliary\Build\vcvars64.bat"
:gotvc
if not defined VCVARS ( echo set VCVARS to the path of your vcvars64.bat & exit /b 1 )
if not exist "%VCVARS%" ( echo no vcvars64.bat at "%VCVARS%" - set VCVARS yourself & exit /b 1 )
rem Its own noise is swallowed: some versions of vcvars64.bat print a harmless complaint about
rem vswhere on the way, which otherwise reads like a failure of this script. A real failure
rem still comes back as a non-zero exit.
call "%VCVARS%" >nul 2>&1 || exit /b 1
rem Optional argument: the source tree to build (a git worktree beside bg3se); default bg3se.
set TREE=%~1
if "%TREE%"=="" set TREE=bg3se
cd /d "%~dp0%TREE%" || exit /b 1

if not exist BG3Extender\Extender\Shared\ExtenderProtocol.pb.cc (
  pushd BG3Extender
  ..\External\protoc\bin\protoc --cpp_out=. Extender\Shared\ExtenderProtocol.proto || exit /b 1
  ..\External\protoc\bin\protoc --cpp_out=. Lua\Debugger\LuaDebug.proto || exit /b 1
  ..\External\protoc\bin\protoc --cpp_out=. Osiris\Debugger\osidebug.proto || exit /b 1
  popd
)

rem The project's post-build step copies the DLL to its author's game folder; skipped here.
rem Second argument "fast": no whole-program optimisation, so a small change does not regenerate
rem the code of the whole DLL at link time. For iterating; build without it before a DLL is
rem handed to anyone. Switching modes in one tree rebuilds everything once.
set FAST=
if /i "%~2"=="fast" set FAST=/p:WholeProgramOptimization=false /p:GateKitFast=true
msbuild BG3Tools.sln /t:BG3Extender "/p:Configuration=Game Release" /p:Platform=x64 /p:PostBuildEventUseInBuild=false %FAST% "/p:ForceImportAfterCppTargets=%~dp0gatekit-build.targets" /m /nologo /v:minimal /consoleloggerparameters:Summary;ErrorsOnly
exit /b %ERRORLEVEL%
