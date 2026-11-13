@echo off
rem Fetches and builds what BG3Extender needs (a subset of bg3se's pull-externals.bat:
rem no curl, no ZipLib - only the updater and crash reporter use those).
rem Unpinned Norbyte repositories are set to their last commit before PIN_DATE,
rem the day after the Script Extender commit the fork is based on.
setlocal
set PIN_DATE=2026-10-25
if not defined VCVARS for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"
if not defined VCVARS ( echo set VCVARS to the path of your vcvars64.bat & exit /b 1 )
call "%VCVARS%" || exit /b 1
cd /d "%~dp0bg3se\External" || exit /b 1

if not exist Detours (
  git clone https://github.com/microsoft/Detours --branch v4.0.1 || exit /b 1
  pushd Detours & nmake & popd
)
if not exist glm git clone https://github.com/g-truc/glm --branch 1.0.3
rem Script Extender v33 builds against the "se" branch of Norbyte/imgui (1.91.9b, no docking);
rem "master" (1.93) is what v34 uses and does not compile with v33.
if not exist imgui ( git clone --branch se https://github.com/Norbyte/imgui && call :pin imgui )
rem The GateKit's change to the imgui backend (HDR10 output); skipped when already applied.
pushd imgui & git apply --check "%~dp0patches\imgui\0001-DX11-backend-optional-HDR10-output-encoding.diff" 2>nul && git apply "%~dp0patches\imgui\0001-DX11-backend-optional-HDR10-output-encoding.diff" & popd
if not exist lua ( git clone https://github.com/Norbyte/lua-dos lua && call :pin lua )
if not exist optick ( git clone https://github.com/Norbyte/optick && call :pin optick )
if not exist NoesisGUI-NativeSDK-win-3.1.7-Indie (
  curl -L http://bg3se-updates.norbyte.dev/Stuff/NoesisGUI-NativeSDK-win-3.1.7-Indie.zip -o NoesisGUI-NativeSDK-win-3.1.7-Indie.zip || exit /b 1
  tar -xf NoesisGUI-NativeSDK-win-3.1.7-Indie.zip
)
if not exist protoc (
  curl -L https://github.com/protocolbuffers/protobuf/releases/download/v36.1/protoc-36.1-win64.zip -o protoc.zip || exit /b 1
  mkdir protoc & tar -xf protoc.zip -C protoc/
)
if not exist protobuf git clone https://github.com/protocolbuffers/protobuf --branch v36.1
if not exist protobuf-build\Release\libprotobuf-lite.lib (
  cmake -G "Visual Studio 18 2026" -A x64 -DCMAKE_CXX_STANDARD=23 -DCMAKE_CXX_FLAGS="/DWIN32 /D_WINDOWS /EHsc /D_ITERATOR_DEBUG_LEVEL=0" -S protobuf -B protobuf-build || exit /b 1
  cmake --build protobuf-build --config Release --target libprotobuf-lite -- /m /nologo /consoleloggerparameters:summary || exit /b 1
)
if not exist rapidjson (
  git clone https://github.com/tencent/rapidjson
  pushd rapidjson & git reset --hard 24b5e7a8b27f42fa16b96fc70aade9106cf7102f & popd
)
if not exist SDL (
  curl -L https://github.com/libsdl-org/SDL/releases/download/release-2.30.6/SDL2-devel-2.30.6-VC.zip -o SDL.zip || exit /b 1
  mkdir SDL & tar -xf SDL.zip -C SDL/ --strip-components 1
)
if not exist tinycrypt git clone https://github.com/intel/tinycrypt
rem stb_image decodes the image files mods bring (patch 11); the commit is the one upstream pins for v34.
if not exist stb (
  git clone https://github.com/nothings/stb
  pushd stb & git reset --hard 7023e273f1513b68b8d4086077c6faca555d50df & popd
)
if not exist Vulkan git clone https://github.com/KhronosGroup/Vulkan-Headers Vulkan --branch vulkan-sdk-1.4.357
echo EXTERNALS DONE
exit /b 0

:pin
pushd %1
for /f %%c in ('git rev-list -1 --before^=%PIN_DATE% HEAD') do git checkout -q %%c
git log -1 --format="%1 pinned to %%h %%cd"
popd
exit /b 0
