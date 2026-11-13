# AdLib Composer

AdLib Visual Composer(작곡)와 Instrument Maker(악기 편집)를 **한 프로그램**으로 재현하는 C# 솔루션.
BNK 악기를 보이스(채널)에 할당하고, 건반 악보에 노트를 배치하고, OPL2 에뮬레이션으로 재생한다.

- 파일: ROL·IMS(곡) / BNK(악기 뱅크) / INS(악기 1개) 읽기·쓰기
- 화면: 원본 스크린샷(640x400 흑백 DOS 화면) 레이아웃을 따른 WinForms GUI
- 확장: SOP 같은 곡 포맷, 악보(스코어) 변환, OPL 칩 교체를 위한 인터페이스 제공

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

**메뉴 (원본 Visual Composer 구성 그대로, 이 프로그램의 추가 기능은 각 메뉴 구분선 아래)**
| 메뉴 | 원본 항목 |
|---|---|
| F5 File | New, Open, Save, Save As, Revert / Instrument Bank File (→ 뱅크 브라우저, Ctrl+B) |
| F6 Edit | Edit(선택 도구 I), Copy, Cut, Paste, Clear / Semitone Up·Down (Ctrl+↑↓), Octave Up·Down (Ctrl+Shift+↑↓) |
| F7 Playback | Play (Space), Play Voice (Shift+Space, 현재 보이스만), Interrupt (Esc) / Basic Tempo |
| F8 Display | Tempo 1.0, Instrument, Volume 0.75, Pitch 1.0 (위쪽 행에 보일 트랙 + 넣을 값) / Large·Medium·Small Grid / Ticks per Beat, Beats per Measure |
| F9 Options | With / Without Percussion / Audio Feedback / MIDI Input (미구현) |

- 위쪽 행: 더블클릭(또는 Insert)으로 Display에서 고른 트랙에 이벤트 넣기 (Instrument는 악기 선택), 오른쪽 클릭으로 지우기
- 새 곡 기본 악기: 1~6 `piano1`, BD `bdrum`, SD `snare1`, TO `tom1`, CY `cymbal1`, HH `hihat1` (STANDARD.BNK에 `hihat`이 없어 `hihat1`)

**뱅크 브라우저** (`File → Instrument Bank File...`, Ctrl+B — 현대식 UI)
- 왼쪽: 폴더의 BNK 목록 (악기 수, 현재 곡 악기가 몇 개 들어 있는지). 처음엔 곡 악기가 가장 많은 뱅크를 자동 선택
- 오른쪽: 악기 목록 (검색 Ctrl+F, 종류 필터, 곡에서 쓰는지 ♪, 현재 뱅크에 있는지 ✓), 아래에 파라미터 요약
- 시음: Preview 버튼(누르는 동안), 더블클릭 또는 Space
- `Use this bank`: 그 뱅크를 현재 뱅크로 / `Add selected to current bank`: 고른 악기만 복사 (이름이 겹치면 덮어쓰기 또는 새 이름)

**Composer**
| 조작 | 동작 |
|---|---|
| 좌상단 `✎` / `I` | 연필(입력) / 선택 도구 |
| `1~6, BD, SD, TO, CY, HH, All` | 보이스 선택 (리듬 모드). 멜로딕 모드는 `1~9` |
| 연필: 클릭/드래그 | 노트 추가(드래그로 길이). 기존 노트를 클릭하면 삭제 |
| 선택: 클릭/드래그 | 선택/이동/오른쪽 끝 드래그로 길이 변경, Ctrl+클릭 다중 선택 |
| 오른쪽 클릭 | 노트 삭제 |
| 상단 행 더블클릭 (또는 Insert) / 오른쪽 클릭 | Display에서 고른 트랙(Tempo/Instrument/Volume/Pitch)에 이벤트 넣기 / 지우기 |
| 왼쪽 건반 클릭 | 현재 보이스 악기로 시음 |
| 휠 / Shift+휠 / Ctrl+휠 | 세로 스크롤 / 가로 스크롤 / 줌 |
| Space / Home / Esc | 커서부터 재생·정지 / 처음부터 / 정지 |
| Ctrl+Z, Y, A, C, V, Delete | 실행취소, 다시실행, 전체선택, 복사, 붙여넣기, 삭제 |

**Instrument Maker**
- 왼쪽 Carrier / 오른쪽 Modulator, `F1` / `F2`로 열 전환
- 슬라이더(◁ ▷ 클릭, 드래그, 방향키, 휠) + 체크박스, 변경 즉시 소리에 반영
- 하단 건반 클릭 또는 `Z S X D C V G B H N J M ,` / `Q 2 W 3 E R 5 T 6 Y 7 U I`로 시음, `PageUp/PageDown` 옥타브
- 메뉴 `F5 File`, `F6 Option` (원본 구성): Transpose Keyboard Up/Down, Additive Synthesis,
  Melodic / Bass Drum / Snare Drum / Tom / Cymbal / Hi-hat Mode, MIDI Input(미구현)
- 모드는 악기에 저장된다 (BNK `iPercussive`/`iVoiceNum`). 악기를 고르면 저장된 모드로 자동 전환
- Snare/Tom/Cymbal/Hi-hat 모드는 단일 오퍼레이터라 **Carrier 열만** 보인다. 값은 파일의 모듈레이터 필드에 저장된다
  (STANDARD.BNK의 snare1/tom1/cymbal1/hihat1에서 실제 값이 모듈레이터 쪽에 있고 캐리어 쪽은 bdrum과 같은 남은 값인 것으로 확인)
- Bass Drum 모드는 두 열(Carrier + Modulator)이 보인다 — 원본과 같음 (OPL도 두 오퍼레이터를 쓰고, bdrum/bdrum1도 두 쪽 모두 실제 값)
- `List...`로 뱅크 안 악기 선택/추가/복사/이름변경/삭제

## 구조

| 프로젝트 | 역할 |
|---|---|
| `AdlibComposer.Core` | 모델: `Song`(틱 기반 ROL 구조), `VoiceTrack`, `Note`, `AdlibInstrument`, `InstrumentBank`, `TempoMap` |
| `AdlibComposer.Formats` | `RolFormat`, `ImsFormat`, `BnkFormat`, `InsFormat`, `FormatRegistry`, `Planned/` (SOP 자리표시) |
| `AdlibComposer.Audio` | `Opl2Emulator`(내장 OPL2), `OplSynth`(보이스→레지스터, 리듬 모드), `Sequencer`, `AudioEngine`, `WaveOutPlayer`, `OplMapping` |
| `AdlibComposer.Notation` | `Score` 모델, `ScoreBuilder`(Song→마디/음표, 붙임줄), `IScoreExporter`, 텍스트 내보내기 예제 |
| `AdlibComposer.App` | WinForms GUI: `MainForm`, `ComposerView`, `InstrumentView`, `PianoRollControl`, `RetroSlider` 등 |
| `tests/AdlibComposer.SelfTest` | 의존성 없는 자체 테스트 + 데모 WAV 렌더러 |

## 확장 방법

**곡 포맷 추가 (SOP 등)**
1. `ISongFormat`을 구현한 클래스를 만든다 (실제 예: `Formats/ImsFormat.cs`, 뼈대: `Formats/Planned/PlannedSongFormats.cs`).
2. 파일 내용을 `Song`(보이스별 `Note`, 악기/볼륨/피치/템포 이벤트)으로 변환한다.
3. `FormatRegistry.CreateDefault()`에 `r.Songs.Add(new SopFormat());` 한 줄 추가.
→ 열기/저장 대화상자, 편집, 재생, 악보 변환이 그대로 동작한다.

**악보 변환**
- `ScoreBuilder.Build(song, options)`가 `Song`을 마디/음표 구조(`Score`)로 바꾼다 (마디 경계 분할, 붙임줄, 쉼표, 양자화).
- 내보내기 형식은 `IScoreExporter`를 구현해 추가한다 (예: MusicXML, MIDI, 이미지). 현재는 텍스트 예제만 있다 (`File → Export Score (text)...`).

**OPL 칩 교체**: `IOplChip`를 구현(예: Nuked-OPL3 C# 포팅)하고 `new OplSynth(chip)`으로 주입.

**악기 ↔ 레지스터 규칙 수정**: 모두 `OplMapping`과 `OplSynth.LoadVoice`에 모여 있다.

## 검증 상태

- 확인함: Core / Formats / Audio / Notation 빌드, 자체 테스트 통과 (BNK·INS·ROL 왕복 저장, 바이트 동일 재저장,
  OPL 음높이(ROL 69 = 440Hz), 리듬 모드 5종 발음, 시퀀서 타이밍과 템포 배수, 악보 변환)
- 확인함: 실제 샘플(`_incoming/`: STANDARD.BNK 2890개, BLUE-C#2.BNK, WZ-GAME1.BNK, INS 4개, AXEL-F·FOR_LADY·WZ-GAME1.ROL)
  → 파일 끝까지 정상 파싱, 재저장 시 이벤트·악기 값 보존, INS 바이트 동일, ROL 크기 동일.
  `_incoming/`이 있으면 자체 테스트가 자동으로 검사한다.
- 확인함: IMS — `WZ-GAME1.IMS`를 읽은 결과가 같은 곡의 `WZ-GAME1.ROL`과 노트·악기·피치·볼륨·템포까지 일치, IMS 재저장 후 같은 곡.
- 확인함: 재생 시작 지연 ≈ 50~60ms (출력 버퍼 6×512 샘플), 커서는 장치가 실제 재생 중인 샘플 위치(`waveOutGetPosition`)를 따라간다
  (`-- --latency 곡 뱅크.bnk`로 측정) 구조 덤프: `-- --probe 파일...`,
  실제 곡 렌더링: `-- --render 곡.rol 뱅크.bnk out.wav [초]`
- **확인하지 못함**: WinForms GUI는 .NET Framework 참조 어셈블리로 컴파일 검사만 했고 **실제로 실행해 보지 못했다**
  → 첫 실행에서 배치 어긋남이나 예외가 있을 수 있다.
- **확인하지 못함**: 실제 곡 렌더링 결과를 원본 Visual Composer 재생과 귀로 비교하지는 않았다

## 가정 / 확인 필요 항목

| 항목 | 현재 가정 | 확인 방법 |
|---|---|---|
| ~~Output Level 방향~~ | **확인됨**: 파일 값 = OPL TL 레지스터(0 = 최대). 실제 BNK 캐리어 대부분이 0. 화면 Output Level = 63 − 파일 값 | — |
| ~~노트 번호~~ | **확인됨**: ROL 노트 = MIDI 번호, 60 = 가운데 도 (실제 곡 음역 + adlib.c `MID_C 60`, 칩 노트 = ROL − 12) | — |
| Sustain Level 방향 | 파일 값을 레지스터에 그대로 사용 (0 = 최대) | 실제 곡과 비교 |
| ~~Connection(FM/가산)~~ | **확인됨**: 원본 Option 메뉴의 Additive Synthesis (파일 값 0 = 가산) | — |
| 피치 이벤트 범위 | 0.0~2.0 → ±1반음 (adlib.c 기본 pitchRange, `Options → Pitch Bend Range`) | 원본 재생과 비교 |
| 리듬 모드 탐탐 초기 음높이 | ROL 36 (adlib.c `TOM_PITCH` 칩 노트 24) | 원본 재생과 비교 |
| `B` 상자 / `✎`·`I` 도구 | `B`는 커서 아래 음 이름으로 해석, 도구는 입력/선택으로 해석 | 원본 동작 |
| OPL 엔벨로프 타이밍 | 데이터시트 시간 곡선 근사 (실제 칩과 완전 일치하지 않음) | 필요 시 칩 교체 |
| ROL 기본값 | 틱/박 48, 박/마디 4, scaleX/Y 1 (실제 곡은 틱/박 3·4·12로 훨씬 작다) | 원본 파일 헤더 |

## 알려진 제한

- 재생은 시작 시점의 곡을 기준으로 이벤트를 만든다 (재생 중 편집은 다음 재생부터 반영).
- 재생 중에는 시음이 꺼진다.
- 한 보이스는 동시에 한 음만 낸다 (OPL 제약). 겹치는 노트는 자동으로 잘리거나 밀린다.
- BNK의 쓰이지 않는 필드(캐리어 feedback/con 등)에는 0~255 쓰레기 값이 흔하다. 값은 그대로 보존하고 재생 시 비트 마스킹한다.
- ROL 트랙 이름·서명 필드의 NULL 뒤 쓰레기 바이트(예: WZ-GAME1.ROL 서명 칸의 제목 문자열)는 보존하지 않는다.
- BNK 재저장 시 데이터 영역이 이름순으로 다시 배치되고, 범위를 벗어난 미사용 항목은 빠진다 (악기 값은 동일).
- IMS 저장은 박당 240틱(가능할 때)으로 쓰고, 볼륨은 노트 vel + `An` 이벤트로 나눠 쓴다. 원본과 바이트까지 같지는 않다 (같은 곡).
- IMS 제목(조합형 한글)은 읽지만 화면 제목은 파일명을 쓴다.
- SOP 곡 포맷, MusicXML/MIDI 내보내기는 아직 없다 (확장 지점만 준비됨).
