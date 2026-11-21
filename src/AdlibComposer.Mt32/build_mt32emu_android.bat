@echo off
rem ---------------------------------------------------------------------------
rem  Builds libmt32emu.so (Munt libmt32emu, LGPL-2.1+) for Android arm64-v8a: C API only, libc++ linked statically.
rem  Requires: git, Android NDK (%ANDROID_NDK_HOME%, or the newest under %LOCALAPPDATA%\Android\Sdk\ndk).
rem    Install: sdkmanager "ndk;30.0.16248370"  (docs/android.md)
rem  Output: android\arm64-v8a\libmt32emu.so (AdlibComposer.Android packs it into the APK).
rem ---------------------------------------------------------------------------
setlocal enabledelayedexpansion
set MUNT_COMMIT=91d2205
set HERE=%~dp0
set WORK=%HERE%build
set API=24

set NDK=%ANDROID_NDK_HOME%
if "%NDK%"=="" for /d %%D in ("%LOCALAPPDATA%\Android\Sdk\ndk\*") do set NDK=%%D
if "%NDK%"=="" ( echo Android NDK not found & exit /b 1 )
set CXX=%NDK%\toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android%API%-clang++.cmd
if not exist "%CXX%" ( echo clang++ not found: %CXX% & exit /b 1 )

if not exist "%WORK%\munt" (
  git clone -q https://github.com/munt/munt "%WORK%\munt" || exit /b 1
)
git -C "%WORK%\munt" checkout -q %MUNT_COMMIT% || exit /b 1

set SRC=%WORK%\munt\mt32emu\src
rem config.h as CMake would generate it: shared library, C API only, no version tagging
powershell -NoProfile -Command "(Get-Content '%SRC%\config.h.in') -replace '@libmt32emu_VERSION@','2.8.6' -replace '@libmt32emu_VERSION_MAJOR@','2' -replace '@libmt32emu_VERSION_MINOR@','8' -replace '@libmt32emu_VERSION_PATCH@','6' -replace '@libmt32emu_EXPORTS_TYPE@','1' -replace '@libmt32emu_SHARED_DEFINITION@','#define MT32EMU_SHARED' -replace '@libmt32emu_RUNTIME_VERSION_CHECK@','0' | Set-Content '%SRC%\config.h'" || exit /b 1

set OUT=%HERE%android\arm64-v8a
if not exist "%OUT%" mkdir "%OUT%"
pushd "%SRC%"
rem 16 KB page alignment: required for apps on Android 15+ devices with 16 KB pages
call "%CXX%" -shared -fPIC -O2 -DNDEBUG -Dmt32emu_EXPORTS -DMT32EMU_FASTER_MATH=1 -fvisibility=hidden -I. ^
 Analog.cpp BReverbModel.cpp Display.cpp File.cpp FileStream.cpp LA32FloatWaveGenerator.cpp LA32Ramp.cpp LA32WaveGenerator.cpp ^
 MidiStreamParser.cpp Part.cpp Partial.cpp PartialManager.cpp Poly.cpp ROMInfo.cpp Synth.cpp Tables.cpp TVA.cpp TVF.cpp TVP.cpp ^
 sha1\sha1.cpp SampleRateConverter.cpp c_interface\c_interface.cpp ^
 -static-libstdc++ -Wl,-z,max-page-size=16384 -Wl,--gc-sections -s -o "%OUT%\libmt32emu.so"
set RC=%ERRORLEVEL%
popd
if not "%RC%"=="0" exit /b %RC%
copy /y "%WORK%\munt\mt32emu\COPYING.LESSER.txt" "%HERE%LICENSE.mt32emu.txt" >nul
echo Built %OUT%\libmt32emu.so
