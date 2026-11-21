# 안드로이드 Jukebox (개인 태블릿용 APK)

Windows 프로그램(`FMOPLTools.exe`)은 그대로 두고, 재생(Jukebox)만 하는 안드로이드 앱을 따로 만든다.
엔진(Core · Formats · Audio)은 그대로 쓰고, 화면과 오디오 출력만 안드로이드용으로 새로 짠다.

## 원칙

- **Windows 실행 파일에 영향 없음**: `AdlibComposer.App` · `AdlibComposer.Audio.Windows`는 고치지 않는다. 엔진 프로젝트도 안드로이드 때문에 동작을 바꾸지 않는다.
- 안드로이드 프로젝트는 `FMOPLTools.sln`에 넣지 않고 `FMOPLTools.Android.sln`에 따로 둔다 → Android 워크로드가 없는 PC에서도 기존 솔루션이 그대로 빌드된다.
- 외부 NuGet 없이: .NET for Android (`net10.0-android`) + 안드로이드 기본 뷰.
- 화면 없는 재생 묶음(`AdlibComposer.Player`, net8.0)은 SelfTest로 PC에서 시험한다.

## 준비 (한 번)

```
dotnet workload install android
dotnet build -t:InstallAndroidDependencies -f net10.0-android src/AdlibComposer.Android/AdlibComposer.Android.csproj -p:AcceptAndroidSDKLicenses=true
```

태블릿: 설정 → 개발자 옵션 → USB 디버깅 (또는 APK를 복사해 "출처를 알 수 없는 앱" 설치 허용).

## 단계

| 단계 | 버전 | 내용 | 상태 |
|---|---|---|---|
| A1 | v2.3.29 | `AdlibComposer.Player`: `JukeboxSession`(곡 열기 · 재생 · 일시정지 · 정지 · 이동 · 끝 알림, ROL · IMS · MUS · SOP · 트래커 · XM · IT · MIDI(FM) · VGM 계열 · hoot 항목), `SongFolder`(폴더 · zip(언제나 캐시에 풀기) · 곡 여럿 든 파일). SelfTest `player` | 끝 |
| A2 | | 안드로이드 앱 뼈대 + `AudioTrack` 출력(`IAudioOutput`) + 리소스(STANDARD.BNK · GENMIDI.OP2 · systems.json) 복사 → 내장 데모 곡 재생 | |
| A3 | | Jukebox 화면 최소판: 저장소 권한(모든 파일 접근), 폴더 목록 · zip · 곡 여럿 든 파일, 재생 · 일시정지 · 정지 · 이전 · 다음 · 위치 막대, 다음 곡 자동, 마지막 폴더 기억 | |
| A4 | | 성능 확인 · AOT, 백그라운드 재생 중 화면 꺼짐 처리, 서명한 릴리스 APK 만들기 | |

## 빠진 것 (최소판)

가사(ISS) · 채널 믹서 · 건반 표시 · 8-bit MIDI 신디 · Windows MIDI · MT-32 에뮬레이터(GM 번호로 냄) · hoot 목록 트리(xml) — hoot zip은 폴더 목록에서 열면 .hoot 항목으로 보인다(목록 xml이 zip 옆에 있을 때).

## 주의

- `ZipPath`(zip을 풀지 않고 읽기)는 경로 구분자를 `\`로 다뤄 안드로이드에서 쓰지 않는다. 안드로이드는 zip을 언제나 `ZipFolder.Extract`로 캐시에 푼다.
- 안드로이드 파일 시스템은 대소문자를 가린다: 같은 이름 BNK · ISS 찾기는 대소문자 무시로 목록을 훑는다 (`JukeboxSession.SideFile`).
