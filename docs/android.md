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
| A3 | v2.3.31 | Jukebox 화면 최소판: 저장소 권한(모든 파일 접근), 폴더 목록 · zip · 곡 여럿 든 파일, 재생 · 일시정지 · 정지 · 이전 · 다음 · 위치 막대, 다음 곡 자동 · 한 곡 반복, 마지막 폴더 기억 | 끝 (기기 확인 전) |
| A4 | v2.3.32 | 전경 서비스 (화면 꺼도 재생, 알림) + 부분 wake lock, 렌더 부하 표시, Release = 전체 AOT (arm64, 약 9.7MB), 서명 키 설정 자리 | 끝 (기기 확인 전) |

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
%LOCALAPPDATA%\Android\jdkin\keytool -genkeypair -v -keystore %USERPROFILE%mopl-jukebox.keystore -alias jukebox -keyalg RSA -keysize 2048 -validity 36500
```

`src/AdlibComposer.Android/signing.local.props`:

```xml
<Project>
  <PropertyGroup>
    <AndroidKeyStore>true</AndroidKeyStore>
    <AndroidSigningKeyStore>$(USERPROFILE)mopl-jukebox.keystore</AndroidSigningKeyStore>
    <AndroidSigningKeyAlias>jukebox</AndroidSigningKeyAlias>
    <AndroidSigningStorePass>비밀번호</AndroidSigningStorePass>
    <AndroidSigningKeyPass>비밀번호</AndroidSigningKeyPass>
  </PropertyGroup>
</Project>
```

이미 디버그 키로 설치한 앱은 키가 바뀌면 덮어 설치가 안 된다 → 한 번 지우고 설치.

## 화면 (A3)

- 가로 화면: 왼쪽 폴더 목록 | 오른쪽 재생 데크. 세로 화면: 목록 위 · 데크 아래.
- 위 막대: [▲ 위로] [저장소] (내부 저장소 맨 위) [데모] (내장 데모 곡) + 지금 경로.
- 목록: 📁 폴더 · 🗜 zip (누르면 캐시에 풀고 안으로, 처음은 시간이 걸림) · ♪ 곡 · ▸ 곡 여럿 든 파일 (누르면 곡 목록). 곡을 누르면 그 목록이 재생 목록이 된다.
- 데크: 제목 · 형식 · 게임 정보, 위치 막대 (끌어서 이동), ⏮ ▶/❚❚ ■ ⏭, 다음 곡 자동 · 한 곡 반복 (기억).
- 저장소 권한: 안드로이드 11 이상은 처음 실행할 때 '모든 파일 접근' 설정 화면이 열린다 → FMOPL Jukebox를 켜고 돌아오면 목록이 보인다. 10 이하는 읽기 권한을 묻는다.
- 곡 열기 · 폴더 읽기 · zip 풀기는 뒤 스레드 (hoot 항목은 미리 렌더해서 몇 초 걸릴 수 있다).

## 빠진 것 (최소판)

가사(ISS) · 채널 믹서 · 건반 표시 · 8-bit MIDI 신디 · Windows MIDI · MT-32 에뮬레이터(GM 번호로 냄) · hoot 목록 트리(xml) — hoot zip은 폴더 목록에서 열면 .hoot 항목으로 보인다(목록 xml이 zip 옆에 있을 때).

## 주의

- `ZipPath`(zip을 풀지 않고 읽기)는 경로 구분자를 `\`로 다뤄 안드로이드에서 쓰지 않는다. 안드로이드는 zip을 언제나 `ZipFolder.Extract`로 캐시에 푼다.
- 안드로이드 파일 시스템은 대소문자를 가린다: 같은 이름 BNK · ISS 찾기는 대소문자 무시로 목록을 훑는다 (`JukeboxSession.SideFile`).
