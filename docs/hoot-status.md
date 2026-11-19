# hoot 재생 작업 현황 (2026-11-20, v2.1.9)

다른 PC(회사)에서 이어 하기 위한 기록. 자세한 할 일 목록은 원래 `_incoming/Todo.md`(git에 안 들어감)에 있었고, 그중 이어 하는 데 필요한 것을 여기에 옮겼다.

## 0. 이어 하기 전에

- **시험 자료는 저장소에 없다**: `_incoming/Hootarchive/` (hoot 배포본: `xml_new/` 목록 · `pc98/` 1,709 zip · `pc/` 480 zip · `snes/` · `msx/` · `nes/` · `gb/` …)는 `.gitignore`. 회사 PC에도 같은 자리(`_incoming/Hootarchive`)에 두면 실제 파일 시험 · 진단이 돈다. 없으면 실제 파일 시험은 `SKIP`으로 넘어간다 (합성 시험은 그대로 통과).
- 작업 방식: 단계마다 커밋 하나 (`vX.Y.Z: …` 한국어 메시지), `Directory.Build.props` 버전 마지막 번호 올림, README 갱신, SelfTest 전부 통과 후 커밋.
- 시험: `dotnet run -c Release --project tests/AdlibComposer.SelfTest` (전체, 지금 890개 통과), 묶음만: `-- --test pc98` · `hoot98` · `hootgen` · `hootmsx` · `hoot88` · `hootx1` · `hootneo` · `hoottaito` · `hootopm` · `hootarcade` · `hootdeco` · `hootmd` · `hootx68` · `pcat` · `hoot`.
- hoot 문서: http://retropc.net/yui/hoot/PC98.TXT (https 안 됨, http로). 그 밖의 기종(PC/AT · MSX · PC-88 · X1 …) 문서는 없음 — 목록의 시작 코드(rom "patch" 등)를 역어셈블해 규약을 맞춘다.

## 1. 지금까지 (v1.21.0 ~ v2.1.9)

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
| 2.0.1 | 8-6 (PC-88) | hoot pc88 opn · opna: `Audio/HootPc88.cs` (PC-8801 기계 · hoot 포트 00h · 01h · 80h, IM 2 VRTC · RTC · OPN 타이머) |
| 2.0.2 | 8-6 (X1) | hoot x1 psg · opm: `Audio/HootX1.cs` (X1 기계 · 16비트 I/O · Z80 CTC · I/O VRAM) |
| 2.0.3 | 8-6 (네오지오) | hoot neogeo: `Audio/HootNeoGeo.cs` (사운드 보드 Z80 + YM2610, M1 ROM 그대로, NMI 명령 · 뱅크 · ADPCM ROM 블록) |
| 2.0.4 | 8-6 (메가드라이브) | hoot megadrive generic_z80 · iyinc: `Audio/HootMegaDrive.cs` (Z80 사운드 쪽 · 카트리지의 Z80 드라이버를 RAM에, 요청 주소) |
| 2.0.5 | 8-7 (X68000) | MC68000 `Audio/Cpu/M68000.cs` + hoot x68k generic: `Audio/HootX68k.cs` (OPM · ADPCM · DMA · MFP, E00010h = hoot가 대신 하는 IOCS · DOS) |
| 2.0.6 | 8-7 (X68000 MIDI) | x68k MIDI 판: YM3802 MIDI 보드 (EAFA00h) → 살아 있는 MIDI 곡 (`HootX68k.LoadMidi`) |
| 2.1.9 | 8-8d (데이터 이스트) | hoot dataeast HuC6280 보드: `Audio/HootDeco.cs` (HES의 HuC6280 다시 씀, YM2151 + YM2203 + OKI 둘), `HootOkiVoices` (OKI 음성 상태), VGM 듀얼 OKIM6295 |
| 2.1.8 | 8-8c (System 1 · M72) | hoot sega system1 (`HootSegaSystem1.cs`, SN76489 2MHz + 4MHz → VGM 듀얼 SN76489) · irem m72 계열 (`HootM72.cs`, RST_NEG 인터럽트, transfer, 핸드셰이크 00h) |
| 2.1.7 | 8-8b (YM2151 + OKIM6295) | hoot capcom cps1 · technos ddragon2 · ddragon3 · ctribe: `Audio/HootOpmBoard.cs` (보드 배치 표, YM2151 타이머 IRQ, OKI 음성 상태) |
| 2.1.6 | 8-8a (타이토 YM2610) | hoot taito f2system · bsystem · dual68 · xsystem · fx1a · sonicb: `Audio/HootTaito.cs` (Z80 + YM2610(B) + TC0140SYT, 소리 켬 EFh) |

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
| PC-88 (opn · opna) | 487 / 527 — opn 357/385 · opna 130/142 | `--hoot88survey pc88 1000 6` |
| X1 (psg · opm) | 143 / 152 — opm 65/71 · psg 78/81 | `--hootx1survey x1 1000 8` |
| 네오지오 | 137 / 146 (음악일 만한 코드 10h 이상 3곡까지) | `--hootneosurvey roms 200 6` |
| 타이토 YM2610 보드 (f2 · b · dual68 · x · fx1a · sonicb) | 67 / 67 (첫 곡이 조용하면 다음 곡 3개까지) | `--hoottaitosurvey roms 200 6` |
| CPS-1 · Double Dragon 2 · 3 (YM2151 + OKIM6295) | 39 / 39 (FM 키온 16개 또는 OKI 2번 이상, 처음 6곡까지) | `--hootopmsurvey roms 200 6` |
| 세가 System 1 · 아이렘 M72 계열 | 52 / 52 — system1 29/29 · m72 23/23 | `--hootarcadesurvey roms 200 6` (HOOT_ONLY=zip이름,…) |
| 데이터 이스트 HuC6280 보드 | 41 / 43 — 빠진 2: wizdfire (zip에 사운드 ROM 없음), hangzo (시제품, 곡 목록이 틀뿐) | `--hootdecosurvey roms 300 6` |
| 메가드라이브 (generic_z80 · iyinc) | 93 / 131 (smd zip에 있는 것) — generic_z80 70/108 · iyinc 23/23 | `--hootmdsurvey smd 400 6` |
| X68000 generic (OPM · ADPCM) | 499 / 553 — generic 328/370 · mfp 171/183 | `--hootx68survey X68k 1000 6` |
| MIDI 판 X68000 | 141 / 166 (노트) | `--hootx68midisurvey X68k 1000 10` |
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
--hoot88survey  <pc88 폴더> [zip 수] [초]   PC-88: 원인 분류 (no-interrupts 추가)
--hootx1survey  <x1 폴더> [zip 수] [초]     X1: CTC 채널마다 제어어:시간 상수 · 벡터도
--hootneosurvey <roms 폴더> [zip 수] [초]   네오지오 (hoot 목록에 neogeo로 있는 zip만)
--hoottaitosurvey <roms 폴더> [zip 수] [초]  타이토 YM2610 보드 (환경 변수 HOOT_PC=1 · HOOT_PCQ=주소,… · HOOT_RAM=주소,길이 · HOOT_WATCH=주소 · HOOT_REGS=1로 PC 분포 · RAM · 읽은 곳 · 레지스터)
--hootopmsurvey <roms 폴더> [zip 수] [초]   CPS-1 · Double Dragon 2 · 3 · Combatribes (HOOT_PC · HOOT_REGS)
--hootarcadesurvey <roms 폴더> [zip 수] [초] 세가 System 1 · 아이렘 M72 계열 (HOOT_ONLY · HOOT_PC · HOOT_PCQ, 타이머 레지스터 표시)
--hootdecosurvey <roms 폴더> [zip 수] [초]  데이터 이스트 HuC6280 보드 (HOOT_ONLY · HOOT_PC)
--hootmdsurvey  <smd 폴더> [zip 수] [초]    메가드라이브 Z80 드라이버
--hootx68survey <X68k 폴더> [zip 수] [초]  X68000: 원인 분류 (boot-not-waiting · exception-N …), 처리 못 한 IOCS · DOS 부름 횟수
--hootx68midisurvey <X68k 폴더> [zip 수] [초]  X68000 MIDI 판: 보낸 바이트 · 노트, YM3802 레지스터별 마지막 값 (환경 변수 HOOT_ONLY=zip이름,… 로 고르기)
--hootx68trace  <zip> [곡 번째] [초]         X68000: 예외 · hoot 부름 (IOCS · DOS의 D0 · D1 · A1) · MFP 쓰기, 1초마다 상태
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
- **PC-88 opn · opna — ✅ v2.0.1** (`Audio/HootPc88.cs`, 487 / 527). 시작 코드 108개 + 실패 항목을 보고 맞춘 규약:
  - hoot 포트: IN 00h = 명령 (읽으면 비움), OUT 00h = 받음, IN 01h = 곡 코드 아래 바이트 (02h · 03h = 그 위), OUT 01h,n = bgm n을 mdata_addr에 (**없으면 4000h**, 0도 주소) + voice n을 vdata_addr에 (vdata_size · vfile_size), IN 80h = 곡 코드 맨 위 바이트 (음색 묶음 · 변형)
  - 기계: Z80 3.9936MHz (clock_mul 2 = 2배), 64KB RAM, init_pc 없으면 0에서 시작. 인터럽트 IM 2 (I·256 + 레벨×2): VRTC 레벨 1 (60Hz, use_vrtc 또는 E6h bit1), RTC 레벨 2 (600Hz, use_rtc 또는 E6h bit0), OPN 타이머 A · B 레벨 4 (27h). **벡터 칸이 0이면 넘김** (N88-BASIC이 채우던 F300h 표를 시작 코드가 일부만 씀). E4h 레벨 · 32h SINTM은 무시
  - 칩: OPN 44h · 45h (44h 읽기 = 타이머 상태, FFh = ID), OPNA 46h · 47h. VGM = YM2203 / YM2608
  - 남은 무음 40: 인터럽트 없음 6 (byouin_88 · gallforc · hardrank · p1demo1 · shuten88 · tnmbox_88 — VRTC를 E6h 없이 쓰거나 OPN 타이머 인터럽트 켜는 길이 다름), 부팅이 명령 대기에 안 닿음 3 (prajator · rst88v1 · skugyoku), 칩 쓰기 없음 1 (thexder88), 파일 없음 1 (wingsp88 — 반각 가나 이름), 키온 없음 23 (100yen2 · 4 · andrgyns · blassty · castle · castleex · daikokai · europe · ginga · inindou · kami · liberty_88 · lizard88 · lmpereur · luxsor · mazekun · saziri · snatcher · spitfl88 · xzr · yakyufan · yokosuka …: 대부분 인터럽트 몇 번 뒤 PC가 0 근처 → 벡터 · BASIC ROM 루틴을 기대)
  - 아직 없음: adpcm rom (OPNA ADPCM 메모리), file · shell · conin · prog (디스크 BASIC), use_n88rom, pc88va · pc88vados, 8801-10 · 8801-11 등 형식
- **X1 psg · opm — ✅ v2.0.2** (`Audio/HootX1.cs`, 143 / 152). 시작 코드 153개 + 실패 항목으로 맞춘 규약:
  - I/O는 16비트 주소 (OUT (C) = BC). hoot 포트 (상위 바이트 0): IN 0000h = 명령, OUT 0000h = 받음, IN 0001h = 곡 코드 아래 바이트, OUT 0001h,n = bgm n (mdata_addr, 없으면 4000h) + voice n (vdata_addr), IN 000Fh = 곡 코드 맨 위 바이트
  - Z80 CTC (본체 1FA0h~, FM 보드 0704h~) IM 2: 채널 0 벡터 바이트, 제어어, 타이머 = 4MHz ÷ 16/256 ÷ 시간 상수, 채널 3 카운터 = 채널 0 출력 (X1 배선). **시간 상수를 받으면 리셋(bit1)이 풀림**, 읽기 = 남은 수 (x1rv가 CTC 있는지 써 보고 읽음). 옵션 use_ctcN = 시작 코드가 시간 상수를 안 쓸 때의 값 (x1wibarm ch3 = 80h → 61Hz)
  - 칩: PSG 1C00h 레지스터 · 1B00h 데이터 (2MHz), OPM 0700h · 0701h (4MHz). **FM 보드 (OPM · 0704h CTC)는 opm 판에만** (PSG 판에 보이면 드라이버가 OPM으로 냄)
  - **I/O 2000h~FFFFh = VRAM (쓰고 읽는 메모리)**: Sorcerian 등이 그래픽 VRAM을 작업 메모리로 씀. 8255 PPI 1A01h = bit7 수직 귀선 · 서브 CPU 늘 준비됨, 서브 CPU 1900h 읽기 = 0
  - 남은 무음 9: Super Laydock (OPM · PSG), 三国志 1 · 2, 維新の嵐, 戦国群雄伝, 水滸伝 (KOEI — 인터럽트가 없거나 ch3만 시간 상수 1), Hydlide (ch3가 시간 상수를 기다림, 옵션 없음)
  - 아직 없음: opmx2 (OPM 둘) 2, x1 opn 1
- **네오지오 — ✅ v2.0.3** (`Audio/HootNeoGeo.cs`, 137 / 146). hoot 전용 시작 코드 없음 — 실제 사운드 보드 (MAME neogeo와 같은 배치):
  - Z80 4MHz, 0000h~7FFFh M1 고정, 8000h~ · C000h~ · E000h~ · F000h~ = 16 · 8 · 4 · 2KB 뱅크 창 (IN 0Bh · 0Ah · 09h · 08h의 주소 상위 바이트, 처음 02h · 06h · 0Eh · 1Eh), F800h~ RAM
  - 명령 = NMI (00h 읽기, 08h 쓰기 = NMI 켬 · 18h = 끔), 0Ch = 68000에 답. YM2610 04h~07h, 타이머 A · B (8MHz: A = 144 × (1024 − TA), B = 2304 × (256 − TB) / 8MHz) → IRQ IM 1 (플래그를 지울 때까지)
  - **SNK 사운드 드라이버 (Mr.Pac Ver 2.0 · MAKOTO Ver 3.0 · "Sound Driver Ver … To SNK")는 처음에 음악이 꺼져 있어 명령 08h를 먼저 보낸다** (aof: 곡 시작 루틴이 FE34h ≠ 0이면 건너뜀, 명령 08h · 09h가 지움). 다른 드라이버 (ADK maglord · mslug 등)는 보내지 않음 (mslug는 받으면 안 남). 옵션 code_play가 있으면 곡 명령 0.1초 뒤 그 명령도
  - VGM = YM2610 + ADPCM-A ROM (82h) · ADPCM-B ROM (83h, adpcmb가 없으면 A ROM을 같이) 데이터 블록. 180초 미리 렌더
  - 남은 무음 9: 파일 없음 3 (diggerma · kof2001 · zupapa — zip에 m1 · v1 없음), m1_decrypt (mslug5), bstars · ganryu · pulstar (MAKOTO인데 08h로도 안 됨), s1945p (IRQ 없음), wjammers (음수 offset 줄)
  - **암호화 V ROM (neo_pcm2_swap · neo_pcm2_snk_1999) 10항목**: 키온은 나지만 ADPCM 소리가 깨짐 (복호 아직)
- **메가드라이브 generic_z80 · iyinc — ✅ v2.0.4** (`Audio/HootMegaDrive.cs`, 93 / 131). 68000은 흉내 내지 않고 Z80 사운드 쪽만:
  - Z80 3.58MHz, 0000h~1FFFh Z80 RAM (거울 ~3FFFh), 4000h~4003h YM2612 (읽기 = 타이머 플래그 — 메가드라이브는 YM2612 IRQ가 없어 드라이버가 상태를 봄, A = 144 × (1024 − TA), B = 2304 × (256 − TB) / 7.67MHz), 6000h 뱅크 (bit0을 9비트로 밀어 넣음), 7F11h PSG, 8000h~ = 카트리지 32KB 창, 수직 귀선 60Hz INT (IM 1)
  - hoot 옵션: 카트리지 ROM의 topaddr부터 size1 (· size2 …) 바이트를 Z80 RAM data1 (· data2 …)에 올리고 0.5초 돌린 뒤, 곡 번호를 reqaddr에 (**없으면 1FFFh** — iyinc · lordmon 본 고리가 1FFFh를 봄), trigadr · trigvalue (trigsubadr · trigsubval)를 씀. 처음 뱅크 = topaddr의 32KB 칸. VGM = YM2612 + SN76489 (세가 되먹임)
  - reqaddr = 1FFFh 항목은 모두 소리 남. 남은 무음 38은 다른 요청 주소를 쓰는 generic_z80 (드라이버마다 요청 방식이 다름 — verytex는 Z80 안에 사운드 테스트 UI가 있어 버튼 입력 0104h로 고름, madoumon은 곡 0 = 요청 없음): advdaisn · arrow · bahamuts · columns1 · columns3j · dahna · ddanpei · djboyj · dynduke · earnest · elvientoj · eswatj · f1circus · kujaku2 · leynos · magichat · mazinsagj · miracle9 · nhktaidr · nikkan · rastan2j · runark · sangoret · shikinjo · shougi · shuramon · smasters · soldeace · sswordj · supermanu · surging · topgear2 · verytex · volfied · waniwani · wboy3 · wimbled · zoom
  - 아직 없음: koei 15 (Z80 드라이버 위치 옵션이 없음), 게임별 형식 수십 개 (68000 드라이버: puyopuyo · sonic · gunstar …), smd 폴더에 없는 generic_z80 29 (g_* 아카이브)
- 8-6 끝 (MSX · PC-88 · X1 · 네오지오 · 메가드라이브). 다음: 8-7 X68000 (MC68000 새로) — ✅ v2.0.5 · MIDI 판 v2.0.6. 다음: PCM8, 또는 8-8

### 8-7. X68000 — ✅ v2.0.5 (FM · ADPCM 판, `Audio/HootX68k.cs`, 499 / 553)
- **MC68000** (`Audio/Cpu/M68000.cs`): 명령 전부 (BCD · MOVEM · MOVEP · 비트 · 시프트 · 예외 · 사용자 모드). 무작위 명령 약 2만 개를 Unicorn (QEMU)과 맞춰 봄 — 다른 것은 Unicorn 쪽 잘못 (68020 확장 워드 · −(A7) 바이트 걸음 · 메모리 ASR) 이나 정해지지 않은 깃발 (DIV 넘침 N · 잘못된 BCD · CHK, Musashi와 같게). 홀수 주소 워드 접근은 주소 오류 대신 바이트 둘 (bakkon 등이 그렇게 돌아야 함, `AddressErrors` 끔)
- 시작 코드 ("X680x0 SOUND DRIVER EMULATOR for 'hoot'", 442가지)를 읽어 맞춘 규약:
  - 0번지 벡터 표 (SSP 00F0FFFEh), 리셋 뒤 400h~ 표 (초기화 · 재생 · 타이머)를 부르고 E00000h를 기다림. E00000h = 명령 (CLR로 비움), E00001h · E00002h = 곡 코드, E00800h 읽기 = 쉼 (다음 일까지 건너뜀 — 180초가 약 1초), E00801h · E00802h = midiout · midiout_type
  - **E00010h 쓰기 = hoot 부름**: 2Fh = IOCS (TRAP #15를 시작 코드가 처리 안 한 번호), FFh = DOS (F줄 FFxxh, 쌓인 PC +2), 22h = PCM8 (TRAP #2, 아직 없음), 그 밖 = 예외 보고 (그 뒤 시작 코드는 쉼 고리)
  - IOCS: ADPCMOUT · AOT · LOT · SNS · MOD (DMA 채널 3), OPMSET · OPMSNS, OPMINTST (벡터 43h — mfp 없는 판이면 OPM 인터럽트를 벡터 43h로: 자동 벡터 처리기가 RTE뿐인 시작 코드), TIMERDST · VDISPST (MFP 타이머 D · A를 켬), B_INTVCS (100h~ = IOCS 표 400h~), DMAMOVE · A · L, ONTIME. DOS: _INTVCS · _INTVCG · _SUPER · _MALLOC · _OPEN · _READ · _SEEK · _CLOSE (zip 안 파일) · _BUS_ERR (확장 보드 자리는 없음) · _VERNUM
  - OPM IRQ: mfp 없는 판 = 타이머가 넘칠 때마다 레벨 6 자동 벡터 하나 (레벨로 두면 처리기가 없을 때 인터럽트 폭주). mfp 판 = MFP GPIP3 (가장자리), VR 처음 40h
  - rom: code · data, x (Human68k 실행 파일: 머리말 64바이트 빼고 text · data, bss 0, 재배치). zip에 없는 파일은 같은 폴더 다른 zip에서 (`SharedPrograms`가 이제 모든 이름을 색인)
- 남은 무음 54: boot-not-waiting 32 (같이 쓰는 zmusic.x · cos68snd.bin이 다른 판이라 데이터로 뛰어듦 — jotei3 · 4 · drkysmky · carat · conti · himihana · mayumi · will · xix …, ao: 시작 코드가 FM_DRV.68K 가운데로 JSR, arcus2 · mai: F000h · FD80h F줄, tt: A줄), missing-file 10, writes-no-keyon 10, 예외 2
- **MIDI 판 — ✅ v2.0.6** (141 / 166): YM3802 (CZ-6BM1, EAFA01h~EAFA0Fh 홀수 = R00~R03 · 묶음 R01의 Rg4~Rg7), midiout이 있을 때만 보드가 있음 (_BUS_ERR도). R56 (TDR) = 보낸 바이트, TSR R54 = 늘 빔, 인터럽트 레벨 4 · 벡터 = R04 & E0h + 원인 × 2. 범용 타이머 (R84 · R85, 1MHz로 셈, ISR bit7)는 있지만 쓰는 곡이 없음 — 거의 모든 드라이버가 OPM · MFP 타이머로 템포를 잡고 IER(R06) = 0. TX 빔 인터럽트는 없음 (bit6으로 걸면 robokid가 맴돔). 곡 = 8-4와 같은 살아 있는 MIDI 곡 (MIDI만, 같이 나는 FM · ADPCM은 버림), midiout_type → MT-32 / GS / GM은 PC-98과 같게
  - 남은 무음 25: missing-file 16 (ZML · ZMG 등 zip에 없음), bytes-no-note 6, boot-not-waiting 2 (geogseal: 무엇을 기다리는지 모름), no-midi-bytes 1
- 아직 없음: PCM8 (TRAP #2, pcm8 옵션 12항목 — ADPCM 여러 채널 섞기, VGM으로는 MSM6258 하나뿐이라 새 길이 필요), mxdrv 형식 3, 래스터 · H-SYNC 인터럽트

### 8-8. 아케이드 · 그 밖 — 보드마다 CPU · 칩이 다름 (약 600항목). 있는 CPU · 칩으로 되는 보드부터 하위 단계로

- 있는 것으로 되는 보드 (zip 있는 항목): 타이토 Z80 + YM2610 ≈ 67 → **8-8a ✅**, Z80 + YM2151 + OKIM6295 (capcom cps1 29 · technos ddragon2 7 · …) ≈ 40, irem m72 (Z80 + YM2151 + DAC) 23, sega system1 (Z80 + SN76489 × 2) 29, Z80 + YM2203 계열 (taito 소품 · dataeast …)
- 새 칩 · CPU가 필요: capcom cps2 (QSound) 38, taito f3system (68000 + ES5505) 40, namco system1 · 2 (6809 + YM2151 + C140), sega system16 (YM2151 + uPD7759), konami (K053260 · K054539), model2 · 3 · zn …
- **8-8a. 타이토 YM2610 보드 — ✅ v2.1.6 (67 / 67, `Audio/HootTaito.cs`)**
  - 기계 (MAME taito_f2 · taito_b · taito_h · ninjaw · taito_x · taitofx1): Z80 4MHz, 0000h~3FFFh 고정, 4000h~7FFFh 뱅크 (F200h = n → ROM n × 4000h, MAME의 (n − 1) & 7 + CONTINUE와 같은 자리), RAM C000h~DFFFh, YM2610 E000h~E003h (E000h 읽기 = 타이머 플래그), E400h · EA00h … 읽기 = 0
  - TC0140SYT (슬레이브): E200h = 모드, E201h 읽기 = 모드 0~3 니블 (1 · 3 읽으면 포트 01 · 23 다 읽음), 모드 4 = 상태 (bit0 포트 01 참 · bit1 포트 23 참), 쓰기 모드 0~3 = 68000에 답 (버림), 5 · 6 = NMI 끔 · 켬. 쓴 드라이버들은 모두 NMI를 끄고 타이머 IRQ 안에서 상태를 읽는다
  - 타이토 드라이버 (Kitaro 계열)는 처음 소리가 꺼져 있음 — 명령 EFh = 소리 켬 (CF2Ch = 7), EEh = 끔. 처리 코드 `FE EF C0 3E`가 있으면 부팅 뒤 EFh를 먼저 보냄. 명령 3Eh 미만 = 곡, C0h~EFh = 볼륨 · 팬 등, F0h~ = 특수
  - 곡 코드 = 포트 0 · 1 (낮은 · 높은 니블), 256 이상 (fx1a Magical Date EX 0x1A4 …)은 위 바이트를 포트 2 · 3에 먼저
- **8-8b. Z80 + YM2151 + OKIM6295 보드 — ✅ v2.1.7 (39 / 39, `Audio/HootOpmBoard.cs`)**
  - 배치 표 `Layout` (MAME cps1 · ddragon · ddragon3): CPS-1은 래치 F008h를 IRQ 안에서 앞 값과 비교 (68000이 쓰지 않을 때 FFh), 뱅크 F004h bit0 → ROM 8000h + n × 4000h (MAME CONTINUE 자리), OKI 1MHz. DD2 · Combatribes · DD3은 래치를 쓰면 NMI, OKI 1.056MHz, DD3만 OKI 뱅크 E800h (VGM OKI 레지스터 0Fh)
  - YM2151 타이머 A (64 × (1024 − TA) / 클럭) · B (1024 × (256 − TB) / 클럭), 상태 = 플래그 (바쁨 없음). OKI 읽기 F0h | 울리는 음성 — 구절 표 (구절 × 8, 시작 · 끝 18비트)의 길이 ÷ (클럭 / 132)로 계산
  - 곡 목록 첫 줄이 "Stop the Music" (F0h)인 게임이 있어 조사 기준을 FM 키온 16개 · OKI 2번 이상으로 조임
- **8-8c. 세가 System 1 · 아이렘 M72 계열 — ✅ v2.1.8 (52 / 52)**
  - System 1 (MAME system1 sound_map): RAM 8000h~ (2KB 거울), SN76489 #1 A000h = 2MHz · #2 C000h = 4MHz (SN76489A: 되먹임 0006h · 15비트), 명령 E000h~ 읽기 · 보낼 때 NMI, IRQ = 화면 줄 32부터 64줄마다 (HOLD_LINE). VGM은 같은 칩 둘 (클럭 bit30, 명령 30h · 3Fh)로 쓰고 곡 객체에 Sn76489Clock2 = 4MHz — VgmFormat · VgmPlayer에 표준 듀얼 SN76489 읽기 · 재생을 더함 (파일로 내보내면 두 칩이 같은 클럭이 됨)
  - irem m72 형식은 여러 보드: M72 · M75 · M77 · M81 = 포트 02h · 06h, M82 · M84 · M90 · M97 = 80h · 83h (MAME rtype2_sound_portmap), New Dyna Blaster · Pound for Pound = YM2151 40h · 41h · 명령 42h. 모두 받게 함
  - 인터럽트: RST_NEG 버퍼 (YM2151 = RST 28h, 명령 = RST 18h, 둘 다 RST 08h). 드라이버가 IM 1이면 모두 0038h, IM 2면 벡터 표. 타이머 A = 명령 처리 빈도, 타이머 B = 음악 틱 (Air Duel FF57h · FF58h)
  - 아이렘 드라이버는 부팅 뒤 메인 CPU가 보내는 명령 00h를 받기 전 명령을 버린다 (Air Duel FF56h) → 부팅 1초 뒤 00h. 이것으로 12 → 21, 포트 40h~43h로 23
  - transfer 1: rom code offset 0 · 1 = V30 짝수 · 홀수 바이트, z80_offset부터 64KB를 RAM 0000h에 (MAME는 메인 CPU가 옮김)
  - 빠진 것: M72 DAC 샘플 (북 · 목소리, zip에 있는 v0 ROM · 포트 80h~84h · 주기 NMI), System 1의 다른 사운드 보드 변형
- **8-8d. 데이터 이스트 HuC6280 보드 — ✅ v2.1.9 (41 / 43, `Audio/HootDeco.cs`)**
  - 형식 thndzone · thunderzone · supbtime · deco32 · wolffang · generic이 모두 같은 사운드 보드 (MAME cninja · darkseal · supbtime · deco32 sound_map). nslasher (Z80 판)는 뺌
  - HuC6280: 리셋 때 MPR7 = 0 → FFFEh = ROM 1FFEh. 인터럽트 우선 = 내부 타이머 (FFFAh) · IRQ1 명령 (FFF8h, 140000h를 읽으면 내림) · IRQ2 YM2151 (FFF6h), 1FF402h 막기 · 1FF403h 대기/타이머 확인. 내부 타이머 = (값 + 1) × 1024 사이클
  - 클럭 (32.22MHz 결정): CPU ÷ 8, YM2151 ÷ 9, YM2203 ÷ 8, OKI #1 ÷ 32 · #2 ÷ 16 (핀7 높음). YM2151 레지스터 1Bh의 CT 출력 = OKI 뱅크 (deco32: bit0 → #1 · bit1 → #2, 그 밖: bit0 → #2)
  - VGM 듀얼 OKIM6295를 더함 (VgmFormat Okim6295Clock2 · VgmPlayer 둘째 칩 · 레지스터 bit7 · ROM 블록 크기 bit31). 메모리 속 곡은 둘째 칩 클럭 2MHz (파일로 내보내면 같은 클럭)
  - 처음부터 핸드셰이크 없이 소리 남 — 데이터 이스트 드라이버는 명령 IRQ1만으로 곡을 받는다
- 다음 (8-8e): 같은 YM2151 + OKIM6295 보드 더 (dataeast supbtime · tecmo16 · toaplan · raizing · banpresto …, `HootOpmBoard.Layout` 추가), atari system1 (6502 + YM2151), 그 뒤 새 칩 (QSound · ES5505 · C140 · uPD7759 · K053260 …)
  - 남은 것 (8-8a): "소리 남" 기준이 처음 6초에 키온 하나라 몇 항목은 ADPCM 하나뿐 (driftout · liquidk · megablst 등 첫 곡이 짧은 효과음일 수 있음) — 귀로 확인, bshark (code0 · code1 둘), taito z 계열 다른 보드

### 남은 문제 (단계와 따로)
- PC-98 FM 무음 (300 zip 표본): vector-not-hooked 25 (98stdium · g3: 0으로 찬 메모리로 뛰어감), writes-no-keyon 24 (FUGA fgplay_h · MAKO · ARTDI_98이 hoot 인터럽트를 끔), unknown-op 9 (386 접두어 66h · 65h), no-chip-writes 8, koroshi 같은 이름 불일치 (xml = file01.MUS, zip = 일본어 원래 이름)
- PC-98 MIDI 무음 (드라이버별): DOFMDX98 50 · FUGA 50 · ARTDI 32 · FMX 16 · MINT/NLP 16 · MFD 12 · valky 11, RS-MIDI (내장 8251) 없음
- PC/AT: 무음 6 (rakuen · kurodan · eshoutei · darksun · globaleffect · nestory2), SB16 PCM (DSP DMA) 없음, Game Blaster 8 · Tandy 4 (칩은 있음: SAA1099 · SN76489), CPU 기준 클럭 (1.193MHz × clockmul로 추정) 확인
- 공통: 이동(seek) = 기계를 처음부터 그만큼 돌림 (오디오 스레드에서 기다림) → 상태 저장 · 되돌리기, 곡 길이 3분 고정 → 끝 · 되풀이 찾기, 86 PCM · OPNA ADPCM, fm_mix · ssg_mix 옵션, 트리 장비 이름 정리 (PC-98 / PC-9801 …), zip은 있지만 .kss가 없는 KSS 71항목을 트리에서 미리 빨강으로
