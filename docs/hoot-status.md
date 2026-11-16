# hoot 재생 작업 현황 (2026-11-16, v2.0.0)

다른 PC(회사)에서 이어 하기 위한 기록. 자세한 할 일 목록은 원래 `_incoming/Todo.md`(git에 안 들어감)에 있었고, 그중 이어 하는 데 필요한 것을 여기에 옮겼다.

## 0. 이어 하기 전에

- **시험 자료는 저장소에 없다**: `_incoming/Hootarchive/` (hoot 배포본: `xml_new/` 목록 · `pc98/` 1,709 zip · `pc/` 480 zip · `snes/` · `msx/` · `nes/` · `gb/` …)는 `.gitignore`. 회사 PC에도 같은 자리(`_incoming/Hootarchive`)에 두면 실제 파일 시험 · 진단이 돈다. 없으면 실제 파일 시험은 `SKIP`으로 넘어간다 (합성 시험은 그대로 통과).
- 작업 방식: 단계마다 커밋 하나 (`vX.Y.Z: …` 한국어 메시지), `Directory.Build.props` 버전 마지막 번호 올림, README 갱신, SelfTest 전부 통과 후 커밋.
- 시험: `dotnet run -c Release --project tests/AdlibComposer.SelfTest` (전체, 지금 835개 통과), 묶음만: `-- --test pc98` · `hoot98` · `hootgen` · `hootmsx` · `pcat` · `hoot`.
- hoot 문서: http://retropc.net/yui/hoot/PC98.TXT (https 안 됨, http로). 그 밖의 기종(PC/AT · MSX · PC-88 · X1 …) 문서는 없음 — 목록의 시작 코드(rom "patch" 등)를 역어셈블해 규약을 맞춘다.

## 1. 지금까지 (v1.21.0 ~ v2.0.0)

| 판 | 단계 | 내용 |
|---|---|---|
| 1.21.0 | 7b-1 | x86 CPU (80286 리얼 모드) `Audio/Cpu/X86.cs` |
| 1.22.0 | 7b-2 | MS-DOS 흉내 `Dos/DosMachine.cs` · `DosFileSystem.cs` |
| 1.23.0 | 7b-3 | PC-98 기계 `Dos/Pc98Machine.cs` (PIC · PIT · OPN/OPNA · BEEP · hoot 포트 7E0h~) |
| 1.24.0 | 7b-4 | hoot 항목 → `.hoot` 파일 (zip을 풀 때) → Jukebox, 스스로 도는 칩 |
| 1.25.0 / .1 | — | 왼쪽 목록 **[hoot]** 트리 (제작사 → 장비 → 게임 → 곡), 곡 파일 없음 = 빨강 |
| 1.26.0 | 8-1 | SPC · KSS · NSF · GBS 항목을 곡 코드로 재생 (717항목 · 17,972곡) |
| 1.27.0 | 8-2 | PC-98 무음 고치기: 보드 INT5 (IRQ12), 마우스 타이머 IRQ13, 공용 드라이버, Shift_JIS 이름 |
| 1.28.0 | 8-3 | PC/AT DOS 기계 `Dos/PcAtMachine.cs` (AdLib OPL2 · SB16 OPL3 · 스피커) |
| 1.29.0 | 8-4 | MIDI 판: MPU-401 / MPU-PC98 (`Dos/Mpu401.cs`, UART + 지능 모드) → 살아 있는 MIDI 곡 |
| 1.29.1 | 8-5 | pc98vx · pc98 (DOS 없는 PC-98, PC-98generic): `HootPc98.BootGeneric` + `Pc98Machine.Generic` (7D0h I/O, 요인 0 → bgm 넣기 → 요인 1) |
| 1.29.2 ~ .7 | — | UI: Jukebox 첫 페이지 · 열기는 왼쪽 목록 / Jukebox만, 파일 안 이전 · 다음 곡, Note · Orgel 곡 길이 막대, 칩 곡 → 노트(`VgmToSong`, 진행 막대), 목록 찾기, 오류 기록 |
| 2.0.0 | 8-6 (MSX) | hoot msx generic: `Audio/HootMsx.cs` (MSX 기계 · hoot 포트 02h~07h, 미리 렌더) |

### 구조 (어디를 보면 되나)

- `Formats/HootArchive.cs` — hoot 목록 색인 (zip 이름 = `<romlist archive>`), `.hoot` 파일 쓰기 (`WriteEntries` · `WriteEntry`), 재생 가능 판단 (`Playable` · `PlayableGame` · `MidiPlayable`), `MissingRom`, `FindFile`. 표시 파일 `.fmopl_hoot4` (재생 가능 범위가 늘면 숫자를 올려 이미 푼 zip에도 다시 씀)
- `Formats/ZipFolder.cs` — zip 풀 때 `.hoot` 쓰기, `SourceZip(dir)` (푼 폴더 → 원래 zip)
- `Audio/HootSongs.cs` — `.hoot` 읽기를 드라이버별로: pc98dos · pcatdos → `HootPc98`, snes · msx · nes · gb → 그 파일 재생기, `IsMidi` / `LoadMidi`
- `Audio/Dos/HootPc98.cs` — 항목 → 기계 세우기 (`BootAny` · `BootDirAny`: rom 줄 file · conin · device · code · string · shell 차례로; pc98vx · pc98은 `BootGeneric`: code · binary(16진 패치) · string을 주소에, bgm · bgm2 · strlist는 `Pc98Machine.GenericData`로, bootcs:bootip에서 시작), `IHootMachine`, `HootDosChip` (기계를 품은 칩: OPN/OPNA · OPL2/OPL3 + BEEP), MIDI 판 `LoadMidi`
- `Audio/Dos/Pc98Machine.cs` · `PcAtMachine.cs` · `Pic8259.cs` · `Mpu401.cs` · `SharedPrograms.cs` (zip에 없는 셸 프로그램 = 같은 폴더 다른 zip)
- `Formats/MidiSong.cs` (`Extend` · `RenderedTicks` · `PrepareLive`) + `Audio/MidiPlayer.cs` — 살아 있는 MIDI 곡 (재생기가 1초 앞까지 오디오 스레드에서 채움)
- `App/Shell/HootTree.cs` · `FileBrowser.cs` ([hoot] 버튼) · `Views/JukeboxView.cs` (.hoot MIDI 판 → MIDI 재생, MT-32 판 강제)

### 지금 결과 (첫 곡 기준)

| 대상 | 소리 남 | 측정 |
|---|---|---|
| PC-98 DOS FM (opn · opna · 86 · beep) | 300 zip 표본 226 / 301 (약 75%) | `--hoot98survey pc98 300 6` |
| PC-98 pc98vx · pc98 FM (opn · opna · 86 · beep) | 354항목 중 314 (약 89%) — vx opn 168/198 · opna 18/20 · 86 9/9 · beep 36/40, pc98 opn 77/81 | `--hoot98survey _vx 400 6` (pc98vx · pc98 항목이 든 zip 285개를 따로 모은 폴더) |
| MIDI 판 pc98vx · pc98 | 97 / 109 | `--hootmidisurvey _vx 400 20` |
| PC/AT DOS (adlib · sb16 · beep) | 482 / 488 | `--hootatsurvey pc 1000 6` |
| MIDI 판 PC/AT | 571 / 586 | `--hootmidisurvey pc 1000 20` |
| MIDI 판 PC-98 | 360 / 641 | `--hootmidisurvey pc98 1709 20` |
| MSX generic (PSG · OPLL · SCC · SCC+ · MSX-AUDIO) | 481 / 498 (첫 곡이 조용하면 다음 곡 5개까지) | `--hootmsxsurvey msx 1000 8` |
| SPC · KSS · NSF · GBS | 717항목 파일 있음 (KSS 71항목은 zip에 .kss 없음) | — |

"소리 남" = 처음 몇 초 안에 키온 · 노트가 나옴. 템포 · 음높이가 hoot와 맞는지는 아직 귀로 비교하지 않았다.

### 진단 명령 (SelfTest)

```
--hoot98survey  <pc98 폴더> [zip 수] [초]   PC-98 FM: 원인 분류 (vector-not-hooked · hoot-disabled · writes-no-keyon · no-chip-writes · unknown-op · shell-not-found · no-memory)
--hoot98trace   <zip> [항목 번호] [초]       1초마다 PIC · PIT · OPN 타이머 · 인터럽트 · BIOS/DOS 호출 · 포트, 도는 곳 바이트, 못 연 파일, MPU 명령
                환경 변수 HOOT_MIDI=n (n번째부터 MIDI 바이트), HOOT_STEP=세그먼트 (명령 단위 추적, 느림)
--hootatsurvey  <pc 폴더> [zip 수] [초]      PC/AT
--hootmidisurvey <폴더> [zip 수] [초]        MIDI 판
--hootmsxsurvey <msx 폴더> [zip 수] [초]    MSX generic: 원인 분류 (boot-not-waiting · no-chip-writes · writes-no-keyon · missing-file), 조용하면 많이 쓴 레지스터
--hoottree      <xml 폴더>                   트리 자료 읽는 시간 · 장비 수
```

## 2. 다음 할 일 (8단계 계획, 품 대비 곡 수 순서)

### 8-5. pc98vx (DOS 없는 PC-98) — ✅ v1.29.1
- `HootPc98.BootGeneric`: DOS 없이 code (파일을 주소에) · binary (16진 바이트 패치, 아니면 파일) · string, addressing (목록에 `adressing`으로도 씀) 0 = 선형 · 1 = 세그먼트 << 16 | 오프셋, bootcs:bootip에서 시작, 7E8h ← 81h + IF가 켜질 때까지 (최대 2억 클럭) 돌린다. pc98 = PIC 자동 EOI, 버스트 클럭 모드는 흉내 내지 않음
- `Pc98Machine.Generic`: hoot 인터럽트를 F000:0420 함정 (F1 0201h + IRET)을 거쳐 넣어 처리기가 돌아온 때를 안다 (1초 넘게 안 돌아오면 돌아온 것으로). 재생 = 요인 0 → bgm / bgm2 (곡 코드 아래 16비트 번호)를 dataaddr / data2addr에 filesize까지 → 넣었으면 요인 1. 7D0h 기능 10h · 11h · 18h · 20h · 21h · 30h · 31h, 상태 0 / FFh
- MPU-PC98은 MIDI 판에만 (`Pc98Machine.MpuPresent`): FM 판에 MPU가 보이면 Wolfteam 등 드라이버가 MIDI로만 냄 (pc98dos는 아직 늘 꽂혀 있음 — 같은 문제가 있는지 확인할 것)
- titlelist에 제목 없이 range만 있으면 range = 곡 목록 (`HootArchive.Parse`), 진단은 bgm이 있는 첫 곡 · "Stop"이 아닌 첫 곡을 돌림
- 남은 무음 (40): writes-no-keyon 19 (sorc98v2/v3 · biblem · lesserm98 · dorse_sa · eldmnon · yaksa · ed3r …), no-chip-writes 13 (schwarz 1~3 · navitn · arugies · wg2 · trader(포트 43h) · tentochi · aftdark · fh98 · fukujin1 · rolw1), unknown-op 2 (dolly · revery: 0000:0063 — 벡터 표 자리 실행), vector-not-hooked 2 (valis2), hoot-disabled 2 (jnbgszm · mv2)
- 아직 없음: dummysndrom (26K 사운드 BIOS ROM 흉내, 63항목이 씀), amd98 · soundorchestra · rashin · burai 형식, fm_mix · ssg_mix

### 8-6. Z80 기계들 — 큼 (Z80 · 칩은 다 있음, 기종별 hoot 규약을 찾아야 함)
- 대상: msx generic 498 / 6,873곡, pc88 opn 389 · opna 142, x1 psg 83 · opm 71, neogeo 146, megadrive generic_z80 · iyinc · koei 145 — 합계 약 1,500항목 · 2.9만 곡
- **MSX generic — ✅ v2.0.0** (`Audio/HootMsx.cs`, 481 / 498). 문서가 없어 시작 코드 424개를 훑어 맞춘 규약:
  - hoot 포트: IN 02h = 명령 (1 = 재생, 그 밖 = 정지; 읽으면 비움 — 곡이 코드에 든 시작 코드는 OUT 03h를 안 쓰고 다시 기다림), OUT 02h = 받음, IN 03h~06h = 곡 코드 바이트 0~3 (04h는 템포 나눗수 등으로 씀), OUT 03h,n = bgm n을 mdata_addr에 (mdata_size까지 0, mfile_size까지 복사), IN 07h = use_opll이면 1
  - 기계: 64KB RAM, VDP 60Hz (IM 1 / IM 2, 99h 읽으면 받음), 작은 BIOS (0038h → 0060h 처리기: H.KEYI FD9Ah · H.TIMI FD9Fh, WRTPSG · RDPSG · GICINI · RDSLT · WRSLT · CALSLT · ENASLT). 칩: PSG A0h~, OPLL 7Ch · 7Dh, MSX-AUDIO C0h · C1h, SCC (9000h ← 3Fh, use_scc면 늘), SCC+ (BFFEh 모드 → B800h~)
  - rom: code (주소를 넘는 큰 rom = 16KB 뱅크 카트리지, FM-PAC 64KB는 7FF7h 뱅크), binary, sccp (Konami SCC+ 카트리지 RAM → 4000h~), bgm. fmbios · rom 형은 아직
  - 곡: 재생 명령부터 180초를 미리 렌더 (끝 4초 줄임, 부팅 중 칩 설정은 0초에) → VGM
  - 남은 무음 17: Cybernetic Hi-School 1~3 (PSG R7만 씀), Golvellius 2 · Rona (칩 쓰기 없음), Ultima IV · mbsp · herzog (볼륨 0), Nemesis SCC+ (PSG만 볼륨 0), 銀河英雄伝説 OPLL/PSG (OPLL 16h~ 쓰는데 키온 없음), Illusion City, Tanteidan X (부팅이 명령 대기에 안 닿음), 福神漬, Disk NG
- 다음: pc88 (Z80 + OPN/OPNA, 포트 44h~47h · A8h~), x1, neogeo, megadrive

### 8-7. X68000 — 큼 (MC68000 CPU 새로)
- x68k generic 553 / 11,679곡 (.OPM · .ZMD · .M …를 원래 드라이버로), MIDI 판 166 (8-4의 MIDI 길 다시 씀)

### 8-8. 아케이드 · 그 밖 — 아주 큼, 나중 (보드마다 CPU · 칩이 다름, 약 600항목)

### 남은 문제 (단계와 따로)
- PC-98 FM 무음 (300 zip 표본): vector-not-hooked 25 (98stdium · g3: 0으로 찬 메모리로 뛰어감), writes-no-keyon 24 (FUGA fgplay_h · MAKO · ARTDI_98이 hoot 인터럽트를 끔), unknown-op 9 (386 접두어 66h · 65h), no-chip-writes 8, koroshi 같은 이름 불일치 (xml = file01.MUS, zip = 일본어 원래 이름)
- PC-98 MIDI 무음 (드라이버별): DOFMDX98 50 · FUGA 50 · ARTDI 32 · FMX 16 · MINT/NLP 16 · MFD 12 · valky 11, RS-MIDI (내장 8251) 없음
- PC/AT: 무음 6 (rakuen · kurodan · eshoutei · darksun · globaleffect · nestory2), SB16 PCM (DSP DMA) 없음, Game Blaster 8 · Tandy 4 (칩은 있음: SAA1099 · SN76489), CPU 기준 클럭 (1.193MHz × clockmul로 추정) 확인
- 공통: 이동(seek) = 기계를 처음부터 그만큼 돌림 (오디오 스레드에서 기다림) → 상태 저장 · 되돌리기, 곡 길이 3분 고정 → 끝 · 되풀이 찾기, 86 PCM · OPNA ADPCM, fm_mix · ssg_mix 옵션, 트리 장비 이름 정리 (PC-98 / PC-9801 …), zip은 있지만 .kss가 없는 KSS 71항목을 트리에서 미리 빨강으로
