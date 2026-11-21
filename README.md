# AdLib Composer

AdLib Visual Composer(작곡)와 Instrument Maker(악기 편집)를 **한 프로그램**으로 재현하는 C# 솔루션.
BNK 악기를 보이스(채널)에 할당하고, 건반 악보에 노트를 배치하고, OPL2 에뮬레이션으로 재생한다.

- 파일: ROL·IMS(곡) / BNK(악기 뱅크) / INS(악기 1개) 읽기·쓰기, SOP(OPL3 곡) 읽기·쓰기·재생 (Composer SOP 모드 작곡, Jukebox), STM · S3M(Scream Tracker 2/3) · MOD · NST(ProTracker / NoiseTracker) 만들기·읽기·쓰기·재생, VGM · VGZ 재생 · 내보내기 (칩 기록: PSG · SCC · OPL · SN76489 · YM2413 · YM2151 · YM2203 · YM2608 · YM2612 · YM2610 · OKIM6295 · OKIM6258 · SAA1099 · GB DMG · 2A03 · HuC6280 · POKEY), S98 (PC-88 · PC-98) · KSS (MSX · 세가, Z80으로 드라이버 실행) · NSF (NES, 6502) · HES (PC엔진, HuC6280) · GBS · GBR (게임보이, LR35902) · MDX + PDX (X68000 MXDRV) · SPC (슈퍼 패미컴, SPC700 + S-DSP) · hoot PC-98 DOS · PC-98 (DOS 없음, pc98vx) · PC/AT DOS · MSX 범용 드라이버 · PC-8801 · X1 · 네오지오 · 타이토 YM2610 보드 · CPS-1 · Double Dragon 2 · 3 (YM2151 + OKIM6295) · 세가 System 1 · 아이렘 M72 계열 · 데이터 이스트 HuC6280 보드 · 잘레코 Mega System 1 · 세가 System 16A · 16B · 캡콤 CPS-2 (QSound) · 세가 PCM 보드 (Out Run · X · Y · Hang-On) · 메가드라이브 (Z80 드라이버) · X68000 (MC68000) 항목 (x86 + MS-DOS + PC-98 / PC/AT 흉내로 PMD · AdLib · SB16 등 원래 드라이버 실행) · IMF · HSC · CMF · DRO · RAW 재생, PC-88 · PC-98 디스크 이미지(D88 · FDI · HDM) 파일 꺼내기, 같은 곡 찾기 · 정리 (ROL / IMS / SOP)
- 화면: 원본 스크린샷(640x400 흑백 DOS 화면) 레이아웃을 따른 WinForms GUI
- 확장: 곡 포맷, 악보(스코어) 변환, OPL 칩 교체를 위한 인터페이스 제공

버전: `Directory.Build.props`의 `<Version>` (현재 2.1.20). 주·부 번호(1.1, 1.2 ...)는 직접 정하고, 작업마다 마지막 번호를 올린다. hoot 재생(PC-98 · PC/AT DOS 흉내 · MIDI 판 · 트리) 작업 현황과 다음 할 일은 [docs/hoot-status.md](docs/hoot-status.md).

## 빌드 / 실행

필요: **Visual Studio 2022 17.8 이상** + **.NET 8 SDK** (Windows)

1. `FMOPLTools.sln` 열기
2. 시작 프로젝트를 `AdlibComposer.App`으로 지정하고 F5
3. `samples/demo.rol` + `samples/demo.bnk`로 동작 확인 (곡을 열면 같은 폴더의 BNK를 자동으로 제안)

자체 테스트 (Windows/Linux/macOS 모두 가능, 외부 패키지 없음):

```
dotnet run --project tests/AdlibComposer.SelfTest
dotnet run --project tests/AdlibComposer.SelfTest -- --wav demo.wav   # 데모 곡을 WAV로 렌더링
```

외부 NuGet 패키지를 쓰지 않는다. 플랫폼 코드는 `AdlibComposer.Audio.Windows` 하나에만 있다 (오디오 출력 winmm `waveOut`, Windows MIDI `midiOut`, MP3 등 Media Foundation). 앱 · 자체 테스트는 시작할 때 `WindowsAudio.Register()`로 `AudioPlatform`에 끼운다. 다른 플랫폼은 `IAudioOutput` · `IMidiOutDevice` · 디코더를 구현해 끼우면 된다.

**실행 폴더 구성** (빌드하면 자동으로 만들어진다)

```
FMOPLTools.exe
FMOPLTool.cfg        ← 설정 (처음 실행할 때 기본값으로 생성, File → Preferences / Ctrl+, 로 변경)
resource\            ← 프로그램 리소스 (기본 뱅크 폴더 · MT-32 ROM 폴더의 기본값)
  STANDARD.BNK       ← 프로그램 기본 뱅크 (시작할 때 불러옴, 새 곡 기본 악기 piano1/bdrum/... 가 들어 있음)
  GENMIDI.OP2        ← MIDI FM용 GM 뱅크 (Freedoom, BSD 3-Clause, GENMIDI.OP2.LICENSE.txt)
  systems.json       ← 시스템 정의 (기계별 CPU · 칩 · 클럭 · 포트 / 메모리 배치: KSS의 MSX · 세가, NSF의 nes, HES의 pce, IMF 속도). 같은 id는 기본값을 덮어쓴다
  (MT-32 ROM)        ← 사용자가 넣는 Roland MT-32 컨트롤 + PCM ROM
```

곡 폴더는 미리 정하지 않는다: 왼쪽 목록의 **폴더 열기**(또는 폴더를 창에 끌어다 놓기)로 연 폴더를 `BrowserFolder`에 기억해 다음 실행 때 다시 연다.

`FMOPLTool.cfg` (텍스트, 경로는 실행 폴더 기준 상대 경로 또는 절대 경로):

```
BrowserFolder=D:\Musicdlib
BankFolder=resource
DefaultBank=resource\STANDARD.BNK
LoadDefaultBank=true
OplEmulator=builtin
JukeboxTone=standard
JukeboxSpeaker=default
MidiSynth=fm
MidiBank=
MidiModule=gm
Mt32RomFolder=resource
```

- `MidiSynth`: `fm`(OPL3 FM) / `windows`(Windows MIDI 신디사이저) / `psg`(8-bit PSG) / `chip`(8-bit PSG + SCC) / `fmpac`(MSX FM-PAC) / `pce`(PC엔진) / `nes`(NES) / `md`(메가드라이브) / `gb`(게임보이) / `snes`(슈퍼패미컴). `MidiBank`: FM용 GM 뱅크 (DMX `GENMIDI.OP2` 또는 BNK). 비우면 뱅크 폴더 또는 `resource` 폴더의 `GENMIDI.OP2`(Freedoom 프로젝트의 GM 뱅크를 함께 배포, BSD 3-Clause, `resource/GENMIDI.OP2.LICENSE.txt`), 없으면 기본 뱅크(STANDARD.BNK)에서 GM 번호마다 비슷한 이름의 악기를 골라 쓴다
- `MidiModule`: `gm` / `mt32`. MT-32이면 Munt 에뮬레이터(`mt32emu.dll`, LGPL — `src/AdlibComposer.Mt32/build_mt32emu.bat`로 빌드)와 `Mt32RomFolder`의 Roland ROM(컨트롤 + PCM, 직접 준비)으로 재생하고 SysEx(사용자 음색)도 보낸다. DLL · ROM이 없으면 MT-32 음색 번호를 GM에 맞춰(`GeneralMidi.Mt32ToGm`) FM / Windows로 낸다
- `OplEmulator`: `builtin`(내장 간이 에뮬레이터) / `nuked`(Nuked OPL3, 사이클 정밀). File → Preferences → Sound에서 바꾸며 프로그램 전체(Composer · Instrument Maker 시음 · Jukebox · S3M AdLib 채널)에 바로 적용된다
- `VoiceColors`: 보이스 1~11의 색 (`#RRGGBB`를 쉼표로, 리듬 모드 7~11 = BD SD TO CY HH). Composer 노트 막대 · Note 음표 · Jukebox 채널(믹서 선택 칸 · 건반)이 같이 쓴다. File → Preferences → Voice colors에서 바꾸면 바로 적용, 채널이 더 많은 곡(SOP · MIDI · S3M)은 돌려 쓴다

- 곡 열기/저장 대화상자는 곡 형식에 맞는 폴더(ROL → rol, IMS → ims)에서 시작하고 왼쪽 목록에 두 폴더 바로가기가 있다. 뱅크·INS 대화상자와 뱅크 브라우저는 뱅크 폴더에서 시작한다
- 리소스 원본은 `src/AdlibComposer.App/Defaults/resource/` (STANDARD.BNK, GENMIDI.OP2). 예전 기본값(`bnk`, `bnk\STANDARD.BNK`, `mt32rom`)을 쓰던 설정 파일은 처음 실행할 때 `resource`로 바뀐다. 실행 폴더의 사본을 고쳐 저장하면 다시 빌드해도 덮어쓰지 않는다
- `_incoming/`(개인 샘플)은 저장소에서 제외된다. 있으면 자체 테스트가 실제 파일 검사를 하고, 없으면 건너뛴다

## 사용법

**화면 구성** (현대식 틀 + 원본 재현 페이지)

| 영역 | 내용 |
|---|---|
| 메뉴 / 툴바 | 원본 메뉴(F5~F10) + 툴바(새 곡 · 저장 · 뱅크 브라우저 · 재생 · 현재 위치부터 재생 · 정지 · 설정). 재생 버튼 셋은 Composer · Note · Orgel Note · Transcribe가 같이 쓴다 (지금 페이지를 재생) |
| 페이지 메뉴 (메뉴와 툴바 사이, 가로) | **Jukebox** · **Hoot** (PLAY), **Composer** · **Instrument Maker** · **Tracker** (CREATE), **Note** · **Orgel Note** · **Transcribe** (SCORE), **Organizer** (TOOLS) — 곡을 듣고 → 고치고 → 악보로 가는 순서. 처음 여는 페이지 = Jukebox. Ctrl+1~9 (Jukebox = Ctrl+1, Composer = Ctrl+2, Instrument Maker = Ctrl+3, Note = Ctrl+4, Orgel Note = Ctrl+5, Tracker = Ctrl+6, Organizer = Ctrl+7, Transcribe = Ctrl+8, Hoot = Ctrl+9) |
| 왼쪽 폴더 / 곡 목록 | 모든 페이지가 같이 쓴다. **폴더 열기**로 고른 폴더와 그 아래 폴더만 오가는 목록 하나 (`..` · 폴더 · 곡: ROL · IMS · SOP · S3M · STM · MOD · NST · MID · WRK · XMI · MDI · MUS · VGM · VGZ · S98 · KSS · NSF · HES · GBS · GBR · MDX · SPC · HOOT · IMF · WLF · HSC · CMF · DRO · RAW · MP3 · WAV …), **위로** / Backspace. **찾기** 칸 (경로 줄 아래): 파일 이름 · 곡 안 제목(목록의 '제목' 칸과 같은 것, 곡이 여럿 든 파일은 파일 안 곡 제목도 — HES · GBS는 곡 목록을 이미 알 때만)에 띄어 쓴 낱말이 모두 든 곡을 지금 폴더와 그 아래 폴더에서 찾는다 (Enter, 뒤에서 찾으며 결과가 차례로 들어오고 경로 줄에 "n곡 (파일 m개 중)"). 파일은 안 맞고 파일 안 곡 제목만 맞으면 그 곡들을 파일 아래 곡 줄로. 결과 목록에서 두 번 클릭 / Enter = 보통 목록과 같다, 결과가 Jukebox 다음 곡 목록. Esc · 위로 · Backspace = 폴더 목록으로, ↓ = 결과로. zip은 들어가 있을 때 그 안만 찾는다. 곡이 많으면 세로 스크롤바 + 아래 ◀ ▶로 한 쪽씩 (n / m 쪽). 곡 두 번 클릭 / Enter: 곡은 모두 Jukebox를 거친다 — Jukebox = 재생(다음 곡 = 그 폴더), Composer · Note · Orgel Note(ROL · IMS · SOP) · Tracker(S3M · STM · MOD · NST) = Jukebox에 재생 없이 올리고 그 페이지가 바로 불러옴, 그 밖의 페이지 · 형식 = Jukebox로 가서 재생, MP3 · WAV … = Transcribe. 곡이 여럿 든 파일(KSS · NSF · HES · GBS · GBR · HOOT, ▸ 표시)은 두 번 클릭 / → 로 곡마다 한 줄씩 펼치고 ← 로 접는다 — 곡 줄을 두 번 클릭하면 그 곡을 재생, 재생 중인 곡 줄은 굵게 (다음 곡으로 넘어가면 따라 펼친다). **ZIP**은 폴더처럼 들어간다 (두 번 클릭 · 창에 끌어다 놓기, 위로 / Backspace로 나옴): 처음 한 번 임시 폴더(%TEMP%\FMOPLTool\zip)에 풀어 안의 ROL · IMS · SOP · VGM · KSS · NSF … 를 그대로 열고, 같은 이름의 가사(ISS) · 뱅크(BNK) · m3u도 함께 쓴다. 이름은 UTF-8 / 옛 한글(완성형 · 조합형)을 알아보고, zip 밖으로 나가는 이름은 막는다. zip 안 곡을 고쳐 저장하면 다른 이름으로 저장을 묻는다 (zip은 바꾸지 않음). hoot 게임 목록은 **Hoot** 페이지로 옮겼다 (아래 줄). |
| Hoot 페이지 | **PLAY**의 Jukebox 옆 (Ctrl+9). 화면 · 위 막대 · 재생 제어는 Jukebox와 같은 것 (같은 `JukeboxView`를 하나 더, 재생 중 페이지를 바꾸면 멈춤), 왼쪽만 hoot 목록. hoot 곡은 가사가 없으므로 표시 방식 단추 (Implay · Ocplay · 노래방 · Cyberfunk)와 가사 상자를 빼고, 건반 위에 채널 믹서 (Implay, 왼쪽)와 흐르는 작은 악보 · 음량 막대 (Ocplay, 오른쪽)를 좌우로 함께 보인다 (v2.1.76). 왼쪽 위 **목록 고르기 상자** = 설정의 hoot 목록 폴더들 (xml · xml2 · xml_new …) 중 하나, **[설정]** = Preferences. 그 아래 경로 줄 "제작사 n · 게임 m (재생 p · 곡 없음 k)"와 **제작사 → 장비([PC-9801] 등) → 게임 → 곡** 트리 (뒤에서 읽음, 9,494게임 1초 남짓, 아래 단계는 펼칠 때). 찾기 칸 = 게임 이름 · 제작사 · 아카이브로 거르기 (Enter, Esc = 지우기), 게임 툴팁 = 드라이버 · 아카이브(찾은 경로) · 옵션 · 재생 방법. **처음 Hoot 페이지를 열 때 비로소** 목록을 읽고 zip 색인을 만든다 (프로그램 시작 때는 읽지 않음, 목록만 바꾸면 zip 색인은 그대로). 곡 두 번 클릭 / Enter: 설정의 **데이터 폴더** (비면 목록 폴더의 위 폴더)와 그 아래 모든 폴더에서 아카이브 zip을 찾아, 재생할 수 있는 항목이면 그 곡 재생 (다음 곡 = 같은 게임의 titlelist 순서, 제목 = hoot 목록의 것). 그 밖의 기종(회색)과 곡 파일이 zip에 없는 항목은 Jukebox 페이지로 가서 zip을 폴더 목록에서 연다 (안의 KSS · NSF · SPC … 는 Jukebox가 재생). 색: 기본 = 재생, 회색 = 아직 못 하는 기종, 빨강 = 곡 파일 없음, 묶음 줄 "(n · 재생 p · 없음 m)". 트리 오른쪽 클릭 = 없는 파일(zip) 목록 · 폴더 구성 CSV. 트리에 포커스가 있을 때 Space · Esc · ← → · PageUp/Down은 Jukebox와 같다. Composer · Note · Orgel Note는 마지막에 쓴 재생기(Jukebox 또는 Hoot)의 곡을 불러온다. 설정 (Preferences → **Hoot**): 목록 폴더들 (Add... · Remove · Up · Down, hoot 폴더 자체를 고르면 안의 xml* 폴더를 모두 더하고 데이터 폴더도 그 폴더로) · 데이터 폴더 — FMOPLTool.cfg의 HootXmlFolders (| 로 나눔) · HootFolder (마지막에 고른 목록) · HootDataFolder, 예전 HootFolder 하나만 있으면 목록 폴더 첫 칸으로 옮긴다. 진단: `SelfTest --hoottree xml폴더` |
| 끌어다 놓기 (왼쪽 목록 · Jukebox 페이지만) | 폴더 = 폴더 열기와 같다. zip = 왼쪽 목록에서 그 zip 안으로. 곡 파일 = Jukebox가 그 곡만 재생 (여러 곡이면 그것들이 다음 곡 목록). Composer 등 다른 페이지 위에 놓으면 받지 않는다 |
| 칩 곡 → 노트 | Jukebox의 칩 기록 곡을 Composer · Note · Orgel Note로 가져가면 `VgmToSong`이 곡을 소리 없이 끝까지 돌리며 한 틱(120 BPM · 박당 48틱 ≈ 10ms)마다 채널별 음(Jukebox 건반 표시와 같은 것)을 읽어 노트로 만든다: 음이 바뀌거나 다시 쳐지면 새 노트, 2틱보다 짧은 조각은 앞 노트에 붙임. 음높이가 있는 채널만 (노이즈 · PCM · 드럼 뺌), 채널 하나 = 보이스 하나 — 9개 이하면 멜로딕 9보이스, 넘으면 SOP 20보이스(노트가 많은 20개). 음색 · 세기 · 템포 변화는 옮기지 않음 (악기 = 보이스 기본 악기, 템포 120 고정 → Note의 양자화로 정리), 루프 곡은 한 번 더 돈 길이까지 · 최대 10분. 저장 안 한 새 곡 (저장 = ROL / SOP). 바꾸는 동안 진행 막대 · 남은 시간 · [취소] 창 (0.3초 안에 끝나면 안 보임, 취소하면 지금 곡 그대로). 예: Castlevania NSF 2분 곡 2.5초, 펄스 2 + 삼각파 = 3보이스 |
| 오류 기록 | 화면에서 처리하지 못한 오류는 프로그램을 끝내지 않고 진행 로그와 실행 폴더의 `FMOPLTool-error.log`에 호출 위치(스택)까지 남긴다. 진단: 환경 변수 `FMOPL_FIRSTCHANCE=1`이면 안에서 잡힌 범위 오류(ArgumentOutOfRange)까지 |
| 파일 열기 | 곡을 여는 곳은 왼쪽 폴더/파일 목록과 Jukebox(끌어다 놓기)뿐: Composer · Tracker · Transcribe의 열기(File → Open · 툴바 열기 · Ctrl+O · 음악 파일 열기)는 없앴다. **Composer · Note · Orgel Note**는 페이지로 갈 때 Jukebox에 올린 곡(ROL · IMS · SOP)을 — 칩 기록 곡(VGM · NSF · KSS · HES · GBS · S98 · SPC · hoot …)은 노트로 바꿔(아래 "칩 곡 → 노트") — **Tracker**는 트래커 곡을 작업 곡으로 불러온다 (Jukebox 곡이 바뀌었을 때만, 저장 안 한 변경은 먼저 물음; 열지 못하는 형식이면 지금 곡 그대로). 새 곡 · 저장 · 다른 이름으로 저장은 그대로 |
| 페이지 머리글 | 현재 페이지 이름과 설명 |
| 진행 로그 | 시작 · 뱅크/곡 열기·저장 · 경고 · 오류 기록. 필터(전체/정보/경고/오류), 상세(예외 내용), 복사, 지우기. 위쪽 경계를 끌어 높이 조절 |
| 상태 표시줄 | 곡 · 템포 · 커서 · 뱅크, `FMOPLTool.cfg` / 실행 폴더 바로 열기 |

**Note** (오선 악보)
- 맨 위 곡 전체 길이 막대: 지금 위치 시간 · 곡 전체 시간(곡의 템포대로). 막대를 누르거나 끌어 놓은 지점부터 재생
- 보이스마다 오선: 평균 음높이로 높은음/낮은음자리표, 리듬 모드 드럼(BD SD TO CY HH)은 한 줄 타악기 보표 (CY/HH는 X 머리)
- 음 길이를 그릴 수 있는 음표로 나눠 붙임줄로 잇는다 (점음표, 박을 넘는 음, 마디를 넘는 음). 보통 음표로 안 되면 셋잇단음표 (예: 박당 3틱 곡)
- 올림표(#)와 마디 안 제자리표, 덧줄, 쉼표, 박자표, 마디 번호. 같은 박은 모든 보이스에서 같은 위치
- 악보 오른쪽: 보이스 체크 목록 (모두 켜기 / 모두 끄기)
- 툴바: 양자화(끄기 / 1/4 ~ 1/32), 확대(− + 또는 Ctrl+휠), MusicXML · MIDI · PNG 내보내기, 인쇄 미리보기
- 음표에 마우스를 올리면 음 이름·마디·박. 클릭 = 현재 위치(파란 선), 음표 두 번 클릭 = Composer의 그 보이스·위치로 가서 그 노트를 선택
- 재생 = 툴바의 재생(처음부터) · 현재 위치부터 재생 · 정지 (Space = 재생 / 정지, Esc = 정지). 보이는 보이스만 소리 낸다. 재생 위치(초록 선)를 따라 스크롤, 멈추면 멈춘 곳이 현재 위치
- MusicXML: 보이스 = 파트, divisions = 박당 틱, 셋잇단·붙임줄·타악기 보표 포함 (MuseScore 등에서 열기)
- MIDI: 곡에서 바로 (틱 그대로), PPQ = 박당 틱, 템포 변화, 볼륨 → 세기, 피치 → 피치벤드, 드럼은 채널 10 GM 음(BD 36 SD 38 TO 45 CY 49 HH 42). 숨긴 보이스는 빠진다
- 아직 없음: 빔(꼬리를 묶는 가로줄), 화음(한 보이스는 한 음뿐이라 필요 없음), 오른쪽 끝 맞춤

**Tracker** (Scream Tracker 2/3 편집기 + MOD / NST, Ctrl+6 — 화면과 키는 ST3를 따른다)
- 새 곡: S3M(ST3, 32채널 · AdLib A1~A9, 처음 8채널 L1 R1 L2 R2 …) / STM(ST2, 4채널, 명령 A~J). 곡은 Jukebox에 올린 것 · 왼쪽 목록에서 고른 것을 불러오고, 저장은 확장자대로, 다른 형식으로 저장하면 바꿔서 쓰고 빠진 것을 알려 준다 (STM은 4채널 · 31악기 · 8bit · A~J)
- F1 주문·변수: 주문 목록(더블클릭/G/Enter = 그 패턴 편집, Ins/Del, +++ 건너뜀, --- 끝), 곡 이름 · 속도(A) · 템포(T) · 전역/마스터 볼륨 · 스테레오 · 빠른 볼륨 슬라이드 · 아미가 한계, 채널 32개 종류(L1~L8 R1~R8 A1~A9 --)와 팬
- F2 패턴: 한 칸 `C-5 04 12 H81` (음, 악기, 볼륨, 명령, 인포). 아랫줄 Z S X D C V G B H N J M = 기준 옥타브, 윗줄 Q 2 W 3 E R 5 T 6 Y 7 U I 9 O 0 P = 한 옥타브 위 (입력하면 소리), `/` `*` 옥타브, `.` 지우기, `1` ^^ 노트 오프, `<` `>` 악기, `+` `-` 패턴, `{` `}` 속도, Ins/Del(Shift = 모든 채널), Enter = 칸의 악기 가져오기, `4` 칸 듣기, `8` 줄 듣기, Shift+화살표/끌기 = 영역, Ctrl+C/X/V, Alt+Q/A = 반음 올림/내림, Alt+L 채널 선택, Ctrl+Z 되돌리기
- F3 악기: 목록(##, 이름, SMP/AMel, 볼륨, C4Spd, 길이, 루프), 이름 · 파일 이름 · 볼륨 · C4Spd · 루프, 파형, WAV 불러오기/저장, AdLib 파라미터 D00~D0B. 건반 키로 들어 보기
- 재생: F5 곡, F6 패턴 되풀이, Shift+F6 지금 주문부터, F7 커서부터, F8 정지. 따라가기(재생 위치를 따라 패턴 화면 이동)
- 재생기(`TrackerPlayer`): STM은 st2play(viiri/st2play)를 그대로 옮김(템포 바이트 · 주기 표 · FXMULT · LFO · 명령 A~J). S3M은 ST3 규칙(TECH.DOC 주기 공식 · 틱 = 2.5/템포초)과 ST3.DOC 명령 A B C D E F G H I J K L O Q R S(3 4 8 B C D E) T U V X, 볼륨 칸, 채널 팬, AdLib 멜로디 채널(OPL2). 샘플은 선형 보간
- MOD / NST (`ModFormat`): ProTracker · NoiseTracker (`M.K.` `M!K!` `4CHN` `6CHN` `8CHN` `xxCH` `FLT4/8` `CD81` `OKTA` `N.T.`, 서명 없는 15샘플 SoundTracker도). S3M 모델로 바꿔 열고 (채널 L R R L, 아미가 한계 켬, 주기 428 = ProTracker C-2 = ST3 C-4, 파인튠 → C4Spd), 명령은 S3M 명령으로 (`Cxx` = 볼륨 칸, `Dxx` = C, `Exy` = S 명령 · 미세 슬라이드, `Fxx` = A 속도 / T 템포). `.mod` / `.nst`로 저장하면 거꾸로 바꿔 쓰고 빠진 것을 알려 준다 (AdLib, 음역 밖 음, MOD에 없는 명령, 명령과 겹친 볼륨 칸, ^^는 C00으로). 채널은 실제로 쓰는 샘플 채널만 (최소 4, 짝수)
- 아직 없음: ST3 화음 모드 · 폴리채널 · 악기 라이브러리(F4) · 정보 화면(F5 화면), AdLib 드럼 채널, 샘플 편집(자르기 등), MOD 고유 재생 규칙 (MOD도 S3M 규칙으로 재생)

**Organizer** (같은 곡 찾기 · 정리, Ctrl+7)
- **Hoot 목록 비교** (Organizer의 [Hoot 목록 비교...] · File → Compare Hoot Game Lists...): hoot 게임 목록 폴더(xml · xml2 · xml3 …, 제작사마다 .xml 하나)를 읽어 같은 게임 · 같은 판끼리 묶는다. 묶음마다 있는지 · 곡 수 · 다른 점(이름 · 곡 · 옵션 · 롬 · 파일) · 남길 항목을 한 줄에, 고른 줄의 묶음별 XML · 곡 목록(코드 × 묶음, 다르면 색)을 나란히. 찾기 · 보기(중복 · 한 묶음만 · 다름 · 확인 권장) · 제작사로 거르기
  - 같은 항목: 같은 아카이브 안에서 드라이버 · 종류 · 장치 옵션(midiout · midiout_type · subtype)이 같고 칩이 맞으며 (OPN = YM2203, MT-32 Emulation = Munt …), 제목이 같거나 곡 코드가 80% 넘게 겹치거나 제목 · 곡이 절반 넘게 닮음 — 근거가 강한 짝부터 묶는다. 같은 파일 안 두 항목(OPLL / PSG, 데모 / 정식)은 일부러 나눈 판이라 묶지 않음. 섞기 · 클럭 같은 조정 옵션은 달라도 같음
  - 남김: 곡 목록 있음 → 새 이름 형식(일본어 제목 + 칩) → 새 묶음 → 곡 수 (다른 항목의 곡 목록이 1.5배 넘게 길면 그것)
  - [중복 없이 합치기]: 남긴 항목만 새 폴더(xml_new)에 제작사 파일로 (원래 바이트 그대로 Shift_JIS, 밑줄만 다른 제작사 파일은 하나로), hoot.xsl · hoot.css · _report.txt 함께, hoot.xml이 있으면 목록을 새 폴더로 바꾼 hoot_new.xml도 (hoot.xml은 그대로)
- 폴더(하위 폴더 포함 가능)의 ROL / IMS / SOP를 모두 읽어 같은 곡을 묶는다. 판정 (`MusicDuplicateFinder`):
  같은 바이트(크기 + SHA-1) · 같은 음표(형식 · 박당 틱 · 보이스 배치와 상관없이 모든 음의 시작 · 길이 · 음높이 — 같은 곡의 ROL과 IMS도 묶인다) ·
  곡 안 제목 + 악기 순서가 같고 음 개수 5% 안 · 형식 + 크기 + 악기 순서가 같고 음표 90% 이상 같음(조금 고친 판)
- 남길 파일 순서: **우선 폴더**(하위 폴더 포함, 위쪽 `우선 폴더` 칸 · 설정 `OrganizerPreferredFolder`에 저장) → 같은 이름 ISS 있음 → 같은 이름 BNK 있음 → 점수 → 음이 많은 것 → 짧은 이름.
  점수: 같은 이름 BNK +30, 같은 이름 ISS +30, 뜻 있는 파일 이름 +20, 숫자만 나열된 이름(예: `0001`, `SONG001`, `MUS00012`) −20, 곡 안 제목 +5
- ROL과 IMS가 같은 그룹이면 ROL · IMS를 각각 하나씩(형식마다 순위가 가장 높은 것) 남긴다
- 왼쪽: 파일 목록(파일 · 경로 · 형식 · 크기 · 제목 · 음 · 점수 · 판정), 같은 곡끼리 묶음 머리글(이유 · 남길 파일). `같은 곡만 보기`를 끄면 나머지도 보인다
- 오른쪽: 고른 파일 정보 (경로 · 크기 · 날짜 · BNK · ISS · SHA-1, 제목 · 템포 · 길이 · 보이스 · 음 · 악기 순서, 점수 내역 · 같은 그룹 파일)
- 아래: `추천대로 체크`(그룹마다 남길 파일 빼고 체크), `체크 해제`, `시뮬레이션`(기본 켬: 할 일 목록만 보여 주고 로그에 남김), `같은 이름 BNK · ISS도 함께`(같은 폴더에 같은 이름의 다른 곡이 남으면 그대로 둠), 삭제(휴지통) / 폴더로 이동(같은 이름이 있으면 `이름 (2).ROL`). 그룹 전체를 체크하면 경고. 삭제 · 이동할 때 그룹의 파일이 모두 체크돼 있으면 먼저 묻는다 (예 = 추천 파일은 남기고 진행 · 아니요 = 모두 진행 · 취소)

**Transcribe** (음악 파일 → 주 멜로디 악보, Ctrl+8)
- 왼쪽 목록에서 MP3 · WAV (그 밖에 WMA · M4A · AAC · FLAC은 Windows Media Foundation 코덱으로)를 고르면 이 PC에서 주 멜로디만 찾는다 (화음 · 반주는 보지 않음, 인터넷 · 외부 라이브러리 없음)
- 방식 (`MelodyAnalysis`, Melodia 방식): 22050Hz 모노 · 2048 Hann · 4096 FFT · 홉 11.6ms → 스펙트럼 봉우리 → 배음 합 살리언스(20센트 칸, A1~A6) → 비터비 추적(음역 안 + '멜로디 없음' 상태) → 음 나누기(반음 넘게 벗어나면 새 음, 같은 음은 어택에서 나눔, 짧은 음 버림) → 옥타브 오류 고치기(앞뒤 3초 · 곡 전체 음역에서 8반음 넘게 떨어진 음을 배음이 있는 쪽 옥타브로) → 뚜렷함 거르기(이 곡 음들의 가운데 값 대비, 감도로 조절). 템포 · 첫 박은 스펙트럼 플럭스의 자기상관으로
- 조절 (바꾸면 분석은 그대로 두고 곧바로 다시 뽑음): 음역(베이스 · 반주가 잡히면 아래쪽을 올림), 감도, 최소 길이, 템포(자동 / 직접), 격자(4분 · 8분 · 16분 · 없음)
- 피아노 롤: 멜로디 곡선(점) + 음(막대, 뚜렷할수록 진하게) + 박 격자. 클릭 = 그 위치, 휠 = 이동, Ctrl+휠 = 확대. 재생(툴바의 재생 · 현재 위치부터 재생 · 정지, Space)은 원곡 / 채보한 멜로디(간단한 소리)를 섞어 견줄 수 있다
- **악보 보기 (Note)** / **Composer에서 편집**: 채보한 멜로디를 새 곡의 보이스 1로 (템포 · 첫 박에 맞춰 격자에 붙임) → 오선 악보, MusicXML · MIDI · PNG, ROL 저장
- 한계: 한 음씩 뚜렷한 멜로디(노래 · 독주 악기)가 반주보다 클 때 잘 맞는다. 반주가 크거나 여러 악기가 같은 음역에서 겹치면 반주 음이 섞이고, 빠른 꾸밈음 · 미끄러지는 음은 줄어든다. 템포는 반이나 두 배로 잡힐 수 있다

**Orgel Note** (30음 오르골 종이 띠, Ctrl+5)
- 맨 위 곡 전체 길이 막대: 시간은 돌리는 빠르기대로. 막대를 누르거나 끌어 놓은 지점부터 시험 연주
- 지금 곡을 손으로 돌리는 30음 오르골 띠의 구멍 위치로 보여 준다. 시간은 왼쪽 → 오른쪽, 아래 줄이 낮은 음 (띠에 인쇄된 음계 표시를 180도 돌린 방향)
- 빗살 음계 (아래부터): C D G A B C₁ D₁ E₁ F₁ F♯₁ G₁ G♯₁ A₁ A♯₁ B₁ C₂ C♯₂ D₂ D♯₂ E₂ F₂ F♯₂ G₂ G♯₂ A₂ A♯₂ B₂ C₃ D₃ E₃. 음계 표시는 띠처럼 두 칸에 엇갈려, 6줄마다 굵은 선
- 구멍은 음의 시작점에만 (길이 · 쉼표는 구멍 없음). 템포 변화는 무시하고 틱 간격만 맞춘다 (손으로 돌리므로). 같은 시점 같은 음은 구멍 하나
- 조옮김 −/+ · 자동(옮기지 않고 들어가는 음이 가장 많은 값, 곡을 열면 자동), 옥타브 옮기기(빗살에 없는 음을 가까운 옥타브 줄로 = 파란 구멍, 끄면 띠 아래 빨간 ×), 빗살 C = C3/C4/C5(기준 음 · 시험 연주 음높이)
- 격자 1/4 · 1/8 · 1/16 (격자 한 칸 = 세로선 간격), 격자에 맞추기, 같은 줄 구멍이 한 칸보다 가까우면 주황 고리 (빗살이 다시 울릴 시간이 모자랄 수 있음)
- 보이스 고르기 = 띠 오른쪽 체크 목록 (모두 켜기 / 모두 끄기, 리듬 곡은 아래 `드럼도 넣기`, 드럼은 기본으로 뺀다). 구멍에 마우스 = 줄 이름 · 원래 음 · 마디/박
- 시험 연주 = 툴바의 재생 · 현재 위치부터 재생 · 정지 (Space = 재생 / 정지): 오르골 소리(빗살을 튕긴 소리, 저절로 사라짐)로 구멍만 연주. ♩ −/+ = 돌리는 속도. 띠를 클릭하면 현재 위치, 멈추면 멈춘 곳이 현재 위치
- PNG, 인쇄(실제 크기): 줄 간격 2mm · 격자 8mm로 가로 용지에 띠를 구간별로 싣고 "구간 n → n+1에 이어 붙이기" 표시. 실제 띠와 크기가 다르면 `OrgelView`의 `PrintRowPitchMm` / `PrintGridMm`

**Jukebox** (곡 재생기, 옛 IMPLAY / OCPLAY 화면을 참고한 현대식 데크)
- 곡 고르기: 창 왼쪽 공용 목록 (이름 · 곡 안 제목(IMS · SOP · S3M · STM · MOD · MIDI, 뒤에서 읽어 채움) · 형식 · 가사 · 뱅크) 또는 끌어다 놓기, ROL / IMS / SOP / S3M / STM (S3M/STM: 곡 안 샘플로 재생, 채널 믹서에 L1 R1 A1 …, 길이는 끝까지 빨리 돌려 잰다) (가사 = 같은 이름 .ISS, 뱅크 = 같은 이름 .BNK 표시). 두 번 클릭 / Enter로 재생
- 정보: 곡 제목(IMS 제목, 조합형 → 유니코드), 곡 파일 (불러온 악기 / 곡이 쓰는 악기), 뱅크 파일 (악기 수), 가사 파일, 지금 / 전체 시간, ♩ 템포 · 속도 · 키
- 뱅크: 곡 옆에 같은 이름의 BNK가 있으면 그것을 먼저, 없으면 설정의 기본 뱅크(STANDARD.BNK). Composer의 작업 뱅크는 바꾸지 않는다
- 가사: 같은 이름의 ISS. 조합형(원래 형식)과 완성형(CP949로 바꾼 파일)을 자동 판별. IMPLAY 규칙대로 기록 순서로 글자를 칠하고(카라오케), 다음 줄을 아래에 미리 보여 준다
- 재생 제어: 이전 곡 · 뒤로 5초 · 재생/일시정지 · 정지 · 앞으로 5초 · 다음 곡, 진행 막대(클릭 / 끌어서 이동), 다음 곡 자동 재생. 곡이 여럿 든 파일(NSF · KSS · HES · GBS · hoot · XMI …)이면 진행 막대 아래에 "파일 안 곡 n / m · 제목"과 이전 / 다음 버튼 (Shift+PageUp / Shift+PageDown) — 다음 곡 목록은 그대로
- 속도 50~200% (느리게 / 빠르게), 키 ±12 반음 (♂ 남성 = 낮게 / 여성 ♀ = 높게, 드럼은 그대로), 볼륨 0~150%, 초기화
- 채널 믹서: 채널마다 연주 중 표시등 · 악기 이름(뱅크에 없으면 빨간 취소선, 안 쓰는 채널은 흐리게) · 지금 음 · 음량 막대 · 켜기/끄기(오른쪽 클릭 = 솔로) · 음높이(반음). 높이가 모자라면 두 열로
- 키: Space 재생/일시정지, Esc 정지, ← → 5초 (Ctrl+← → 어디서나), PageUp/PageDown 이전/다음 곡, Shift+PageUp/PageDown 파일 안 이전/다음 곡, + − 볼륨
- 거친 음색: 끄면 '표준'(되먹임 1/8 줄임 + 칩 출력 12kHz 1-pole 로우패스, ROL/IMS/SOP), 켜면 칩 소리 그대로. 그 시절 스피커: 작은 PC 스피커 흉내 (하이패스 200Hz 4차 · 3.5kHz +12dB · 로우패스 9kHz). 재생 중에도 바뀌고 설정에 저장, Jukebox에서만 적용
- MIDI (.mid / .midi, SMF 형식 0·1·2, RMID) · Cakewalk (.wrk, DOS 판 트랙 · 스트림 · 템포 · 주석, 트랙 채널이 이벤트 채널보다 앞섬, 꺼진 트랙은 뺌): 채널 믹서 = MIDI 채널 16개(10 = 드럼). FM이면 OPL3 2-op 18음(빈 채널 → 오래 꺼진 채널 → 오래 울린 음 뺏기), 볼륨 · 익스프레션 · 벨로시티 GM 곡선, 팬 3단계, 피치 벤드(RPN 범위), 서스테인 페달. 음색 · 스피커 · Nuked도 그대로 적용. Windows 신디사이저면 장치로 보내고 조옮김 · 채널 끄기 · 속도 · 볼륨만 적용
- MIDI SysEx · 뱅크 셀렉트: 곡 규격(GM · GM2 · GS · XG · MT-32)을 곡 안 SysEx로 알아내 곡 정보에 표시. 리셋 SysEx(GM On · GM2 On · GS Reset · XG On)는 채널 상태를 처음으로 되돌린다. 드럼 채널: 기본 10, GS 'Use for rhythm part' SysEx, XG 뱅크 MSB 127 · 126(SFX), GM2 뱅크 MSB 120으로 다른 채널도 드럼 (FM은 GM 뱅크 드럼 음색, 키는 더하지 않음). Windows 신디사이저에는 SysEx를 midiOutLongMsg로 그대로 보내고, 곡을 시작할 때 그 규격의 리셋을 먼저 보내며, XG · GM2 드럼 채널은 GS 드럼 파트 SysEx로 바꿔 알린다. 채널 악기 이름에 뱅크 번호 · GS 드럼 세트 이름
- 8-bit (GXSCC 방식): Jukebox 소리 선택 '원래 소리 / 8-bit PSG / 8-bit PSG + SCC / MSX FM-PAC / PC엔진 (HES) / NES (패미컴) / 메가드라이브 / 게임보이 / 슈퍼패미컴' (MIDI · ROL · IMS) 또는 Preferences → MIDI synthesizer (MIDI). 8-bit PSG = PSG만: 멜로디 = AY-3-8910 두 개 6음 사각파 (엔벨로프 · 비브라토만, 볼륨은 한 단계 3dB), 드럼 = PSG. 8-bit PSG + SCC: 멜로디 = SCC+ 세 개 15음 (32칸 파형 · 4비트 볼륨 · 12비트 분주, GM 프로그램 16 갈래마다 파형 · 엔벨로프 · 비브라토), 드럼 = PSG 3채널 (킥 · 탐 = 톤 쓸어내림, 스네어 = 톤 + 노이즈, 하이햇 · 심벌 = 노이즈). 엔벨로프는 1/60초 프레임마다, 음이 모자라면 오래된 음을 뺏음. 조옮김 · 채널 끄기 · 벤드 · 서스테인 · 볼륨 · GS/XG 드럼 채널 그대로, 스피커 효과 적용
- 8-bit 다른 풍: MSX FM-PAC = YM2413 둘 (리듬 모드 칩: 멜로디 6 + BD · SD · TOM · CYM · HH, 다른 칩 멜로디 9 = 15음), GM 프로그램 → 내장 악기 (피아노 · 비브라폰 · 오르간 · 기타 · 베이스 · 바이올린 · 트럼펫 · 클라리넷 · 플루트 · 신스 …), 엔벨로프는 칩이. PC엔진 = HuC6280 셋 (파형 메모리 12음 = 갈래 파형 5비트, 드럼 칩: 사인파 톤 드럼 2 + 노이즈 2). NES = 2A03 둘 (사각파 4음: 갈래 → 듀티 12.5 · 25 · 50%, 삼각파 2음 = 베이스 갈래 먼저, 노이즈 드럼 2). 메가드라이브 = YM2612 둘 (FM 12음, GM 갈래 16개마다 4연산자 음색, 엔벨로프는 칩이) + SN76489 드럼 (톤 2 · 노이즈). 게임보이 = DMG 둘 (사각파 4: 볼륨이 바뀌면 다시 트리거, 파형 채널 2 = 베이스 먼저, 노이즈 드럼 2 = 칩 엔벨로프). 슈퍼패미컴 = S-DSP 둘 (SPC700 없이 레지스터를 바로): 갈래 파형 한 주기 (32칸)를 BRR로 바꾼 반복 샘플 13음, 칩 ADSR · 가우스 보간 · 에코 (48ms · 작게), 드럼 = 만든 킥 · 스네어 BRR 샘플 + DSP 노이즈 (하이햇 · 심벌). 크기는 같은 화음이 PSG + SCC와 비슷하게 맞춤
- ROL · IMS 8-bit: 곡을 같은 틱의 MIDI로 바꿔(`SongToMidi`) 8-bit 신디로 낸다. 보이스 = 채널 (믹서 · 끄기 · 음높이 · 가사 · 이동 그대로), 리듬 모드 드럼 보이스(BD SD TO CY HH) = GS 드럼 파트 + GM 드럼 음 → PSG, BNK 악기 이름 → GM 갈래(`GmBank.GuessProgram`: STANDARD.BNK 이름표 → 이름 낱말 → OPL 엔벨로프), 볼륨 · 악기 TL → CC7 (AdLib과 같은 dB 곡선), 피치 이벤트 → 벤드 (1반음)
- VGM (.vgm / .vgz, GD3 태그): 칩 레지스터 기록을 그대로 재생. 소리 내는 칩 = AY-3-8910 / YM2149 (PSG), K051649 / K052539 (SCC · SCC+), YM3526 · YM3812 · Y8950(FM 부분) · YMF262, SN76489 (게임 기어 스테레오), YM2413 (OPLL, 내장 악기 15 + 리듬), YM2151 (OPM, LFO · 노이즈), YM2203 (OPN, FM 3 + SSG), YM2608 (OPNA, FM 6 + SSG + 리듬(합성음) + ADPCM-B), YM2610 / YM2610B (OPNB, 네오지오 · 타이토: FM 4 / 6 + SSG + ADPCM-A 6 + ADPCM-B, ROM 블록 0x82 · 0x83), YM2612 (OPN2, DAC), OKIM6295 (ADPCM ROM), OKIM6258 (X68000 ADPCM: 분주 512 · 768 · 1024, 팬, 10 / 12비트), SAA1099 (Game Blaster: 사각파 6 · 노이즈 2 · 엔벨로프 2, 믹서 Q1~Q6), Game Boy DMG (사각파 2 · 쓸기 · 파형 · 노이즈, 믹서 P1 P2 W N), 2A03 (NES APU: 사각파 2 · 삼각파 · 노이즈 · DPCM), HuC6280 (PC엔진: 파형 메모리 6채널 · 노이즈 · DDA · LFO), AY-3-8910 둘째 칩 (머리말 74h bit30, 명령 A0h aa bit7) · 셋째 칩 (hoot 렌더 안에서만 명령 D9h), POKEY (아타리: 분주 4채널 · poly 4/5/9/17 · 16비트 연결 · 고역 필터, 머리말 B0h · 명령 BBh, 믹서 P1~P4), RF5C68 (세가 System 18: PCM 8채널 · 웨이브 RAM 64KB, 머리말 40h · 명령 B0h · C1h · RAM 블록 C0h), YM2612 둘째 칩 (2Ch bit30, A2h · A3h), 코나미 K054539 (PCM 8채널 8 · 16비트 · DPCM, 둘까지, 머리말 A0h · 명령 D3h · ROM 블록 8Ch — 잔향 없음), 코나미 K053260 (PCM 4목소리 · KADPCM · 팬, 머리말 ACh · 명령 BAh · ROM 블록 8Eh), 코나미 K007232 (PCM 2채널 7비트 · 반복 — hoot 렌더 안에서만 명령 DBh), Y8950 ADPCM (delta-T — hoot SNK 렌더 안에서만 명령 DCh), 남코 C140 (System 2: PCM 24목소리, 선형 · 압축 표본, 머리말 A8h · 명령 D4h · ROM 블록 8Dh), 남코 CUS30 (System 1: 파형 RAM 8목소리 · 스테레오 · 노이즈, 믹서 W1~W8) · 15XX (매피 계열: 파형 PROM 8목소리 모노 — 갤러그 계열 WSG 3목소리도 이리로) — VGM에 없는 칩이라 hoot 렌더 안에서만 명령 D8h. DAC 스트림 명령(0x90~0x95: 데이터 블록 0x00~0x3F를 정한 속도로 YM2612 · OKIM6258 · OKIM6295 · OPN 계열 레지스터에)도 따른다. 그 밖의 칩 · 둘째 칩은 로그에 이름만 남기고 건너뛴다. 채널 믹서 = 칩 채널 (A B C · S1~S5 · F1~F9/18 · L1~L9 · M1~M8 · BD SD …), 키 · 채널 음높이 = 분주 · F-number를 바꿔 씀(새 칩은 칩의 음높이 배수), 채널 끄기, 속도 = 기다림만 바꿈. 루프 곡은 한 번 더 돌고 마지막 4초를 줄여 끝낸다
- S98 (PC-88 · PC-98 · X68000 로그, 판 1 · 2 · 3): 같은 내용의 VGM으로 바꿔 재생 (장치 = YM2203 · YM2608 · YM2612 · YM2151 · YM2413 · OPL 계열 · PSG · SN76489, 판 1 · 장치 0개 = YM2608). 태그 = 판 3 key=value (UTF-8 / Shift-JIS), 판 1 · 2 = 제목 (Shift-JIS)
- KSS (MSX · 세가 사운드 드라이버 덤프, KSCC / KSSX): Z80으로 드라이버를 돌려 칩 쓰기(PSG · SCC · SCC+ · OPLL · MSX-AUDIO · SN76489)를 VGM으로 모은다. 곡 목록 · 길이 · 제목 = 같은 이름의 .m3u (NEZplug 확장 m3u), 없으면 KSSX 머리말의 곡 범위 · 2분. 목록에서 KSS 파일 하나가 곡마다 한 줄이 되어 다음 곡 = 그 파일의 다음 곡. 포트 · 메모리 배치 · 클럭은 `resource/systems.json`의 msx / sms
- NSF (NES 사운드 드라이버 덤프, NESM): 6502(2A03)로 드라이버를 돌려 APU 쓰기를 VGM으로 모은다. 4KB 뱅크 전환($5FF8~), DPCM 샘플은 VGM 데이터 블록으로 옮김. 곡 목록 = 머리말의 곡 수 (같은 이름 .m3u가 있으면 그것, 번호는 1부터), 길이 2분. 확장 음원 6가지 모두 소리 냄: FDS (64칸 파형 · 엔벨로프 · 주파수 변조), VRC6 (사각파 2 · 톱니), VRC7 (OPLL, VRC7 내장 악기, 6채널), N163 (파형 메모리 최대 8채널), MMC5 (사각파 2 · PCM), 선소프트 5B (YM2149). 믹서 채널 FD · V1 V2 VS · K1~K6 · N1~N8 · M1 M2 MP · A B C
- HES (PC엔진 사운드 드라이버 덤프, HESM): HuC6280으로 드라이버를 돌리고 타이머 · VDC 수직 귀선 인터럽트로 깨운다. PSG 쓰기를 VGM으로. 곡 목록 = .m3u, 없으면 곡 0~255를 2초씩 돌려(함께 돌림, 처음 한 번 1초 안팎) 소리 나고 앞 곡과 다른 것만. 파일 목록의 제목은 곡 목록을 찾기 전에는 비워 둔다
- GBS · GBR (게임보이 사운드 드라이버 덤프): LR35902로 드라이버를 돌려 APU 쓰기를 VGM(0xB3)으로. GBS = 머리말의 곡 수 · 재생 간격 (VBlank 59.7Hz 또는 타이머 TMA · TAC, 컬러 두 배 속도), RST는 읽어 둘 주소 + n, 뱅크 $2000~$3FFF. GBR ('GBRF', 원래 롬의 뱅크 묶음) = 곡 0~255를 1.5초씩 돌려 소리 나는 곡만 (HES와 같음), MBC5 뱅크 쓰기를 파일 뱅크 첫 바이트(원래 번호)로 찾고 스택은 VRAM 끝. 곡 목록 = .m3u (GBS, 번호 0부터). 진단: `SelfTest --gbs 파일 [곡] [초]`. 인터럽트 처리기에 기대는 GBS(예: Little Nicky)는 아직 소리가 나지 않는다
- MDX (X68000 MXDRV 곡) + PDX (ADPCM 샘플): MXDRV처럼 트랙(FM 8 + ADPCM 1, PCM8 16)을 돌려 YM2151(4MHz) · MSM6258 쓰기를 VGM으로. 음색 · 음량(v / @v) · q / @q · 이음 · 디튠 · 포르타멘토 · 되풀이(빠져나감 포함) · 키온 늦춤 · 동기 · 소프트웨어 LFO(음높이 · 음량, 모양은 근사) · OPM LFO · 노이즈 · 템포(타이머 B). PDX = 곡 머리말의 이름으로 같은 폴더에서 (대소문자 무시, 없으면 FM만), ADPCM 속도 3.9~15.6kHz, 데이터 블록 + DAC 스트림. 길이 = 두 번 되돌아갈 때까지 (최대 10분), 끝 4초 줄임. PCM8은 MSM6258 하나로 (마지막 음만). MDD 등 다른 드라이버의 .MDX(머리말이 다름)는 열지 못한다
- SPC (슈퍼 패미컴 사운드 순간 상태, ID666 태그 글자 / 바이너리): SPC700 CPU + S-DSP를 직접 돌린다 (BRR 샘플 · 가우스 보간(근사 표) · ADSR / GAIN · 노이즈 · 피치 변조 · 에코 FIR). 믹서 = 보이스 V1~V8 (끄기 · 음높이), 길이 · 페이드 = 태그 (없으면 3분), 이동은 칩을 그만큼 앞으로 돌린다 (실시간의 7~9배 빠르기라 긴 이동은 잠깐 걸림)
- hoot PC-98 DOS (pc98dos): hoot 아카이브 zip(예: `pc98\yumenosi_98.zip`)을 왼쪽 목록에서 열면, zip 폴더에서 위로 세 단계 안의 hoot 목록(`xml_new` 또는 `xml` 폴더, 처음 한 번 색인 1초 안팎)에서 `<romlist archive>`가 zip 이름인 항목을 찾아 항목마다 `이름 (YM2203).hoot` 파일(항목 XML)을 zip을 푼 폴더에 만든다 → 곡이 여럿 든 파일처럼 펼쳐 titlelist 곡마다 재생 (range = 효과음은 뺌). 재생 = x86(80286) + MS-DOS + PC-98(PIC · PIT · VSYNC · OPN / OPNA · BEEP) 흉내에서 rom 줄(file · conin · device · code · string · shell)대로 원래 드라이버(PMD · FMP · MMP …)를 상주시키고 hoot 인터럽트로 곡 코드를 재생, 칩 쓰기를 바로 YM2203 (26K) / YM2608 (opna · 86) · BEEP으로 (실시간의 3~5배 빠르기, 기계 세우기는 곡을 열 때 뒤에서). 길이는 알 수 없어 3분 + 페이드 8초, 믹서 = FM · SSG (· 리듬 · ADPCM) · BEEP (모두 '사용'으로 표시). 사운드 보드 인터럽트 = INT5 (IRQ12, hoot · NP2 기본 점퍼, SSG 포트 A로 읽힘), 마우스 타이머 IRQ13 (120 · 60 · 30 · 15Hz), 파일 = 원래 zip에서 Shift_JIS 이름 그대로, zip에 없는 셸 프로그램(NAX · DOFMDX98 같은 공용 드라이버)은 같은 폴더의 다른 zip에서. MIDI 판(midiout) · SB16 · 86 PCM은 아직 없고, 무작위 300 zip 첫 곡 기준 약 75%가 소리 난다 (진단: `SelfTest --hoot98survey 폴더 [zip 수] [초]` · `--hoot98trace zip [항목] [초]`). 이동은 기계를 처음부터 그만큼 돌려 잠깐 걸린다
- PC-98 DOS soundblaster16 = PC-98용 Sound Blaster 16 (SB 레지스터 20D2h + 오프셋 × 100h: OPL3 · 믹서 · DSP 리셋 · 판, PCM 없음 → OPL3 칩). hoot PC-88VA (pc88va = DOS 없음 · pc88vados = DOS)는 PC-98 기계에 VA 포트 배치만 바꿈 (OPN 44h~47h, 8259 주 188h · 종 184h, 8253 1A0h, 32h bit7 = 사운드 인터럽트 막음, 사운드 IRQ = 종 IRQ4). hoot PC-9821 DOS (pc9821dos)는 PC-98 DOS 기계 + 386 리얼 모드 CPU (32비트 레지스터 · FS · GS · 66h · 67h · 0F 확장 — 386을 확인하는 드라이버용). 86 보드는 PCM 포트 A466h~A46Ch에 답만 한다 (PCM 소리는 아직). hoot PC/AT DOS (pcatdos): PC-98과 같은 길로 (.hoot 항목, hoot 포트 · 인터럽트 같음). 기계 = PIC 20h/A0h (벡터 08h · 70h), PIT 40h~43h (1.193MHz, 처음 18.2Hz), BIOS INT 08h가 0040:006C 틱 · INT 1Ch를 부름, 스피커 61h + PIT 2, 키보드 · CMOS · 조이스틱 · VGA 귀선 최소, INT 10h · 11h · 12h · 16h · 1Ah. 음원: adlib = OPL2 (388h, OPL 타이머 상태로 카드 찾기), soundblaster16 = OPL3 (220h~223h · 388h~38Bh, DSP 리셋 AAh · 판 4.05, PCM 없음), beep = 스피커, gameblaster = SAA1099 × 2 (C/MS 7.16MHz: 221h 주소 · 220h 데이터, 223h · 222h 둘째 칩, 채널 Q1~Q12), tandy = SN76489 (C0h 3.58MHz, BIOS F000:FFFE = FFh · F000:C000 = 21h로 Tandy 1000인 척). CPU = 1.193MHz × clockmul (기본 4 = 4.77MHz). Hootarchive\pc 480 zip 첫 곡: AdLib 410/412 · SB16 67/71 · BEEP 5/5 소리 (진단 `SelfTest --hootatsurvey 폴더 [zip 수] [초]`)
- hoot PC-98 (DOS 없음: pc98vx, 옛 방식 pc98 — hoot PC98.TXT "PC-98generic"): 같은 .hoot 길로, DOS 없이 rom 줄 code (파일을 주소에) · binary (16진 바이트 패치) · string을 메모리에 놓고 bootcs:bootip에서 시작한다 (주소: addressing / adressing 0 = 선형, 1 = 세그먼트 << 16 | 오프셋). 스텁이 hoot 인터럽트 벡터(funcvect)를 걸고 7E8h에 81h를 쓰면 부팅 끝. 재생 = 요인 0으로 hoot 인터럽트 → 처리기가 돌아오면 곡 코드 아래 16비트 번호의 bgm / bgm2를 dataaddr / data2addr에 (filesize · file2size까지, 주소 0이면 안 넣음) → 넣었으면 요인 1로 한 번 더. I/O 7D0h (기능 · 상태) · 7D2h~7D7h (인자 1~3): 10h · 11h dataaddr 설정 · 얻기, 18h strlist, 20h · 21h bgm 넣기, 30h · 31h 부분 넣기 (인자 블록). pc98 = PIC 자동 EOI. MPU-PC98은 MIDI 판(midiout)에만 꽂는다 (FM 판에 MPU가 보이면 드라이버가 MIDI로만 낸다). titlelist에 제목 없이 range만 있으면 range가 곡 목록. 소리 남 (첫 음악 6초): pc98vx opn 168/198 · opna 18/20 · 86 9/9 · beep 36/40, pc98 opn 77/81 · opna 2/2 · beep 4/4, MIDI 판 97/109 (진단은 PC-98 DOS와 같은 `--hoot98survey` · `--hoot98trace` · `--hootmidisurvey`)
- hoot MSX 범용 드라이버 (msx generic, 문서 없음 — 목록의 시작 코드를 역어셈블해 맞춘 규약, `Audio/HootMsx.cs`): Z80 3.58MHz · 64KB RAM · VDP 60Hz 인터럽트 (IM 1 / IM 2) · 작은 BIOS (0038h 처리기 → H.KEYI · H.TIMI, WRTPSG · RDPSG · 슬롯 루틴) 위에 rom code · binary · sccp (Konami SCC+ 카트리지 RAM)를 놓고 init_pc에서 시작. 주소 공간을 넘는 rom은 16KB 뱅크 카트리지 (FM-PAC 64KB = 7FF7h 뱅크). hoot 포트: IN 02h = 명령 (1 재생), OUT 02h = 받음, IN 03h~06h = 곡 코드 바이트, OUT 03h,n = bgm n을 mdata_addr에 (mdata_size · mfile_size), IN 07h = FM 있음 (use_opll). 칩: PSG · OPLL · MSX-AUDIO · SCC (9000h ← 3Fh, use_scc) · SCC+ (BFFEh). 곡은 재생 명령부터 180초를 미리 렌더 (끝 4초 줄임). 소리 남 481 / 498항목 (`--hootmsxsurvey`)
- hoot PC-8801 (pc88 opn · opna, 문서 없음 — 시작 코드로 맞춘 규약, `Audio/HootPc88.cs`): Z80 3.9936MHz · 64KB RAM, IM 2 인터럽트 (I·256 + 레벨×2: VRTC 레벨 1 · RTC 1/600초 레벨 2 · OPN 타이머 레벨 4, 벡터 칸이 0이면 넘김), OPN 44h · 45h / OPNA 46h · 47h. hoot 포트: IN 00h = 명령, OUT 00h = 받음, IN 01h = 곡 코드 아래 바이트, OUT 01h,n = bgm n을 mdata_addr에 (없으면 4000h) + voice n을 vdata_addr에, IN 80h = 곡 코드 맨 위 바이트. 180초 미리 렌더 → VGM (YM2203 / YM2608). 소리 남 487 / 527항목 (`--hoot88survey`). OPNA ADPCM · 디스크 BASIC 항목은 아직
- hoot 타이토 Ring Rage · Riding Fight · Grid Seeker: 게임 이름 형식이지만 같은 F3 사운드 보드 → HootTaitoF3
- hoot PC-88: RTC · VRTC 인터럽트는 옵션 use_rtc · use_vrtc로만 (포트 E6h 쓰기로는 켜지 않음)
- hoot MSX 시스템 타이머 (포트 E6h · E7h, MSX2+ · turbo R: 3.911µs마다 하나씩)
- hoot MSX FM-PAC 롬: 실기 부팅 때 롬 초기화처럼 SLTWRK에 작업 영역 (3000h) · 슬롯 0 확장 (FM-PAC = 0-1)
- DOS 기계 파일 핸들 256개 (hoot rom file offset = 핸들 번호, 40h 넘는 것도), --hoot98survey는 첫 곡이 조용하면 다음 곡 2개까지
- PC-98 8259 주 PIC = 특수 완전 중첩 (SFNM, BIOS ICW4 1Dh): 종에만 EOI하는 처리기도 다음 사운드 IRQ를 받음
- DOS 기계: 프로그램 시작 DI = 0 (hoot의 DOS; 실제 DOS는 SP)
- hoot PC-98 파일 이름 맞추기: 목록의 폴더 (DISC_1/AMUS.DAT)가 zip에 없으면 같은 이름 파일, fileNN.EXT가 없으면 zip 순서 NN번째 .EXT
- hoot PC-98 옵션 dummysndrom (74항목): 메모리 스위치 4 (A3FEEh) bit3 = 사운드 ROM 있음
- hoot PC-88 mfile_size > mdata_size 항목 (KOEI · Snatcher): 파일 = 버퍼, 곡 코드 8~23비트 = 버퍼 안 위치에서 mdata_size만 (hoot snatcher.cpp) — 예전엔 모든 곡이 첫 덩어리
- hoot archive="a,b" 항목 (MSX FM-PAC BIOS 등): 이 zip에 없는 파일은 같은 폴더의 b.zip에서, MSX rom 줄 fmbios = FM-PAC 롬을 4000h~ (16KB 뱅크)
- hoot X68000 mxdrv (doujin · takeru 3항목): data 줄 offset = 곡 코드의 MDX를 MDX 재생기로
- HES: VDC 레지스터 5를 안 쓴 곡도 VBlank IRQ1 (hoot hes.cpp), PC-98 OPN: FM 레지스터 읽기 = 0 (fmgen)
- SCC (K051649) 클럭 = VGM 규약 1.789772MHz (hoot · MAME와 같이 ÷16): 재생기는 2.5MHz 아래 클럭을 입력 클럭의 절반으로 읽고 (예전 3.58MHz 파일도 그대로), MSX · KSS 렌더는 1.79MHz로 적음 — 다른 VGM과 코나미 K007232 보드의 SCC가 한 옥타브 낮던 문제
- hoot X1 · PC-88 · MSX 기계는 hoot (RAZE z80_reset)처럼 레지스터 0으로 시작 (KOEI X1 PATCH가 B = 0 · S = 0에 기댐)
- hoot MSX SCC+ 카트리지: sccp 줄 = 본체 RAM과 따로인 카트리지 RAM 128KB (A8h로 고른 페이지에만, 뱅크 레지스터 · 모드 BFFEh)
- KSS 플레이어 (hoot kss.cpp · ssAY8910으로 맞춤): BIOS 영역 = RET, PSG 읽기 = 레지스터 값 (R14 = FFh)
- hoot MSX ds4 (원본 ds4.cpp 이식, Legacy of the Wizard 롬): 4000h~7FFFh 고정 · 8000h / A000h 8KB 뱅크 (7000h · 7800h), IPL + PSG 진입점, 포트 00h 깃발 · 01h 곡 · 02h 쉼, 80h 이상 = 효과음 칸 C095h
- hoot PC-88 옛 팔콤 (원본 oldfalcom.cpp 이식): xanadu · xanadu2 · asteka2 · romancia — 곡 코드 위 바이트 = 드라이버 prog, 주소 표 (끼어들기 · 시작 · 멈춤 · 곡 번호)를 E00Eh에, Xanadu II는 OUT 02h로 bgm. 8801-11 (사운드 보드 II의 OPN) · generic (OPNA)도 재생
- hoot MSX (원본 kss.cpp · ssAY8910으로 맞춤): BIOS 영역 = RET + 빠진 진입점 (GTSTCK · GTTRIG · RSLREG · WSLREG · RDVDP · SNSMAT · CALLF), 인터럽트가 JIFFY를 올림, PSG 읽기 = 레지스터 값, SCC 9890h~ 미러
- hoot PC-88 (원본 mucom88.cpp · ed2.cpp로 맞춤): OPN 타이머 = hoot 규칙 (깃발은 LOAD만으로 · IRQ는 넘칠 때마다), 32h · AAh bit7 (SINTM) = 사운드 인터럽트 막음, 사운드 보드 II 포트 A8h~ADh, Z80 클럭 = 옵션 baseclock (없으면 8MHz)
- hoot X68000 (원본 x68k.cpp로 맞춤): 24비트 주소 버스, DMA ch3 SCR 처음 값 = 주소 증가 (시작 코드의 ADPCM이 한 바이트만 되풀이하던 문제), ADPCM PPI 처음 값 = 양쪽 켬, zmusic.x는 zip 안의 ZMSC.X를 먼저
- hoot X1 (원본 mucomx1.cpp로 맞춤): rom 줄 data = I/O 포트 메모리 (KOEI 드라이버가 IN r,(C)로 곡 표를 읽음), 나머지 포트는 쓴 값을 되읽음, CTC 카운터는 시간 상수가 안 와도 256으로 셈
- hoot 원본 소스 (2001년판, `_incoming/hoot code`)로 맞춤: PC-98 기계의 OPN 타이머 = hoot `ssFMTimer` 규칙 (깃발은 LOAD만으로 · IRQ는 ENABLE일 때 넘칠 때마다), 종 8259가 막은 동안 온 사운드 IRQ도 나중에 열면 살아남, PC-88VA 사운드 보드 II 포트 (A8h~ADh · AAh)
- hoot 타이토 F3 · Super Z System (taito f3system, `Audio/HootTaitoF3.cs`): 앙상블 사운드 모듈 — 68000 15.24MHz, ES5505 (새 칩 `Chips/Es5505.cs`: 목소리 32 · 4극 필터 · 되풀이, 목소리마다 2MB 표본 뱅크), MC68681 DUART 타이머 (IRQ 6), ES5510 ESP는 호스트 쪽 기억만 (반향 없음). 곡 요청 = 메인 CPU와 나누는 DPRAM의 메시지 고리 [길이][명령][인자] (81h 재생 · 86h 음량), tbloffs가 있으면 메인 ROM의 소리 표로 메인 CPU의 재생 루틴을 흉내 (형 0~3). 표본 ROM이 커서 미리 렌더하지 않고 스스로 도는 칩으로 재생. 36 / 36항목
- hoot FM-7 · FM77AV (fm7 · fm77av generic, 문서 없음 — PATCH.BIN으로 맞춘 규약, `Audio/HootFm7.cs`): 6809 2MHz · 64KB RAM, 타이머 IRQ 2.03ms (FD02h 켬 · FD03h 원인, 0 = 그 원인), PSG (FD0Dh 명령 · FD0Eh 자료) · OPN (FD15h · FD16h, FM77AV 안 / FM 음원 카드) · WHG 둘째 OPN (FD45h · FD46h), 명령 0 쉼 · 1 읽기 · 2 쓰기 · 3 주소 · 4 상태, 서브 CPU 멈춤 (FD05h bit7). hoot 포트 FD58h 명령 (1 재생 · 2 멈춤) · FD59h~FD5Ch 곡 코드, FD59h 쓰기 n = bgm n을 mdata_addr에. driver fm7 = FM 카드 없음 (PSG 판). 180초 미리 렌더 → VGM (YM2203 (둘) + AY-3-8910). 29 / 29항목
- hoot X1 (x1 psg · opm, 문서 없음 — 시작 코드로 맞춘 규약, `Audio/HootX1.cs`): Z80 4MHz · 64KB RAM · 16비트 I/O, Z80 CTC (1FA0h~, FM 보드 0704h~, IM 2, 채널 0 → 채널 3 연결, 옵션 use_ctcN = 시간 상수), PSG 1C00h · 1B00h, OPM 0700h · 0701h (FM 보드는 opm 판에만), I/O 2000h~FFFFh = VRAM 메모리, PPI 1A01h (수직 귀선 · 서브 CPU). hoot 포트 0000h 명령 · 0001h 곡 코드 / bgm 올리기 · 000Fh 곡 코드 맨 위 바이트. 180초 미리 렌더 → VGM (AY-3-8910 / YM2151). 소리 남 143 / 152항목 (`--hootx1survey`)
- hoot 네오지오 (neogeo generic, `Audio/HootNeoGeo.cs`): 사운드 보드를 그대로 흉내 내고 M1 ROM을 돌린다 — Z80 4MHz, 뱅크 창 4개 (IN 08h~0Bh), 명령 = NMI (포트 00h), YM2610 04h~07h · 타이머 IRQ. SNK 사운드 드라이버 (Mr.Pac · MAKOTO · "To SNK")면 먼저 명령 08h (음악 켬). ADPCM-A/B ROM은 VGM 데이터 블록으로. 소리 남 137 / 146항목 (`--hootneosurvey`), 암호화 V ROM (neo_pcm2) 10항목은 ADPCM이 깨짐
- hoot 타이토 YM2610 보드 (taito f2system · bsystem · dual68 · xsystem · fx1a · sonicb, `Audio/HootTaito.cs`): 네오지오처럼 사운드 보드를 그대로 흉내 내고 Z80 사운드 ROM을 돌린다 — Z80 4MHz, 0000h~3FFFh 고정 · 4000h~7FFFh 뱅크 (F200h = n → ROM n × 4000h), RAM C000h~DFFFh, YM2610(B) E000h~E003h · 타이머 IRQ, TC0140SYT 통신 칩 E200h (모드) · E201h (니블 읽기 · 상태 · NMI 끔/켬). 곡 코드 = 68000처럼 포트 0 · 1에 낮은 · 높은 니블 (2바이트 코드는 위 바이트를 포트 2 · 3에). 타이토 드라이버(EFh 처리 코드가 있는 것)는 먼저 명령 EFh (소리 켬). code offset은 보드마다 다른 자리라 가장 작은 것을 0으로. 소리 남 67 / 67항목 (`--hoottaitosurvey`)
- hoot 아케이드 Z80 + YM2151 + OKIM6295 보드 (capcom cps1 · technos ddragon2 · ddragon3 · ctribe, `Audio/HootOpmBoard.cs`): 보드 배치 표(`Layout`) 하나로 — CPS-1 = ROM 0000h~7FFFh + 뱅크 8000h~BFFFh (F004h), RAM D000h~, YM2151 F000h, OKI F002h, 핀7 F006h, 래치 F008h (드라이버가 바뀌었는지 보며 읽음, 처음 FFh). Double Dragon 2 · Combatribes = RAM 8000h · YM2151 8800h · OKI 9800h · 래치 A000h (NMI), Double Dragon 3 = ROM ~BFFFh · RAM C000h · YM2151 C800h · OKI D800h · 래치 E000h (NMI) · OKI 뱅크 E800h. YM2151 타이머 = IRQ, OKI 읽기 = 울리는 음성 (구절 표 길이로 계산). VGM = YM2151 + OKIM6295 (ROM 블록 8Bh). 소리 남 39 / 39항목 (`--hootopmsurvey`)
- hoot 세가 System 1 (sega system1, `Audio/HootSegaSystem1.cs`): Z80 4MHz, ROM 0000h~7FFFh · RAM 8000h, SN76489 A000h (2MHz) · C000h (4MHz), 명령 E000h = NMI, IRQ 프레임당 4번 (240Hz). VGM = SN76489 둘 (VGM 듀얼 칩 명령 30h, 재생기가 둘째 칩 클럭을 따로 씀). 소리 남 29 / 29항목
- hoot 아이렘 M72 계열 (irem m72 = M72 · M75 · M77 · M81 · M82 · M84 · M85 · M90 · M97, `Audio/HootM72.cs`): Z80 3.58MHz + YM2151. transfer 판은 메인 CPU ROM(짝수 · 홀수 바이트)의 z80_offset부터 64KB를 RAM에. YM2151 = 포트 00h · 01h (또는 40h · 41h), 명령 = 02h · 80h · 42h, 확인 = 06h · 83h · 42h · 43h. 인터럽트 RST_NEG (IM 0: 타이머 RST 28h · 명령 RST 18h · 둘 다 RST 08h, IM 1 = 0038h). 부팅 뒤 명령 00h (아이렘 드라이버는 이 핸드셰이크 전 명령을 버림). DAC 샘플 (북 · 목소리)은 빠짐. 소리 남 23 / 23항목 (`--hootarcadesurvey`)
- hoot 데이터 이스트 HuC6280 보드 (dataeast thndzone · thunderzone · supbtime · deco32 · wolffang · generic, `Audio/HootDeco.cs`): HES용 HuC6280을 다시 써서 사운드 ROM을 돌린다 — 4.03MHz, 21비트 주소 ROM 000000h · YM2203 100000h · YM2151 110000h · OKIM6295 120000h (1MHz) · 130000h (2MHz) · 명령 140000h (IRQ1) · RAM 1F0000h · 내부 타이머 1FEC00h · 인터럽트 1FF400h, YM2151 타이머 = IRQ2, YM2151 CT = OKI 뱅크. VGM = YM2151 (+ YM2203) + OKIM6295 둘 (VGM 듀얼 OKI: 클럭 bit30 · 레지스터 bit7 · ROM 블록 크기 bit31, 둘째 칩 클럭 따로). 소리 남 41 / 43항목 (`--hootdecosurvey`)
- hoot 잘레코 Mega System 1 (jaleco megasys1, `Audio/HootMegaSys1.cs`): X68000용 MC68000을 다시 써서 사운드 68000 프로그램을 돌린다 — 7MHz, ROM 두 개 = 짝수 · 홀수 바이트, 명령 040000h (System C 060000h, IRQ 4 자동 벡터 · 대부분 드라이버는 폴링), YM2151 080000h, OKIM6295 0A0001h · 0C0001h (각 4MHz), RAM 0E0000h~. 소리 남 16 / 16항목 (`--hootms1survey`)
- hoot 세가 System 16A · 16B (sega system16a · system16b, `Audio/HootSegaSystem16.cs`): Z80 5MHz (16A 4MHz), ROM · ADPCM 뱅크 창 8000h~DFFFh · RAM F800h, YM2151 포트 00h~3Fh, 제어 40h (START · /RESET · 뱅크: boardtype 5358 · 5521 · 5797), uPD7759 데이터 80h · 상태, 명령 C0h (16B = IRQ, 16A = NMI). YM2151 IRQ는 연결 안 됨 (드라이버가 폴링). uPD7759 DRQ = NMI → 처리기가 바이트를 넣음. 소리 남 37 / 38항목 (`--hoots16survey`)
- uPD7759 (`Audio/Chips/Upd7759.cs`, 새 칩): MAME upd7759의 슬레이브 모드 · 마스터 모드 (v2.1.52: 칩이 ROM을 직접, VGM 8Ch bit31 · ROM 블록 92h) 상태 기계 (곡 번호 · 주소 · 블록 머리 · 니블, ADPCM 표 16 × 16, 9비트 출력). VGM 8Ch · 명령 B6h (0 /RESET · 1 START · 2 데이터). 재생기는 쓴 바이트를 DRQ마다 하나씩 짝지어 (VGM 시각이 거칠어도 순서가 맞게), 많이 밀리면 소리 없이 따라잡는다. 마스터 모드 (자기 ROM)는 아직
- hoot 캡콤 CPS-2 (capcom cps2, `Audio/HootCps2.cs`): Z80 8MHz, ROM · 뱅크 8000h (D003h), 68000과 같이 쓰는 RAM C000h (명령 묶음 C000h~C00Fh: C001h = 곡 코드, C00Fh ← 00h, 부팅 핸드셰이크 CFFFh 77h → FFh), QSound D000h~D002h, IRQ 250Hz. QSound ROM은 두 바이트씩 바꿔 둔다 (WORD_SWAP). 소리 남 38 / 38항목 (`--hootcps2survey`)
- QSound (`Audio/Chips/QSound.cs`, 새 칩): DSP 내부 ROM (dl-1425)이 없어 예전 MAME HLE로 근사 — 16채널 8bit PCM · 팬, 3D · 반향 · 필터 없음. VGM B4h · 명령 C4h · ROM 블록 8Fh
- hoot 세가 PCM 보드 (sega outrun · shangon · toutrun · aburner · spmonaco · gforce · pdrift · hangon · sharrier · eropnx2, `Audio/HootSegaPcmBoard.cs`): 사운드 ROM을 읽어 맞춘 두 배치 — Out Run 계열 (Out Run · Super Hang-On · X · Y 보드: ROM ~EFFFh, 세가 PCM F000h, RAM F800h, YM2151 포트 00h, 명령 포트 40h = NMI) · Hang-On 계열 (Hang-On · Space Harrier · Enduro Racer: RAM C000h, YM2203 D000h, 세가 PCM E000h, 명령 40h = NMI, YM2203 타이머 IRQ). 세가 PCM은 보드 안에서도 돌려 드라이버가 읽는 86h 멈춤 비트가 실제처럼 바뀐다. 소리 남 20 / 24항목 (`--hootspcmsurvey`)
- 세가 PCM (`Audio/Chips/SegaPcm.cs`, 새 칩): MAME segapcm — 16채널 8bit PCM, 레지스터 = 256바이트 RAM, 끝 = 256바이트 단위, 뱅크 (BANK_512 · BANK_12M). VGM 38h · 인터페이스 3Ch · 명령 C0h · ROM 블록 80h
- hoot 메가드라이브 Z80 드라이버 (megadrive generic_z80 · iyinc, `Audio/HootMegaDrive.cs`): 68000 없이 Z80 사운드 쪽만 — 카트리지 ROM의 topaddr부터 size1 바이트를 Z80 RAM data1에 올려 돌리고, 곡 번호를 reqaddr (없으면 1FFFh)에, trigadr · trigvalue도. Z80 RAM 8KB · YM2612 4000h~ (타이머 플래그를 상태로) · 뱅크 6000h · PSG 7F11h · 카트리지 창 8000h~ · VBlank INT. VGM = YM2612 + SN76489. 소리 남 93 / 131항목 (`--hootmdsurvey`), koei · 68000 드라이버 형식은 아직
- hoot X68000 (x68k generic, 문서 없음 — 시작 코드로 맞춘 규약, `Audio/HootX68k.cs` · `Audio/Cpu/M68000.cs`): MC68000 10MHz (명령 전부, 무작위 명령 2만 개를 다른 흉내기와 맞춰 봄) · 16MB 메모리, OPM (YM2151, 타이머 넘칠 때마다 레벨 6 자동 벡터 — OPMINTST를 hoot에 맡긴 시작 코드면 벡터 43h) · ADPCM (MSM6258, 클럭 = OPM 1Bh bit7, 분주 · 팬 = PPI) · DMA 채널 3 (보통 · 배열 · 링크 배열 · 계속) · MFP (옵션 mfp: 타이머 A~D · GPIP · V-DISP). hoot 포트 E00000h 명령 · E00001h~ 곡 코드 · E00800h 쉼 · E00010h = hoot 부름 (2Fh IOCS: ADPCM · OPM · 인터럽트 설정 · DMAMOVE …, FFh DOS: 벡터 · 메모리 · 파일 읽기 · _BUS_ERR …). .X 실행 파일은 머리말을 빼고 올려 재배치, zip에 없는 파일 (zmusic.x · boot.bin …)은 같은 폴더 다른 zip에서. 180초 미리 렌더 → VGM (YM2151 + OKIM6258). 소리 남 499 / 553항목 (`--hootx68survey`). MIDI 판 (midiout): YM3802 MIDI 보드 (EAFA00h, R56 = 보냄)의 출력을 살아 있는 MIDI 곡으로 — 노트 남 141 / 166항목 (`--hootx68midisurvey`). PCM8은 아직
- hoot MIDI 판 (pc98dos · pc98vx · pc98 · pcatdos · x68k + 옵션 midiout): 기계의 MPU-401 (PC/AT 330h/331h · IRQ9) · MPU-PC98 (E0D0h/E0D2h · INT2 = IRQ6) 출력을 MIDI 곡으로 — Jukebox의 MIDI 재생 (FM · Windows 신디 · MT-32 / 8-bit 설정 그대로). MPU = UART 모드 + 지능 모드 (Want to Send Data, 재생 시퀀서: 트랙 데이터 요청 F0h+n · 타이밍 · 지휘 트랙, 클럭-투-호스트 FDh, 템포 · 타임베이스). 곡은 "살아 있는" MIDI 곡: 재생기가 1초쯤 앞까지 이벤트를 달라고 하면 그때 기계를 돌려 채운다 (미리 다 만들지 않음, 120 BPM · 480틱 고정 = 1초 960틱, 길이 3분). midiout_type 1~3 (MT-32 · CM-64) = MT-32로 (에뮬레이터가 없으면 MT-32 음색 번호 → GM), 4 · 6 · 7 · 9 = GS, 8 = GM. 첫 곡 20초: PC/AT 571/586 · PC-98 360/641 노트 (진단 `SelfTest --hootmidisurvey 폴더 [zip 수] [초]`)
- IMF (.imf 560Hz · .wlf 700Hz, 종류 0 / 1, 꼬리 태그) · HSC (HSC-Tracker) · CMF (Creative Music File, 리듬 모드 CC 0x67): OPL2 레지스터 기록으로 바꿔 VGM처럼 재생
- DRO (DOSBox Raw OPL, 판 0.1 · 2.0) · RAW (Rdos Raw OPL Capture, 곡 안 클럭 바꾸기 포함): 게임이 OPL에 쓴 기록을 VGM으로. OPL2 · OPL3 그대로, 듀얼 OPL2(사운드 블래스터 프로 1)는 OPL3 하나에 칩 0 = 왼쪽 · 칩 1 = 오른쪽
- DOS 게임 음악 (PC/AT): **XMI** (Miles AIL XMIDI — 곡 여럿이면 왼쪽 목록에서 곡마다, 같은 폴더의 AIL 음색 묶음 .AD / .OPL이 있으면 그 게임의 AdLib 악기로), **MDI** (Ad Lib MIDI Driver — 곡 안의 AdLib 악기로, 타악기 모드 = GM 드럼), **MUS** (Ad Lib MUS + 음색 뱅크 .SND: 같은 이름 → 같은 폴더의 .SND). XMI · MDI는 MIDI 재생(FM · Windows 신디 · MT-32)으로, MUS는 ROL · IMS처럼 Composer · Jukebox에서
- 다른 페이지로 가면 일시정지 (오디오 엔진을 Composer와 같이 쓴다). 돌아와서 Space로 이어 듣기
- File → Export VGM (chip log)...: 작업 곡(ROL · IMS = YM3812, SOP = YMF262)을 VGM / VGZ로 저장 (재생기를 소리 없이 돌려 레지스터 쓰기를 기록, 다른 VGM 플레이어에서 재생)
- File → Extract Disk Image (D88 / FDI / HDM)...: PC-98 MS-DOS(FAT12) · PC-88 N88-BASIC(2D) 디스크의 파일을 이미지 옆 "이름_files" 폴더에 꺼내고 왼쪽 목록에서 연다 (PMD · FMP · mucom88 곡 재생은 아직 없음)
- 엔진: `PlaybackOptions`(속도 · 키 · 채널 끄기 · 채널 음높이 · 출력 크기)를 `AudioEngine.Play(..., options)`로 넘기고 `SetSpeed / SetVoiceMute / ...`로 재생 중에 바꾼다. 채널 상태는 `AudioEngine.GetVoiceStates`

**공통**: 메뉴 `File(F5) Edit(F6) Playback(F7) Display(F8) Options(F9) Help(F10)`, 우측 상단 `Composer / Instrument Maker` 전환 (Ctrl+2 / Ctrl+3)

**메뉴 (원본 Visual Composer 구성 그대로, 이 프로그램의 추가 기능은 각 메뉴 구분선 아래)**
| 메뉴 | 원본 항목 |
|---|---|
| F5 File | New, Open, Save, Save As, Revert / Instrument Bank File (→ 뱅크 브라우저, Ctrl+B) |
| F6 Edit | Edit(선택 도구 I), Copy, Cut, Paste, Clear / Semitone Up·Down (Ctrl+↑↓), Octave Up·Down (Ctrl+Shift+↑↓) |
| F7 Playback | Play (Space), Play Voice (Shift+Space, 현재 보이스만), Interrupt (Esc) / Basic Tempo |
| F8 Display | Tempo 1.0, Instrument, Volume 0.75, Pitch 1.0 (위쪽 행에 보일 트랙 + 넣을 값) / Large·Medium·Small Grid / Ticks per Beat, Beats per Measure |
| F9 Options | With / Without Percussion / Audio Feedback / MIDI Input (미구현) / **With Percussion (SOP)** (이 프로그램의 추가 기능) |

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

**Composer SOP 모드** (`Options → With Percussion (SOP)`, OPL3 작곡)
- 보이스 20개 = SOP 트랙 0~19: `1~6`, `BD SD TO CY HH`, `12~20` (12~20은 OPL3 둘째 뱅크 채널). 드럼 규칙은 ROL 리듬 모드와 같다
- 재생: 곡 + 작업 뱅크를 SOP로 바꿔 Jukebox와 같은 `SopPlayer`(OPL3, 스테레오)로 재생. Play Voice = 나머지 보이스 끄기, Loop Playback 지원
- 팬: `Display → Pan (SOP)...`로 위쪽 행에 L / C / R 이벤트 (SOP 팬 0 = R, 1 = C, 2 = L)
- 볼륨 0~1 ↔ SOP 0~127, 피치 0~2 ↔ SOP 0~200 (100당 1반음), 템포 배수 ↔ SOP 절대 BPM (기본 템포 · 박당 틱은 255까지)
- 4-op: SOP에서 읽은 4-op 악기(오퍼레이터 3·4는 `AdlibInstrument.Sop4OpPair`)를 보이스 1·2·3·12·13·14에 쓰면 4-op로 울리고, 그 짝 보이스 4·5·6·15·16·17은 소리가 나지 않는다 (OPL3 채널 짝, 보이스를 고르면 상태 표시줄에 안내). 다른 보이스에서는 오퍼레이터 1·2만
- 열기: `.sop`를 열면 곡 안 악기로 새 작업 뱅크를 만든다 (같은 이름의 BNK 악기와 소리가 달라 섞지 않음). 길이 0 노트(다음 노트까지)는 다음 노트까지 늘리고, 겹친 노트는 앞 노트를 자른다
- 저장: SOP 모드 곡은 `.sop`로만 (곡이 쓰는 악기만 곡 안에 넣는다, 악기가 없는 보이스는 기본 악기). ROL/IMS 모드 곡을 `.sop`로 저장하면 SOP 모드로 바뀐다
- 모드 바꾸기: SOP → With / Without Percussion이면 보이스 12~20(노트가 있으면 묻는다) · 팬 · 4-op는 ROL/IMS에 들어가지 않는다 (지우지 않고 숨김)
- 시음(건반 · 키보드): 2-op 악기는 OPL2 시음 신디사이저 (보이스 12~20은 보이스 1 자리로), 4-op 악기는 OPL3로 시음한다 (멜로디 보이스)
- SOP 마스터 볼륨 이벤트는 읽은 값을 보존 · 재생만 한다 (편집 화면 없음). 원래 SOP 제목 · 설명은 저장할 때 파일 이름으로 바뀐다

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
- 화면 위: 뱅크 파일 정보(파일 이름 · 폴더 · 악기 수 · 지금 악기 번호 · 이름 칸), 그 아래 F6 Option 메뉴를 버튼으로 (모드 6개 · Additive · 건반 Oct −/+ · Master Volume)
- 왼쪽: 뱅크 악기 목록(번호 · 이름 · 모드, ↑ ↓로 이동) + New / Copy / Rename / Delete (예전 `List...` 대화상자와 `< >` 버튼 대신)
- **4-op 악기** (SOP · OPL3, 이 프로그램의 추가 기능): `4-Op` 버튼으로 켜기 / 끄기 (Melodic 모드만, 켜면 오퍼레이터 3·4를 1·2와 같은 값으로 시작), `Op 1·2` / `Op 3·4` 버튼(F3 / F4)으로 두 짝을 번갈아 편집. 열 머리글이 Op 2 Carrier / Op 1 Modulator, Op 4 Carrier / Op 3 Modulator로 바뀐다
  - `Additive`는 지금 보이는 짝의 연결 비트 → 연결 4가지 (옆에 표시: `1→2→3→4`, `1 + 2→3→4`, `1→2 + 3→4`, `1 + 2→3 + 4`)
  - 웨이브폼은 OPL3의 0~7 (반주기 2배 사인, 그 절대값, 사각, 로그 톱니 포함), 2-op 악기는 OPL2의 0~3
  - 시음은 OPL3 (채널 0+3 짝)로, 누르고 있는 동안 고친 값이 바로 들린다. 오퍼레이터 3·4는 SOP 곡으로 저장할 때만 들어간다 (BNK · INS에는 1·2만, 저장할 때 로그에 경고)

## 구조

| 프로젝트 | 역할 |
|---|---|
| `AdlibComposer.Core` | 모델: `Song`(틱 기반 ROL 구조, SOP 모드 = 보이스 20 · 팬), `VoiceTrack`, `Note`, `AdlibInstrument`, `InstrumentBank`, `TempoMap` |
| `AdlibComposer.Formats` | `RolFormat`, `ImsFormat`, `BnkFormat`, `InsFormat`, `IssLyrics`(ISS 가사), `KoreanText`(조합형/완성형 → 유니코드), `FormatRegistry`, `SopFormat` → `SopSong`(OPL3 곡 읽기 · 쓰기), `SopConvert`(SopSong ↔ Song SOP 모드 · 악기 레지스터), `TrackerModule` + `StmFormat` / `S3mFormat`(Scream Tracker 2/3 읽기·쓰기), `ModFormat`(MOD / NST ↔ S3M 모델), `MidiSong` + `MidiFileFormat` / `WrkFormat`(MIDI · Cakewalk 읽기), `GeneralMidi` · `GmBank`(GM 이름, GENMIDI.OP2 / BNK 이름 맞춤 FM 뱅크), `MidiSysEx`(GM · GS · XG 리셋 · 드럼 파트 SysEx, 곡 규격), `MusicDuplicateFinder`(같은 곡 찾기 · 남길 파일 점수), `SongToMidi`(ROL · IMS → 재생용 MIDI, 8-bit), `VgmFormat` → `VgmSong`(VGM · VGZ 머리말 · GD3), `VgmWriter`(VGM 1.71 쓰기), `S98Format`(S98 → VgmSong), `AdlibStreamFormats`(IMF · HSC · CMF · DRO · RAW → OPL2 VgmSong), `DiskImage`(D88 · FDI · 날 이미지, FAT12 · N88-BASIC 파일 꺼내기) |
| `AdlibComposer.Audio` | `OplChip`(코어를 바꿔 끼우는 칩: 내장 / Nuked, `OplChip.GlobalCore`), `Opl2Emulator`(내장 OPL2), `Opl3Emulator`(18채널 · 4-op · 웨이브폼 8종 · 스테레오), `ToneFilters`(Jukebox 음색 · 스피커 필터), `MidiPlayer`(MIDI → OPL3 FM · Windows MIDI 출력 · MT-32 · 8-bit), `ChipMidiSynth`(GXSCC 방식 SCC+ · PSG MIDI 신디), `Mt32Emu`(Munt mt32emu.dll P/Invoke), `AudioFileReader`(WAV · Media Foundation → 모노), `MelodyAnalysis`(주 멜로디 채보), `MelodyToSong`(초 → 틱, 격자), `PcmPlayer`(원곡 + 멜로디 재생), `OplSynth`(보이스→레지스터, 리듬 모드), `Sequencer`, `SopPlayer`(SOP → OPL3), `MusicBoxPlayer`(오르골 시험 연주), `TrackerPlayer`(STM/S3M 샘플 믹서 + AdLib), `PsgEmulator`(AY-3-8910 / YM2149) · `SccEmulator`(K051649 / K052539) · `Chips/`(`Sn76489`, `Ym2413`, `Ym2151`, `OpnChip` = YM2203 · YM2608 · YM2612 · YM2610(B), `FmEngine` = 4-op FM 공통, `Okim6295`, `NesApu`(+ FDS), `NsfExpansion`(VRC6 · N163 · MMC5), `Huc6280Psg`) — 모두 `ISoundChip` (레지스터 쓰기 · 스테레오 샘플 · 채널 끄기 · 음높이 · 표시), `VgmPlayer`(VGM → 칩들), `VgmExport`(ROL · IMS · SOP · MIDI → VGM), `ChipSongs`(VGM · S98 · KSS · NSF · HES · SPC · IMF · HSC · CMF · DRO · RAW 열기, KSS 곡 목록), `Cpu/Z80` + `Kss`(KSS 드라이버 실행), `Cpu/Cpu6502`(2A03 · HuC6280) + `Nsf` · `Hes`, `Cpu/Spc700` + `Chips/SnesSpc`(S-DSP) + `Spc`, `NezM3u`(곡 목록 m3u), `SystemDefinitions`(시스템 정의 JSON), `AudioEngine`(스테레오 출력, 명령은 큐로 오디오 스레드에 넘기고 UI는 블록마다 남기는 스냅샷을 읽음), `AudioPlatform`(`IAudioOutput` · `IMidiOutDevice` · 디코더 끼우는 곳), `PlaybackOptions`(속도 · 키 · 채널 조절), `OplMapping` |
| `AdlibComposer.Nuked` | `NukedOpl3`: Nuked OPL3 1.8 (Nuke.YKT) C# 포팅. **LGPL-2.1 이상** (`src/AdlibComposer.Nuked/LICENSE`, 출력 폴더에 `LICENSE.Nuked-OPL3.txt`). 바꿔 끼울 수 있게 별도 어셈블리 |
| `AdlibComposer.Notation` | `Score` 모델, `ScoreBuilder`(Song→마디/음표, 붙임줄), `NoteSpelling`(음표 나누기·셋잇단·음 이름), `MusicXmlExporter`, `MidiExporter`, 텍스트 내보내기, `OrgelComb` / `OrgelBuilder`(곡 → 30음 오르골 띠 구멍) |
| `AdlibComposer.Audio.Windows` | Windows 구현: `WaveOutPlayer`(IAudioOutput, winmm), `WinMidiOut`(IMidiOutDevice, midiOut · SysEx), `MediaFoundationDecoder`(MP3 등 → 모노), `WindowsAudio.Register()` |
| `AdlibComposer.App` | WinForms GUI: `MainForm`, `Shell/`(가로 페이지 메뉴 · 공용 폴더/파일 목록 · hoot 목록 `HootBrowser` · `HootTree` · 왼쪽 목록 접점 `ISongList` · 로그 · 셸 테마), `ComposerView`, `InstrumentView`, `JukeboxView`, `NoteView`, `OrganizerView`, `PianoRollControl`, `RetroSlider` 등 |
| `tests/AdlibComposer.SelfTest` | 의존성 없는 자체 테스트 + 데모 WAV 렌더러 |

## 확장 방법

**곡 포맷 추가**
1. `ISongFormat`을 구현한 클래스를 만든다 (실제 예: `Formats/ImsFormat.cs`).
2. 파일 내용을 `Song`(보이스별 `Note`, 악기/볼륨/피치/템포 이벤트)으로 변환한다.
3. `FormatRegistry.CreateDefault()`에 `r.Songs.Add(new MyFormat());` 한 줄 추가.
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
- SOP를 Composer에서 열면 SOP 모드(보이스 20)로 편집 · 저장한다. 원래 파일과 바이트까지 같지는 않다 (같은 틱 이벤트 순서, 모드 1 트랙의 짝 트랙 채널 모드, 제목 · 설명, 길이 0 노트 → 실제 길이).
- SOP 멜로디 모드(퍼커션 아님) 파일은 열 수 있지만 메뉴로 만들 수는 없다. 이때 보이스 10 · 11은 드라이버 규칙대로 소리가 나지 않는다.
- SOP 칩 제어는 Ad262Sop 라이브러리(`_incoming/AD262SOP/AD262SOP.C`, 박진홍)를 옮겼다 (`SopPlayer` 주석 참고):
  트랙 0~8 = 첫 뱅크 채널, 11~19 = 둘째 뱅크 채널, 리듬 모드면 6~10 = BD SD TOM CY HH. 채널 모드 1 트랙(0·1·2·11·12·13)은 처음부터 4-op(+3 채널과 짝).
  음높이는 드라이버의 반음당 32단계 F-Number 표 (노트 60 = 가운데 도, 피치 100당 1반음), 볼륨 표 (63 − TL) × vol / 128, 팬 0 = 오른쪽(C0 0xA0) · 1 = 가운데 · 2 = 왼쪽(0x50).
  드럼은 BD만 자기 음높이, TOM 음이 바뀌면 SD 채널 = TOM + 7. 이벤트 1(특수)은 무시.
- 드라이버와 다르게 한 것 (재생 루프는 소스에 없음): 같은 트랙에서 겹친 노트는 키오프 후 다시 울림 (드라이버만으로는 BD가 다시 울리지 않음),
  리듬 모드가 아닐 때 트랙 9 · 10의 악기/팬은 무시 (드라이버는 채널 0 · 7 · 8을 덮어씀), 마스터 볼륨 = 트랙 볼륨 × 마스터 / 127.
- SOP 가사(같은 이름 ISS)는 IMS와 같은 240/박 기준 (SV6-GOSV.ISS 마지막 기록이 곡 끝과 맞음).
