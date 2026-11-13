@echo off
rem Builds fastexists.dll (x64) into research\perf\fastexists\out. Needs Visual Studio's C++ tools.
setlocal
if not defined VCVARS for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"
if not defined VCVARS ( echo set VCVARS to the path of your vcvars64.bat & exit /b 1 )
call "%VCVARS%" >nul || exit /b 1
cd /d "%~dp0" || exit /b 1
if not exist out mkdir out
cl /nologo /O2 /EHsc /MT /LD /W3 /D_CRT_SECURE_NO_WARNINGS /Fo:out\ /Fe:out\fastexists.dll fastexists.cpp /link /NOLOGO user32.lib|| exit /b 1
