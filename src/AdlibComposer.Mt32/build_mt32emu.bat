@echo off
rem ---------------------------------------------------------------------------
rem  Builds mt32emu.dll (Munt libmt32emu, LGPL-2.1+): 64-bit DLL exporting the C API only.
rem  Requires: git, Visual Studio 2022 Build Tools (C++ and Windows 10/11 SDK).
rem  Output: mt32emu.dll in this folder (AdlibComposer.App copies it to the program folder).
rem ---------------------------------------------------------------------------
setlocal
set MUNT_COMMIT=91d2205
set HERE=%~dp0
set WORK=%HERE%build
set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VCVARS%" set VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VCVARS%" ( echo vcvars64.bat not found & exit /b 1 )

if not exist "%WORK%\munt" (
  git clone -q https://github.com/munt/munt "%WORK%\munt" || exit /b 1
)
git -C "%WORK%\munt" checkout -q %MUNT_COMMIT% || exit /b 1

set SRC=%WORK%\munt\mt32emu\src
rem config.h as CMake would generate it: shared library, C API only, no version tagging
powershell -NoProfile -Command "(Get-Content '%SRC%\config.h.in') -replace '@libmt32emu_VERSION@','2.8.6' -replace '@libmt32emu_VERSION_MAJOR@','2' -replace '@libmt32emu_VERSION_MINOR@','8' -replace '@libmt32emu_VERSION_PATCH@','6' -replace '@libmt32emu_EXPORTS_TYPE@','1' -replace '@libmt32emu_SHARED_DEFINITION@','#define MT32EMU_SHARED' -replace '@libmt32emu_RUNTIME_VERSION_CHECK@','0' | Set-Content '%SRC%\config.h'" || exit /b 1

call "%VCVARS%" >nul || exit /b 1
if not exist "%WORK%\obj" mkdir "%WORK%\obj"
pushd "%SRC%"
cl /nologo /O2 /EHsc /MT /LD /DNDEBUG /Dmt32emu_EXPORTS /DMT32EMU_FASTER_MATH=1 /D_CRT_SECURE_NO_WARNINGS /I. ^
 Analog.cpp BReverbModel.cpp Display.cpp File.cpp FileStream.cpp LA32FloatWaveGenerator.cpp LA32Ramp.cpp LA32WaveGenerator.cpp ^
 MidiStreamParser.cpp Part.cpp Partial.cpp PartialManager.cpp Poly.cpp ROMInfo.cpp Synth.cpp Tables.cpp TVA.cpp TVF.cpp TVP.cpp ^
 sha1\sha1.cpp SampleRateConverter.cpp c_interface\c_interface.cpp ^
 /Fo"%WORK%\obj\\" /Fe"%HERE%mt32emu.dll" /link /NOIMPLIB
set RC=%ERRORLEVEL%
popd
del "%HERE%mt32emu.exp" "%HERE%mt32emu.lib" 2>nul
if not "%RC%"=="0" exit /b %RC%
copy /y "%WORK%\munt\mt32emu\COPYING.LESSER.txt" "%HERE%LICENSE.mt32emu.txt" >nul
echo Built %HERE%mt32emu.dll
