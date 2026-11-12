# AdLib Composer

AdLib Visual Composer(작곡)와 Instrument Maker(악기 편집)를 **한 프로그램**으로 재현하는 C# 솔루션.
BNK 악기를 보이스(채널)에 할당하고, 건반 악보에 노트를 배치하고, OPL2 에뮬레이션으로 재생한다.

- 파일: ROL(곡) / BNK(악기 뱅크) / INS(악기 1개) 읽기·쓰기
- 화면: 원본 스크린샷(640x400 흑백 DOS 화면) 레이아웃을 따른 WinForms GUI
- 확장: IMS·SOP 같은 곡 포맷, 악보(스코어) 변환, OPL 칩 교체를 위한 인터페이스 제공

## 빌드 / 실행

필요: **Visual Studio 2022 17.8 이상** + **.NET 8 SDK** (Windows)

1. `AdlibComposer.sln` 열기
2. 시작 프로젝트를 `AdlibComposer.App`으로 지정하고 F5
3. `samples/demo.rol` + `samples/demo.bnk`로 동작 확인 (곡을 열면 같은 폴더의 BNK를 자동으로 제안)

자체 테스트 (Windows/Linux/macOS 모두 가능, 외부 패키지 없음):

```
dotnet run --project tests/AdlibComposer.SelfTest
dotnet run --project tests/AdlibComposer.SelfTest -- --wav demo.wav   # 데모 곡을 WAV로 렌더링
```

외부 NuGet 패키지를 쓰지 않는다 (오디오 출력은 winmm `waveOut` P/Invoke).

## 사용법

**공통**: 메뉴 `File(F5) Edit(F6) Playback(F7) Display(F8) Options(F9) Help(F10)`, 우측 상단 `Composer / Instrument Maker` 전환 (Ctrl+1 / Ctrl+2)

**Composer**
| 조작 | 동작 |
|---|---|
| 좌상단 `✎` / `I` | 연필(입력) / 선택 도구 |
| `1~6, BD, SD, TO, CY, HH, All` | 보이스 선택 (리듬 모드). 멜로딕 모드는 `1~9` |
| 연필: 클릭/드래그 | 노트 추가(드래그로 길이). 기존 노트를 클릭하면 삭제 |
| 선택: 클릭/드래그 | 선택/이동/오른쪽 끝 드래그로 길이 변경, Ctrl+클릭 다중 선택 |
| 오른쪽 클릭 | 노트 삭제 |
| 상단 `Instr.` 행 더블클릭 (또는 Insert) | 현재 위치에 악기 교체 이벤트 지정 |
| 왼쪽 건반 클릭 | 현재 보이스 악기로 시음 |
| 휠 / Shift+휠 / Ctrl+휠 | 세로 스크롤 / 가로 스크롤 / 줌 |
| Space / Home / Esc | 커서부터 재생·정지 / 처음부터 / 정지 |
| Ctrl+Z, Y, A, C, V, Delete | 실행취소, 다시실행, 전체선택, 복사, 붙여넣기, 삭제 |

**Instrument Maker**
- 왼쪽 Carrier / 오른쪽 Modulator, `F1` / `F2`로 열 전환
- 슬라이더(◁ ▷ 클릭, 드래그, 방향키, 휠) + 체크박스, 변경 즉시 소리에 반영
- 하단 건반 클릭 또는 `Z S X D C V G B H N J M ,` / `Q 2 W 3 E R 5 T 6 Y 7 U I`로 시음, `PageUp/PageDown` 옥타브
- `Options → Preview Voice`로 BD/SD/TO/CY/HH 퍼커션 시음 (리듬 모드 레지스터 사용)
- `List...`로 뱅크 안 악기 선택/추가/복사/이름변경/삭제

## 구조

| 프로젝트 | 역할 |
|---|---|
| `AdlibComposer.Core` | 모델: `Song`(틱 기반 ROL 구조), `VoiceTrack`, `Note`, `AdlibInstrument`, `InstrumentBank`, `TempoMap` |
| `AdlibComposer.Formats` | `RolFormat`, `BnkFormat`, `InsFormat`, `FormatRegistry`, `Planned/` (IMS·SOP 자리표시) |
| `AdlibComposer.Audio` | `Opl2Emulator`(내장 OPL2), `OplSynth`(보이스→레지스터, 리듬 모드), `Sequencer`, `AudioEngine`, `WaveOutPlayer`, `OplMapping` |
| `AdlibComposer.Notation` | `Score` 모델, `ScoreBuilder`(Song→마디/음표, 붙임줄), `IScoreExporter`, 텍스트 내보내기 예제 |
| `AdlibComposer.App` | WinForms GUI: `MainForm`, `ComposerView`, `InstrumentView`, `PianoRollControl`, `RetroSlider` 등 |
| `tests/AdlibComposer.SelfTest` | 의존성 없는 자체 테스트 + 데모 WAV 렌더러 |

## 확장 방법

**곡 포맷 추가 (IMS, SOP 등)**
1. `ISongFormat`을 구현한 클래스를 만든다 (`Formats/Planned/PlannedSongFormats.cs`에 뼈대가 있다).
2. 파일 내용을 `Song`(보이스별 `Note`, 악기/볼륨/피치/템포 이벤트)으로 변환한다.
3. `FormatRegistry.CreateDefault()`에 `r.Songs.Add(new ImsFormat());` 한 줄 추가.
→ 열기/저장 대화상자, 편집, 재생, 악보 변환이 그대로 동작한다.

**악보 변환**
- `ScoreBuilder.Build(song, options)`가 `Song`을 마디/음표 구조(`Score`)로 바꾼다 (마디 경계 분할, 붙임줄, 쉼표, 양자화).
- 내보내기 형식은 `IScoreExporter`를 구현해 추가한다 (예: MusicXML, MIDI, 이미지). 현재는 텍스트 예제만 있다 (`File → Export Score (text)...`).

**OPL 칩 교체**: `IOplChip`를 구현(예: Nuked-OPL3 C# 포팅)하고 `new OplSynth(chip)`으로 주입.

**악기 ↔ 레지스터 규칙 수정**: 모두 `OplMapping`과 `OplSynth.LoadVoice`에 모여 있다.

## 검증 상태

- 확인함: Core / Formats / Audio / Notation 빌드, 자체 테스트 44개 통과 (BNK·INS·ROL 왕복 저장, 바이트 동일 재저장,
  OPL 음높이(ROL 57 = 440Hz), 리듬 모드 5종 발음, 시퀀서 타이밍과 템포 배수, 악보 변환)
- 확인함: 데모 곡 렌더링 결과의 음높이(261.8Hz / 392.3Hz)와 드럼 타격 위치
- **확인하지 못함**: WinForms GUI는 .NET Framework 참조 어셈블리로 컴파일 검사만 했고 **실제로 실행해 보지 못했다**
  → 첫 실행에서 배치 어긋남이나 예외가 있을 수 있다.
- **확인하지 못함**: 실제 `STANDARD.BNK`, 실제 ROL 곡 파일 (포맷 문서 기준으로 구현)

## 가정 / 확인 필요 항목

| 항목 | 현재 가정 | 확인 방법 |
|---|---|---|
| Output Level 방향 | 파일 값 = 화면 값, 63이 최대 음량 → OPL TL = 63 − 값 | 실제 BNK를 열어 소리가 너무 작거나 안 나면 `Options → Output Level` 토글 |
| Sustain Level 방향 | 파일 값을 레지스터에 그대로 사용 (0 = 최대) | 실제 곡과 비교 |
| Connection(FM/가산) | 원본 화면에 항목이 없어 헤더에 체크박스로 추가 (파일 값 0 = 가산) | 원본 Options 메뉴 확인 |
| 피치 이벤트 범위 | 0.0~2.0 → ±2반음 (`Options → Pitch Bend Range`) | 원본 재생과 비교 |
| 리듬 모드 탐탐 초기 음높이 | 노트 24 | 원본 재생과 비교 |
| `B` 상자 / `✎`·`I` 도구 | `B`는 커서 아래 음 이름으로 해석, 도구는 입력/선택으로 해석 | 원본 동작 |
| OPL 엔벨로프 타이밍 | 데이터시트 시간 곡선 근사 (실제 칩과 완전 일치하지 않음) | 필요 시 칩 교체 |
| ROL 기본값 | 틱/박 48, 박/마디 4, scaleX/Y 1 | 원본 파일 헤더 |

## 알려진 제한

- 재생은 시작 시점의 곡을 기준으로 이벤트를 만든다 (재생 중 편집은 다음 재생부터 반영).
- 재생 중에는 시음이 꺼진다.
- 한 보이스는 동시에 한 음만 낸다 (OPL 제약). 겹치는 노트는 자동으로 잘리거나 밀린다.
- ROL 외 곡 포맷, MusicXML/MIDI 내보내기는 아직 없다 (확장 지점만 준비됨).
