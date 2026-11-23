# 안드로이드 Jukebox (개인 태블릿용 APK)

Windows 프로그램(`FMOPLTools.exe`)은 그대로 두고, 재생(Jukebox)만 하는 안드로이드 앱을 따로 만든다.
엔진(Core · Formats · Audio)은 그대로 쓰고, 화면과 오디오 출력만 안드로이드용으로 새로 짠다.

## 원칙

- **Windows 실행 파일에 영향 없음**: `AdlibComposer.App` · `AdlibComposer.Audio.Windows`는 고치지 않는다. 엔진 프로젝트도 안드로이드 때문에 동작을 바꾸지 않는다.
- 안드로이드 프로젝트(`src/AdlibComposer.Android`)는 `FMOPLTools.sln`에 넣지 않는다 → Android 워크로드가 없는 PC에서도 기존 솔루션이 그대로 빌드된다. 따로 솔루션도 만들지 않는다 (저장소 맨 위에서 `dotnet build`가 솔루션 둘로 헷갈리지 않게). csproj 하나로 엔진까지 함께 빌드된다 (VS에서는 csproj를 바로 연다).
- 외부 NuGet 없이: .NET for Android (`net10.0-android`) + 안드로이드 기본 뷰.
- 화면 없는 재생 묶음(`AdlibComposer.Player`, net8.0)은 SelfTest로 PC에서 시험한다.

## 준비 (한 번)

```
dotnet workload install android
dotnet build -t:InstallAndroidDependencies -f net10.0-android src/AdlibComposer.Android/AdlibComposer.Android.csproj -p:AcceptAndroidSDKLicenses=true -p:AndroidSdkDirectory=%LOCALAPPDATA%\Android\Sdk -p:JavaSdkDirectory=%LOCALAPPDATA%\Android\jdk
```

PowerShell에서는 `%LOCALAPPDATA%` 대신 `$env:LOCALAPPDATA`로 쓴다 (`%LOCALAPPDATA%`는 cmd에서만 풀린다. PowerShell에서 그대로 쓰면 프로젝트 폴더 안에 `%LOCALAPPDATA%` 폴더가 생겨 SDK · JDK가 거기에 받아진다).

SDK · JDK를 이 위치(`%LOCALAPPDATA%\Android\Sdk` · `jdk`)에 받으면 csproj가 알아서 찾는다 (`ANDROID_HOME` · `JAVA_HOME`을 정했으면 그쪽).

## 빌드 · 설치

```
dotnet build src/AdlibComposer.Android -c Debug                       # bin/Debug/net10.0-android/net.fmopltool.jukebox-Signed.apk
dotnet build src/AdlibComposer.Android -c Debug -t:Install            # USB로 붙은 태블릿에 바로 설치 (adb)
```

- Debug APK도 어셈블리를 APK 안에 넣는다 (`EmbedAssembliesIntoApk`): 빠른 배포 APK는 파일만 복사해 설치하면 바로 죽는다. 대신 Debug APK는 약 45MB이고 최적화(AOT)가 없다 → 소리가 끊기면 Release(A4)로.
- 앱 이름 FMOPL Jukebox, 패키지 `net.fmopltool.jukebox`, 버전 = `Directory.Build.props`의 Version (번호 = 주 × 1000000 + 부 × 1000 + 마지막).
- 처음 실행할 때 (앱 버전이 바뀌면 다시) APK 안 `resource`(STANDARD.BNK · GENMIDI.OP2 · systems.json)와 데모 곡을 앱 폴더로 복사한다. zip 캐시는 앱 캐시 폴더/zip.

태블릿: 설정 → 개발자 옵션 → USB 디버깅 (또는 APK를 복사해 "출처를 알 수 없는 앱" 설치 허용).

## 단계

| 단계 | 버전 | 내용 | 상태 |
|---|---|---|---|
| A1 | v2.3.29 | `AdlibComposer.Player`: `JukeboxSession`(곡 열기 · 재생 · 일시정지 · 정지 · 이동 · 끝 알림, ROL · IMS · MUS · SOP · 트래커 · XM · IT · MIDI(FM) · VGM 계열 · hoot 항목), `SongFolder`(폴더 · zip(언제나 캐시에 풀기) · 곡 여럿 든 파일). SelfTest `player` | 끝 |
| A2 | v2.3.30 | 안드로이드 앱 뼈대 + `AudioTrack` 출력(`IAudioOutput`) + 리소스(STANDARD.BNK · GENMIDI.OP2 · systems.json) 복사 → 내장 데모 곡 재생 (시험 화면) | 끝 |
| A3 | v2.3.31 | Jukebox 화면 최소판: 저장소 권한(모든 파일 접근), 폴더 목록 · zip · 곡 여럿 든 파일, 재생 · 일시정지 · 정지 · 이전 · 다음 · 위치 막대, 다음 곡 자동 · 한 곡 반복, 마지막 폴더 기억 | 끝 (기기 확인) |
| A4 | v2.3.32 | 전경 서비스 (화면 꺼도 재생, 알림) + 부분 wake lock, 렌더 부하 표시, Release = 전체 AOT (arm64, 약 9.7MB), 서명 키 설정 자리 | 끝 (기기 확인) |
| A5 | v2.3.33 | 폴더 위로: 목록 맨 위 `..` 줄, 안드로이드 뒤로 (제스처 · ◁ 키) = 위로 | 끝 (기기 확인) |
| A6 | v2.3.34 | 안드로이드 15+ 전체 화면(edge-to-edge) 대응: 제목 줄 없앰, 상태 · 내비게이션 막대만큼 안쪽 여백 (위 막대가 상태 표시줄 밑에 가려지던 것) | 끝 (기기 확인) |
| A7 | v2.3.35 | 가로 화면 = Windows Jukebox 배치: 곡 정보 + 조작 상자 (⏮ −5초 ▶ ■ +5초 ⏭ · 다음 곡 자동 · 반복 · 속도 · 키 · 볼륨 · 초기화 · 진행 막대) / 채널 믹서 (켜기/끄기 · 솔로 · 채널 음높이 · 음량 막대) / 건반. 세로 화면은 그대로. `JukeboxSession`에 채널 목록 · 키 · 솔로 · ±초 · BPM | 끝 (기기 확인) |
| A8 | v2.3.36 | 가사(같은 이름 ISS) · 표시 방식 (Implay · Ocplay · 노래방 · 노래방(Cyberfunk)) · 소리 (거친 음색 · 그 시절 스피커 · 원래 소리 / 8-bit · MIDI 신디 · 모듈) · [⚙ 설정] 창 (소리 · resource 폴더 · 노래방 그림 폴더). `JukeboxSession`에 가사 · 소리 고르기 · MT-32 에뮬레이터 (Munt arm64 + 개인용 ROM) | 끝 (기기 확인) |
| A9 | v2.3.37 | 세로 화면: 목록 = 곡 5줄 높이, 가운데 = 표시 방식 단추 (Implay · Ocplay · 노래방 · 노래방(Cyberfunk)) + 가사 + 표시 화면, 아래 데크 = 재생 그대로 + 속도 · 키 · MIDI 신디 · 거친 음색 · 그 시절 스피커. 표시 묶음은 가로 · 세로 공용 (`JukeboxStage`) | 끝 (기기 확인) |
| A10 | v2.4.10 | 파일 검색: 가로 화면 왼쪽 목록 위 검색 칸 — 지금 폴더와 그 아래 모든 폴더에서 이름으로 (낱말 모두 포함, 대소문자 무시, 많아야 500개, zip 안은 빼고). 곡 목록(곡 여럿 든 파일)에서는 그 목록 안에서만. `SongFolder.Search` · `Filter` | 끝 |

## 태블릿에 설치 (권장: Release)

```
dotnet publish src/AdlibComposer.Android -c Release
```

→ `src/AdlibComposer.Android/bin/Release/net10.0-android/publish/net.fmopltool.jukebox-Signed.apk` 를 태블릿에 복사해 설치 (출처를 알 수 없는 앱 허용). USB 디버깅이 켜져 있으면 `adb install -r <apk>`.

- Release = 전체 AOT (엔진을 미리 기계어로): Debug(JIT)보다 칩 · CPU 에뮬레이션이 빠르다. arm64 전용.
- 데크 아래 **렌더 부하 %** = 소리 블록 하나를 만드는 데 든 시간 ÷ 그 소리 길이. 80%를 넘으면 끊길 수 있다 (무거운 hoot 항목 · 칩 많은 VGM).
- 재생 중에는 알림이 뜨고 (전경 서비스) 화면을 꺼도 계속 재생한다. 정지 · 일시정지하면 알림이 없어진다.

### 서명 키 (선택)

키를 정하지 않으면 이 PC의 .NET 디버그 키로 서명된다. 다른 PC에서 만든 APK로 덮어 설치하려면 같은 키가 필요하다 → 키를 한 번 만들고 두 PC에 같은 파일을 둔다 (저장소에 넣지 않음, `.gitignore`).

```
%LOCALAPPDATA%\Android\jdk\bin\keytool -genkeypair -v -keystore %USERPROFILE%\fmopl-jukebox.keystore -alias jukebox -keyalg RSA -keysize 2048 -validity 36500
```

`src/AdlibComposer.Android/signing.local.props`:

```xml
<Project>
  <PropertyGroup>
    <AndroidKeyStore>true</AndroidKeyStore>
    <AndroidSigningKeyStore>$(USERPROFILE)\fmopl-jukebox.keystore</AndroidSigningKeyStore>
    <AndroidSigningKeyAlias>jukebox</AndroidSigningKeyAlias>
    <AndroidSigningStorePass>비밀번호</AndroidSigningStorePass>
    <AndroidSigningKeyPass>비밀번호</AndroidSigningKeyPass>
  </PropertyGroup>
</Project>
```

이미 디버그 키로 설치한 앱은 키가 바뀌면 덮어 설치가 안 된다 → 한 번 지우고 설치.

## 화면 (A3)

- 가로 화면 (A7): 왼쪽 폴더 목록 | 오른쪽 Windows Jukebox 배치 데크 — 위 [곡 정보 (시간 · ♩ · 속도 · 키 · 볼륨 · 상태, 제목, 형식) | 조작 상자], 가운데 채널 믹서 (왼쪽 칸 누르기 = 켜기/끄기, 길게 = 그 채널만, − + = 채널 음높이, 위아래로 끌어 넘김), 아래 건반 C0~B8 (채널 색). 다음 곡 자동 · 반복은 세로 데크와 같은 값.
- 세로 화면 (A9): 위 = 목록 (곡 5줄 높이), 가운데 = 표시 방식 단추 · 가사 · 표시 화면 (남는 칸 전부), 아래 = 재생 데크 (제목 · 위치 · ⏮ ▶ ■ ⏭ · 다음 곡 자동 · 반복) + 속도 · 키 + MIDI 신디 · 모노 / 스테레오 · 거친 음색 · 그 시절 스피커. 원래 소리 상자 · 모듈 · 볼륨은 가로 화면이나 [⚙ 설정].
- 표시 방식 (A8, 가로 화면 곡 정보 아래 단추): Implay = 가사 + 채널 믹서, Ocplay = 가사 + 작은 악보 · 세로 음량 막대, 노래방 = 곡 폴더 그림 (없으면 설정의 그림 폴더, 10초마다 바뀜, 누르면 다음 그림) + 가사, 노래방(Cyberfunk) = 별 · 워프 · 튕기는 도형 + 네온 가사 (오른쪽 위 체크박스로 층 켜고 끄기). 가사 = 곡 옆 같은 이름 ISS (ROL · IMS · SOP).
- 소리 (가로 데크 맨 위 막대, 또는 [⚙ 설정]): 모노 / 스테레오 (맨 왼쪽) · 거친 음색 · 그 시절 스피커 · 원래 소리 / 8-bit 풍 8가지 (MIDI · ROL · IMS) · MIDI 신디 (OPL FM · 8-bit, 원래 소리 상자와 같은 설정) · 모듈 (GM · MT-32 = Munt 에뮬레이터, ROM이나 라이브러리가 없으면 MT-32 음색 번호를 GM으로). 재생 중에 바꾸면 그 자리에서 새 소리로.
- resource 폴더 ([⚙ 설정]): 기본은 APK에 든 STANDARD.BNK · GENMIDI.OP2 · systems.json (처음 실행할 때 앱 폴더로 복사). 왼쪽 목록에서 폴더로 들어가 [지금 목록 폴더로]를 누르면 그 폴더에 있는 파일이 APK 것 대신 쓰인다 (없는 파일은 APK 것). 뱅크는 다음 곡부터, systems.json은 앱을 다시 시작한 뒤부터.
- 파일 검색 (A10, 가로 화면 목록 위 칸): 글을 치면 잠깐 뒤 (또는 키보드 [검색]) 지금 폴더와 그 아래 모든 폴더에서 이름에 낱말이 모두 든 폴더 · zip · 곡을 찾아 목록에 (상대 경로로, 얕은 폴더부터, 많아야 500개). 곡을 누르면 검색 결과가 재생 목록, 폴더 · zip을 누르면 그리로 들어가며 검색 끝. [✕] · [▲ 위로] · 뒤로 = 검색 끝내고 원래 목록. 곡 여럿 든 파일의 곡 목록에서는 그 목록 안에서만 거른다. 세로 화면에는 검색 칸이 없다.
- 위로: 위 막대 [▲ 위로] · 목록 맨 위 `..` 줄 · 안드로이드 뒤로 (제스처 · ◁ 키). 더 갈 곳이 없으면 뒤로는 앱을 뒤로 보낸다 (재생은 계속).
- 위 막대: [▲ 위로] [저장소] (내부 저장소 맨 위) [데모] (내장 데모 곡) + 지금 경로.
- 목록: 📁 폴더 · 🗜 zip (누르면 캐시에 풀고 안으로, 처음은 시간이 걸림) · ♪ 곡 · ▸ 곡 여럿 든 파일 (누르면 곡 목록). 곡을 누르면 그 목록이 재생 목록이 된다.
- 데크: 제목 · 형식 · 게임 정보, 위치 막대 (끌어서 이동), ⏮ ▶/❚❚ ■ ⏭, 다음 곡 자동 · 한 곡 반복 (기억).
- 저장소 권한: 안드로이드 11 이상은 처음 실행할 때 '모든 파일 접근' 설정 화면이 열린다 → FMOPL Jukebox를 켜고 돌아오면 목록이 보인다. 10 이하는 읽기 권한을 묻는다.
- 곡 열기 · 폴더 읽기 · zip 풀기는 뒤 스레드 (hoot 항목은 미리 렌더해서 몇 초 걸릴 수 있다).

## 빠진 것 (최소판)

세로 화면의 건반 · 볼륨 (가로 화면에 있음) · Windows MIDI · hoot 목록 트리(xml) — hoot zip은 폴더 목록에서 열면 .hoot 항목으로 보인다(목록 xml이 zip 옆에 있을 때).

## 주의

- `ZipPath`(zip을 풀지 않고 읽기)는 경로 구분자를 `\`로 다뤄 안드로이드에서 쓰지 않는다. 안드로이드는 zip을 언제나 `ZipFolder.Extract`로 캐시에 푼다.
- 안드로이드 파일 시스템은 대소문자를 가린다: 같은 이름 BNK · ISS 찾기는 대소문자 무시로 목록을 훑는다 (`JukeboxSession.SideFile`).

## MT-32 에뮬레이터 (개인용)

- 라이브러리: `src/AdlibComposer.Mt32/build_mt32emu_android.bat` → `src/AdlibComposer.Mt32/android/arm64-v8a/libmt32emu.so` (Munt libmt32emu 2.8.6, LGPL-2.1+, 저장소에 들어 있음). 다시 빌드하려면 NDK가 필요하다:
  ```
  %LOCALAPPDATA%\Android\Sdk\cmdline-tools\19.0\bin\sdkmanager.bat --sdk_root=%LOCALAPPDATA%\Android\Sdk "ndk;30.0.16248370"
  ```
  (JAVA_HOME = `%LOCALAPPDATA%\Android\jdk`). libc++는 정적으로 묶고 16 KB 페이지 정렬 (안드로이드 15+).
- ROM: 빌드하는 PC의 `_incoming/MT32/MT32_CONTROL.ROM` · `MT32_PCM.ROM`이 있으면 APK에 들어간다 (`_incoming`은 저장소에 넣지 않음 → 다른 PC에서 빌드하려면 그 PC에도 둔다). 처음 실행할 때 앱 폴더/mt32로 복사.
- [⚙ 설정]의 resource 폴더에 `.ROM` 파일이 있으면 그 폴더의 ROM을 쓴다 (CM-32L 등, 파일 이름은 상관없음 — SHA-1로 알아본다).
- 모듈 = MT-32를 고르면 MIDI 곡을 에뮬레이터로 (hoot MT-32 곡은 설정과 상관없이). 곡 정보 줄에 `MT-32 에뮬레이터 · MT32_CONTROL.ROM + MT32_PCM.ROM`. 8-bit 소리일 때는 쓰지 않는다.
