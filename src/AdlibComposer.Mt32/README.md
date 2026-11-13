# mt32emu.dll (Roland MT-32 emulator)

Jukebox의 MIDI module = **Roland MT-32** 재생에 쓰는 [Munt](https://github.com/munt/munt) libmt32emu 2.8.6 (커밋 `91d2205`)을 C API만 내보내는 64비트 DLL로 빌드한 것.

- 라이선스: **LGPL-2.1 이상** (`LICENSE.mt32emu.txt`). 프로그램은 DLL을 P/Invoke로 부르기만 하므로(`src/AdlibComposer.Audio/Mt32Emu.cs`) 같은 이름의 다른 빌드로 바꿔 끼울 수 있다.
- 빌드: `build_mt32emu.bat` (git + Visual Studio 2022 Build Tools의 C++ · Windows SDK 필요). Munt 소스를 `build\`에 받아 `cl`로 컴파일한다.
- ROM은 들어 있지 않다. Roland MT-32 / CM-32L ROM(컨트롤 + PCM)을 직접 구해 설정의 **MT-32 ROM folder**(기본 실행 폴더의 `resource\`)에 넣는다. 파일 이름은 상관없다(SHA-1로 알아본다).
- DLL이나 ROM이 없으면 MT-32 곡은 MT-32 음색 번호를 GM에 맞춰 FM / Windows 신디사이저로 낸다.
