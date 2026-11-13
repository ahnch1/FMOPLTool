# AdLib Composer

AdLib Visual Composer(작곡)와 Instrument Maker(악기 편집)를 **한 프로그램**으로 재현하는 C# 솔루션.
BNK 악기를 보이스(채널)에 할당하고, 건반 악보에 노트를 배치하고, OPL2 에뮬레이션으로 재생한다.

- 파일: ROL·IMS(곡) / BNK(악기 뱅크) / INS(악기 1개) 읽기·쓰기
- 화면: 원본 스크린샷(640x400 흑백 DOS 화면) 레이아웃을 따른 WinForms GUI
- 확장: SOP 같은 곡 포맷, 악보(스코어) 변환, OPL 칩 교체를 위한 인터페이스 제공

버전: `Directory.Build.props`의 `<Version>` (현재 1.1.0). 주·부 번호(1.1, 1.2 ...)는 직접 정하고, 작업마다 마지막 번호를 올린다.

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

**실행 폴더 구성** (빌드하면 자동으로 만들어진다)

```
AdlibComposer.exe
FMOPLTool.cfg        ← 설정 (처음 실행할 때 기본값으로 생성, File → Preferences / Ctrl+, 로 변경)
bnk\STANDARD.BNK     ← 프로그램 기본 뱅크 (시작할 때 불러옴, 새 곡 기본 악기 piano1/bdrum/... 가 들어 있음)
rol\                 ← ROL 곡 폴더
ims\                 ← IMS 곡 폴더
```

`FMOPLTool.cfg` (텍스트, 경로는 실행 폴더 기준 상대 경로 또는 절대 경로):

```
RolFolder=rol
ImsFolder=ims
BankFolder=bnk
DefaultBank=bnk\STANDARD.BNK
LoadDefaultBank=true
```

- 곡 열기/저장 대화상자는 곡 형식에 맞는 폴더(ROL → rol, IMS → ims)에서 시작하고 왼쪽 목록에 두 폴더 바로가기가 있다. 뱅크·INS 대화상자와 뱅크 브라우저는 뱅크 폴더에서 시작한다
- 기본 뱅크 원본은 `src/AdlibComposer.App/Defaults/bnk/STANDARD.BNK`. 실행 폴더의 사본을 고쳐 저장하면 다시 빌드해도 덮어쓰지 않는다
- `_incoming/`(개인 샘플)은 저장소에서 제외된다. 있으면 자체 테스트가 실제 파일 검사를 하고, 없으면 건너뛴다

## 사용법

**화면 구성** (현대식 틀 + 원본 재현 페이지)

| 영역 | 내용 |
|---|---|
| 메뉴 / 툴바 | 원본 메뉴(F5~F10) + 툴바(새 곡 · 열기 · 저장 · 뱅크 브라우저 · 재생 · 정지 · 설정) |
| 왼쪽 사이드바 | **Composer** · **Instrument Maker** (CREATE), **Jukebox** (PLAY), **Note** (SCORE). Ctrl+1~4 |
| 페이지 머리글 | 현재 페이지 이름과 설명 |
| 진행 로그 | 시작 · 뱅크/곡 열기·저장 · 경고 · 오류 기록. 필터(전체/정보/경고/오류), 상세(예외 내용), 복사, 지우기. 위쪽 경계를 끌어 높이 조절 |
| 상태 표시줄 | 곡 · 템포 · 커서 · 뱅크, `FMOPLTool.cfg` / 실행 폴더 바로 열기 |

**Note** (오선 악보)
- 보이스마다 오선: 평균 음높이로 높은음/낮은음자리표, 리듬 모드 드럼(BD SD TO CY HH)은 한 줄 타악기 보표 (CY/HH는 X 머리)
- 음 길이를 그릴 수 있는 음표로 나눠 붙임줄로 잇는다 (점음표, 박을 넘는 음, 마디를 넘는 음). 보통 음표로 안 되면 셋잇단음표 (예: 박당 3틱 곡)
- 올림표(#)와 마디 안 제자리표, 덧줄, 쉼표, 박자표, 마디 번호. 같은 박은 모든 보이스에서 같은 위치
- 툴바: 보이스 고르기, 양자화(끄기 / 1/4 ~ 1/32), 확대(− + 또는 Ctrl+휠), MusicXML · MIDI · PNG 내보내기, 인쇄 미리보기
- 음표에 마우스를 올리면 음 이름·마디·박, 클릭하면 Composer의 그 보이스·위치로 가서 그 노트를 선택
- MusicXML: 보이스 = 파트, divisions = 박당 틱, 셋잇단·붙임줄·타악기 보표 포함 (MuseScore 등에서 열기)
- MIDI: 곡에서 바로 (틱 그대로), PPQ = 박당 틱, 템포 변화, 볼륨 → 세기, 피치 → 피치벤드, 드럼은 채널 10 GM 음(BD 36 SD 38 TO 45 CY 49 HH 42). 숨긴 보이스는 빠진다
- 아직 없음: 빔(꼬리를 묶는 가로줄), 화음(한 보이스는 한 음뿐이라 필요 없음), 오른쪽 끝 맞춤

**Jukebox** (곡 재생기, 옛 IMPLAY / OCPLAY 화면을 참고한 현대식 데크)
- 왼쪽 위: 폴더 트리 (ROL 폴더 · IMS 폴더 · 내 PC 드라이브), 왼쪽 아래: 고른 폴더의 ROL / IMS 목록 (가사 = 같은 이름 .ISS, 뱅크 = 같은 이름 .BNK 표시). 두 번 클릭 / Enter로 재생
- 정보: 곡 제목(IMS 제목, 조합형 → 유니코드), 곡 파일 (불러온 악기 / 곡이 쓰는 악기), 뱅크 파일 (악기 수), 가사 파일, 지금 / 전체 시간, ♩ 템포 · 속도 · 키
- 뱅크: 곡 옆에 같은 이름의 BNK가 있으면 그것을 먼저, 없으면 설정의 기본 뱅크(STANDARD.BNK). Composer의 작업 뱅크는 바꾸지 않는다
- 가사: 같은 이름의 ISS. 조합형(원래 형식)과 완성형(CP949로 바꾼 파일)을 자동 판별. IMPLAY 규칙대로 기록 순서로 글자를 칠하고(카라오케), 다음 줄을 아래에 미리 보여 준다
- 재생 제어: 이전 곡 · 뒤로 5초 · 재생/일시정지 · 정지 · 앞으로 5초 · 다음 곡, 진행 막대(클릭 / 끌어서 이동), 다음 곡 자동 재생
- 속도 50~200% (느리게 / 빠르게), 키 ±12 반음 (♂ 남성 = 낮게 / 여성 ♀ = 높게, 드럼은 그대로), 볼륨 0~150%, 초기화
- 채널 믹서: 채널마다 연주 중 표시등 · 악기 이름(뱅크에 없으면 빨간 취소선, 안 쓰는 채널은 흐리게) · 지금 음 · 음량 막대 · 켜기/끄기(오른쪽 클릭 = 솔로) · 음높이(반음). 높이가 모자라면 두 열로
- 키: Space 재생/일시정지, Esc 정지, ← → 5초 (Ctrl+← → 어디서나), PageUp/PageDown 이전/다음 곡, + − 볼륨
- 다른 페이지로 가면 일시정지 (오디오 엔진을 Composer와 같이 쓴다). 돌아와서 Space로 이어 듣기
- 엔진: `PlaybackOptions`(속도 · 키 · 채널 끄기 · 채널 음높이 · 출력 크기)를 `AudioEngine.Play(..., options)`로 넘기고 `SetSpeed / SetVoiceMute / ...`로 재생 중에 바꾼다. 채널 상태는 `AudioEngine.GetVoiceStates`

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
| 건반 키 `Z S X D C V G B H N J M ,` / `Q 2 W 3 E R 5 T 6 Y 7 U I` | 현재 보이스 악기로 소리. 연필 도구면 커서에 Length 길이 노트를 넣고 커서 이동 (넣은 노트는 선택됨 → Shift+→로 바로 늘리기), 선택 도구면 소리만 |
| ← → / ↑ ↓ | 커서 상자 이동: 시간(Length 단위) / 음높이(반음) |
| **Shift+→ / Shift+←** | 커서 자리에 노트 그리기·늘리기 / 줄이기 (선택된 노트가 있으면 그 노트) |
| **Shift+↑ / Shift+↓** | 그 노트의 음높이를 반음씩 |
| `[` / `]` | Length 짧게 / 길게 (1, 1/2, 1/4, 1/8, 1/16, 1/32, 1/64 음표, 1틱). 위쪽 Length 버튼과 같음 = 마우스 스냅 |
| Esc | 선택 해제 (재생 중이면 정지) |
| PageUp / PageDown | 건반 키 옥타브 (왼쪽 건반의 파란 줄 = 키보드가 덮는 범위) |
| BD / SD / TO / CY / HH 보이스 선택 | 건반 키와 커서가 드럼 음역(C2 = 36)으로 이동. 드럼은 낮은 음에서 드럼 소리가 난다 (bdrum을 C4로 치면 짧은 음정 소리) |
| Ctrl+← → | 커서를 마디 단위로 이동 |
| Enter / Backspace | 쉼표(커서만 앞으로) / 커서를 뒤로 + 그 자리 노트 삭제 |
| End | 곡 끝으로 |
| Tab / Shift+Tab | 다음 / 이전 보이스 |
| Display → Large / Medium / Small Grid | 가로(박 폭 80/40/20px)와 세로(건반 높이 12/8/5px)를 함께 확대·축소 |
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
- 드럼 모드(Bass Drum ~ Hi-hat)는 건반이 C2부터 시작한다 (멜로딕으로 돌아오면 이전 옥타브)
- Bass Drum 모드는 두 열(Carrier + Modulator)이 보인다 — 원본과 같음 (OPL도 두 오퍼레이터를 쓰고, bdrum/bdrum1도 두 쪽 모두 실제 값)
- `List...`로 뱅크 안 악기 선택/추가/복사/이름변경/삭제

## 구조

| 프로젝트 | 역할 |
|---|---|
| `AdlibComposer.Core` | 모델: `Song`(틱 기반 ROL 구조), `VoiceTrack`, `Note`, `AdlibInstrument`, `InstrumentBank`, `TempoMap` |
| `AdlibComposer.Formats` | `RolFormat`, `ImsFormat`, `BnkFormat`, `InsFormat`, `IssLyrics`(ISS 가사), `KoreanText`(조합형/완성형 → 유니코드), `FormatRegistry`, `Planned/` (SOP 자리표시) |
| `AdlibComposer.Audio` | `Opl2Emulator`(내장 OPL2), `OplSynth`(보이스→레지스터, 리듬 모드), `Sequencer`, `AudioEngine`, `PlaybackOptions`(속도 · 키 · 채널 조절), `WaveOutPlayer`, `OplMapping` |
| `AdlibComposer.Notation` | `Score` 모델, `ScoreBuilder`(Song→마디/음표, 붙임줄), `NoteSpelling`(음표 나누기·셋잇단·음 이름), `MusicXmlExporter`, `MidiExporter`, 텍스트 내보내기 |
| `AdlibComposer.App` | WinForms GUI: `MainForm`, `Shell/`(사이드바 · 로그 · 셸 테마), `ComposerView`, `InstrumentView`, `JukeboxView`, `NoteView`, `PianoRollControl`, `RetroSlider` 등 |
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
