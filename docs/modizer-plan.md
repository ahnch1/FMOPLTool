# modizer 형식 대조 · 재생 추가 계획 (2026-11-22, v2.3.39 기준)

- 참고 소스: `_incoming/modizer-master` (iOS 재생기, C / C++ 라이브러리 약 40개를 묶음). 확장자 표 = `src/ModizerConstants.h`의 `SUPPORTED_FILETYPE_*`.
- 우리 원칙: **외부 패키지 · 네이티브 라이브러리 없이 C#으로 옮긴다** (README). 그래서 modizer 라이브러리는 "그대로 붙이기"가 아니라 **옮겨 쓸 참고 코드**. 품을 가르는 것은 줄 수보다 "필요한 CPU · 칩이 이미 있느냐"다.
- 우리에게 이미 있는 것: CPU = Z80 · 6502 · 6801 · 6809 · LR35902 · M68000 · SPC700 · x86(386 리얼 모드). 칩 = OPL2/3(Nuked) · OPN/OPNA/OPNB/OPN2 · OPM · OPLL · PSG(AY) · SCC · SN76489 · SAA1099 · POKEY · NES APU + 확장 · GB APU · HuC6280 · S-DSP · OKI 6295/6258 · uPD7759 · QSound(HLE) · SegaPCM · RF5C68 · K007232 · K053260 · K054539 · C140 · CUS30/15XX · ES5505 · Y8950 ADPCM.

---

## 1. modizer가 재생하는 형식 (재생 엔진별)

| 묶음 | 엔진 (libs/) | 기종 · 칩 | 확장자 | 소스 크기 |
|---|---|---|---|---:|
| 일본 FM 드라이버 | libpmdmini (pmdwin) | PC-98 YM2608 + PPZ8 · P86 | M · M2 · MZ (+ PZI · P86 · PPC · PPS) | pmdwincore 9.1k |
| | libfmpmini (98fmplayer) | PC-98 FMP · PMD (C 재구현) | OVI · OPI · OZI (+ PPZ · PVI) | fmdriver_pmd 6.1k · _fmp 3.6k |
| | mdxplay | X68000 YM2151 + MSM6258 | MDX (+ PDX) | 12.6k |
| | libeupmini | FM Towns YM2612 계열 + RF5C68 | EUP (+ FMB · PMB) | 코어 2.5k |
| | libkss | MSX (Z80 · PSG · SCC · OPLL · Y8950) | KSS, **MGS · BGM · OPX · MPK · MBM** (드라이버 바이너리 동봉: `modules/drivers/mgsdrv.h` 등) | 12k |
| 칩 기록 | libvgm | VGM 칩 전부 | VGM · VGZ · S98 · GYM · DRO(DR0) | 104k |
| 콘솔 · 컴퓨터 (CPU 흉내) | libGME | NES · GB · PCE · SMS · SNES | NSF · NSFE · GBS · HES · KSS · SAP · SPC · SFM · GYM · VGM · AY | 39k |
| | libnsfplay | NES + 확장 음원 | NSF · NSFE | 34k |
| | gbsplay | 게임보이 | GBS · GBR | 11k |
| | libnez (webnez) | HES · SGC(SMS · GG · Coleco) · ZXAY · NSD | HES · SGC | 52k |
| | libwonderswan | 원더스완 (V30 + WS 음원) | WSR | 13k |
| | asap | 아타리 8비트 (6502 + POKEY) | SAP · CMC · CM3 · CMR · CMS · DMC · DLT · MPT · MPD · RMT · TMC · TM8 · TM2 | **asap.c 하나 8.2k** (재생기 내장) |
| | libsidplayfp · websid | C64 (6510 + SID 6581/8580) | SID · PSID · RSID · MUS · PRG · P00 · C64 | 155k · 29k |
| | libatariaudio · sc68 | 아타리 ST (68000 + YM2149 + MFP · STE DMA) · 아미가 | SNDH · SC68 | 59k · 74k |
| | STSound | 아타리 ST · CPC AY 레지스터 기록 (LHA 압축) | YM | 6.6k |
| | libpt3 · libzxtune | ZX 스펙트럼 AY 트래커 · 기록 · 디스크 | PT3, AY · ASC · STC · STP · PT1 · PT2 · PSG · PSM · SQT · VTX · TRD · SCL … (60여 가지) | 1.9k · 167k |
| PSF 계열 (xSF) | highlyexperimental | PS1 · PS2 (MIPS + SPU) | PSF · MINIPSF · PSF2 | 57k |
| | highlytheoritical | 새턴 (68000 + **SCSP**) · 드림캐스트 (ARM7 + AICA) | SSF · DSF (+ MINI) | 55k |
| | HighlyQuixotic | 캡콤 QSound (**Z80 + QSound**) | QSF · MINIQSF | **5k** |
| | libLazyusf | N64 (MIPS + RSP) | USF · MINIUSF | 74k |
| | libgsf | GBA (ARM7) | GSF · MINIGSF | 27k |
| | vio2sf · libxsf | DS (ARM7/9) | 2SF · NCSF (+ MINI) | 53k · 122k |
| | snsf · libxsf_snsf | 슈퍼 패미컴 전체 (65816 + SPC700) | SNSF · MINISNSF | 29k |
| 트래커 · 모듈 | libopenmpt · libxmp | PC · 아미가 모듈 | MOD · S3M · XM · IT · MPTM · 669 · AMF · AMS · DBM · DMF · DSM · FAR · GDM · IMF · MDL · MED · MMD0~4 · MT2 · MTM · OKT · PSM · PTM · STM · STX · ULT · UMX · MO3 · RTM · LIQ · FNK … (60여 가지) | 1.1M · 100k |
| | AHX (hvl_replay) | 아미가 신스 트래커 | AHX · HVL | **2.3k** |
| | uade 2.13 | 아미가 (68000 + Paula, 이글플레이어 68k 바이너리 158개) | MDAT · TFMX · FC · SID(아미가) · DW · JAM … (300여 가지) | 129k |
| | libfurnace | Furnace · DefleMask · FamiTracker | FUR · DMF · FTM · 0CC | 3.1M |
| | v2mplayer | Farbrausch V2 신스 | V2M · V2MZ | 6.3k |
| | libpixel | Organya · PxTone | ORG · PTCOP · PTTUNE | 73k (ORG 부분은 작음) |
| AdLib | Adplug | OPL2/3 각종 PC 게임 · 트래커 | A2M · A2T · ADL · AGD · AMD · BAM · BMF · CFF · CMF · D00 · DFM · DMO · DRO · DTM · GOT · HA2 · HSC · HSP · HSQ · IMF · IMS · JBM · KSM · LAA · LDS · M · MAD · MDI · MDY · MID · MKJ · MSC · MTK · MTR · MUS · PIS · PLX · RAC · RAD · RAW · RIX · ROL · S3M(AdLib) · SA2 · SAT · SCI · SDB · SNG · SOP · SQX · WLF · XAD · XMS · XSM | 48k |
| MIDI | libtim (Timidity++) | GM + SF2 | MID · MIDI | 195k |
| 녹음 오디오 | vgmstream · ffmpeg · monkeyaudio | 게임 스트림 · 일반 오디오 | ADX · BRSTM · HCA · … 400여 가지, MP3 · OGG · FLAC · APE | 273k + |

---

## 2. 우리가 재생하는 것 (Jukebox · `ChipSongs` · `SongFolder.SongExtensions`)

| 묶음 | 확장자 | 방식 (클래스) |
|---|---|---|
| AdLib 곡 | ROL · IMS · MUS · SOP | `RolFormat` · `ImsFormat` · `SopPlayer` (OPL2 / OPL3) |
| AdLib 스트림 | IMF · WLF · HSC · CMF · DRO · RAW (+ AD · OPL 악기) | `AdlibStreamFormats` |
| 트래커 | MOD · NST · S3M · STM (편집도) · XM · IT (재생만) | `TrackerModule` · `ExtModule` |
| MIDI | MID · MIDI · WRK · XMI · MDI | `MidiPlayer` (OPL · GM 출력 · MT-32) |
| 칩 기록 | VGM · VGZ · S98 | `VgmPlayer` (칩 30여 가지, 듀얼 칩 일부) |
| CPU 흉내 | KSS · NSF · HES · GBS · GBR · SPC · MDX(+PDX) | `Kss` · `Nsf` · `Hes` · `Gbs` · `Spc` · `Mdx` |
| hoot | .hoot (zip 안 항목) | 기계 흉내 7,434 항목 — `docs/todo.md` |
| 녹음 오디오 | WAV · MP3 · WMA · M4A · AAC · FLAC | `AudioFileReader` (Windows Media Foundation) |
| 그 밖 | ZIP 폴더, D88 · FDI · HDM 디스크 이미지 꺼내기 | `ZipFolder` · `DiskImage` |

---

## 3. 비교 — modizer에 있고 우리에게 없는 것

> "재사용" = 우리에게 이미 있는 CPU · 칩. 품: S(하루 안) · M(며칠) · L(한 주 넘게) · XL(새 CPU + 새 칩).

| 형식 | 필요한 것 | 재사용 | 새로 | 참고 소스 | 품 | 덤 (hoot 등) |
|---|---|---|---|---|---|---|
| **GYM** | YM2612 + SN76489 기록 → VgmSong | 둘 다 | 읽기만 | libvgm · GME `Gym_Emu` | S | — |
| **NSFE** · NSF/GBS용 M3U | 청크 읽기 | `Nsf` · `NezM3u` | 청크 파서 | GME `Nsfe_Emu` | S | — |
| **SAP** (type B · C · D) | 6502 + POKEY | 둘 다 | SAP 헤더 · 실행 틀 | asap.c | S | — |
| **CMC · RMT · TMC · MPT · DLT …** | 6502 + POKEY + ASAP 내장 재생기 | 둘 다 | 재생기 바이너리 · 실행 틀 | asap.c | M | — |
| **QSF · MINIQSF** | Z80 + QSound + PSF 컨테이너 (zlib, _lib 묶기) | Z80 · QSound HLE · Kabuki 풀기 | PSF 컨테이너 (다른 xSF 공통 기반) | HighlyQuixotic | S~M | xSF 공통 틀 |
| **MGS · BGM · OPX · MPK · MBM** | 파일 + MSX 드라이버 → KSS 이미지 | `Kss` 전체 | KSS 감싸기 (드라이버 바이너리 libkss에 동봉) | libkss `src/kss/*2kss.c` | S~M | todo 6장 보류 "MGS" 풀림 |
| **SGC** | Z80 + SN76489 (+ FM, GG 스테레오) | 둘 다 · OPLL | SGC 헤더 · 매퍼 | libnez `m_sgc.c` | S~M | hoot 세가 SG-1000 7항목 |
| **YM** (YM2 ~ YM6) | AY 레지스터 기록 + **LHA -lh5- 풀기** + 디지드럼 · SID 효과 | `PsgEmulator` | LHA 풀기 (todo 5장 "LHA / LZH"와 같음) | STSound | S~M | LZH 압축 폴더 |
| **VTX · PSG** (ZX) | AY 기록 (VTX는 LHA) | `PsgEmulator` | 읽기 | zxtune | S | — |
| **AY** (ZXAYEMUL) | Z80 + AY (+ 스펙트럼 · CPC 포트) | 둘 다 | AY 헤더 · 기계 틀 | libnez `m_zxay.c` · GME `Ay_Emu` | M | — |
| **PT3 · PT2 · STC · ASC · SQT …** | ZX 트래커 재생 (네이티브 재생기) | `PsgEmulator` | 형식마다 재생기 | libpt3 (1.9k) · zxtune | M (PT3 먼저) | — |
| **AHX · HVL** | 소프트웨어 신스 재생 | — (순수 계산) | 재생기 2.3k 옮기기 | AHX `hvl_replay.cpp` | M | — |
| **AdPlug 형식** (RAD · D00 · A2M · HSP · SA2 · AMD · DTM · LDS · KSM · BAM · RIX · ADL · XAD · CFF · DMO · MTK · JBM · GOT · SNG · …) | OPL 이벤트 재생기 (형식마다 수백~2천 줄) | Nuked OPL2/3 · `AdlibStreamFormats` 틀 | 형식마다 플레이어 | Adplug `src/*.cpp` | 형식마다 S~M | 프로젝트 본업 (AdLib), todo 5장 "D00 · RAD · A2M" |
| **PMD 단독 파일** (M · M2 · MZ + P86 · PPZ) | PMD 드라이버 재구현 | `OpnChip` (YM2608 · ADPCM) | 드라이버 6k 옮기기 + PPZ8 · 86 PCM | libfmpmini `fmdriver_pmd.c` (pmdwin보다 짧고 C) | L | todo 6장 보류 "PMD · FMP 이식" — hoot 밖 단독 파일. 86 PCM 무음(todo 4장)에도 도움 |
| **FMP** (OPI · OVI · OZI + PVI · PPZ) | FMP 드라이버 재구현 | `OpnChip` | 3.6k + PPZ8 공용 | libfmpmini `fmdriver_fmp.c` | M | PMD와 PPZ8 · ADPCM 공유 |
| **EUP** (FM Towns) | 이벤트 재생 + YM2612 × 2 + RF5C68 | OPN2 · `Rf5c68` | 플레이어 2.5k | libeupmini `eupplayer*.cpp` | M | — |
| **모듈 더** (MTM · 669 · ULT · FAR · PTM · OKT · MED/MMD · AMF · DBM · GDM · DSM · PSM · UMX …) | 로더 → 우리 모듈 구조 | `TrackerModule` / `ExtModule` 재생 | 형식마다 로더 | libxmp `src/loaders/*.c` (openmpt보다 작음) | 형식마다 S~M | — |
| **ORG** (Organya) | 단순 파형 신스 | — | 재생기 + 파형 표 | libpixel (org 부분) | S~M | — |
| **V2M** | V2 신스 | — | 6.3k | v2mplayer | M~L | — |
| **WSR** (원더스완) | V30 + WS 음원 | x86 (V30 ≈ 8086 + 몇 명령) | WS 음원 칩 (작음) | libwonderswan | M | hoot 아이렘 M92 (V35) 16항목 쪽 V30 계열 |
| **SNDH · SC68** | 68000 + YM2149 + MFP 68901 타이머 (+ STE DMA) | M68000 · AY | MFP · 아타리 ST 틀 · ICE 풀기 | libatariaudio · sc68 | M~L | — |
| **SID** (PSID · RSID) | 6510 + SID 칩 | 6502 | SID 칩 (6581 · 8580 필터) + CIA · VIC 타이밍 | websid (짧음) · sidplayfp | L | — |
| **SSF** (새턴) | 68000 + **SCSP** | M68000 · PSF 틀 | SCSP (DSP 포함) | highlytheoritical | L | hoot 세가 Model 2A 24 · Model 3 9 (todo 3.3 "SCSP 큼") |
| **SNSF** | 65816 + SPC700 | SPC700 · S-DSP | 65816 + SNES 메모리 | snsf | L | — |
| **GSF** | ARM7 + GBA 음원 | GB APU 일부 | ARM7 · GBA DMA 음원 | libgsf | XL | — |
| **PSF · PSF2** | MIPS R3000 + SPU | — | 둘 다 | highlyexperimental | XL | — |
| **USF · 2SF · NCSF · DSF** | MIPS + RSP · ARM9/7 · AICA | — | 전부 | lazyusf · vio2sf · libxsf | XL | — |
| UADE 아미가 형식 300여 | 68000 + Paula + 아미가 OS 틀 + 이글플레이어 바이너리 | M68000 | Paula · 아미가 틀 | uade (GPL) | XL | — |
| Furnace · FTM · DMF | 거대 트래커 | — | — | libfurnace 3.1M | 하지 않음 | — |
| vgmstream · APE · OGG 등 | 녹음 오디오 디코더 | — | — | — | 하지 않음 (칩 음악 아님) | — |
| VGM 칩 더 (libvgm) | YMF278B · YMF271 · YMZ280B · RF5C164 · PWM · MultiPCM · C352 · GA20 · ES5503 · ES5506 · X1-010 · SCSP · 원더스완 · VSU · YM2151 × 2 | 기존 VGM 틀 | 칩마다 | libvgm `emu/cores` | 칩마다 S~L | hoot: GA20 (M92) · MultiPCM (model2 · system_multi) · C352 (남코 11 · 12) · YM2151 × 2 |

우리에게만 있는 것 (modizer에 없음): hoot 기계 흉내 전체, ROL · IMS · SOP 편집, WRK · MDI, MT-32 흉내, PC-98 디스크 이미지 꺼내기.

---

## 4. 계획 (단계 = 커밋 하나 · 버전 하나 · SelfTest 통과, `step-commit` 규칙)

### M1 — 있는 CPU · 칩만으로 (S 위주, 먼저)
- [x] M1-1 GYM → VgmSong (v2.4.0, `GymFormat`)
- [x] M1-2 NSFE 청크 · NSF/GBS 곁 M3U 곡 이름 · 길이 (v2.4.1, `Nsfe` — m3u는 NSF · GBS 모두 이미 `NezM3u`)
- [x] M1-3 SAP (type B · C · D) — 6502 + POKEY, 곡 여럿 (`ChipSongs.multi`) (v2.4.2, `Sap` + TYPE S, STEREO = VGM 둘째 POKEY)
- [x] M1-4 MGS · BGM · OPX · MPK · MBM → KSS 이미지 (libkss 드라이버 바이너리를 리소스로, 라이선스 확인) (v2.4.3, `KssDrivers` — 드라이버는 저작물이라 사용자가 resource\kssdrv에 둠 (.bin 또는 libkss .h), 확장 I/O 끝 · 되풀이 감지. MBM은 남김, 실제 곡 파일로 귀 확인 아직)
- [x] M1-5 SGC (SMS · GG · Coleco) — 다음에 hoot SG-1000 7항목에 같은 코드 (v2.4.4, `Sgc` — hoot SG-1000은 아직)
- [x] M1-6 LHA -lh5- 풀기 (Formats) → YM · VTX 재생 + LZH 압축 폴더 (todo 5장 항목 같이 닫음) (v2.4.5, `Lha` · `AyDumpFormats` + PSG, YM5/6 특수 효과 · VTX 스테레오는 남김)
- [x] M1-7 PSF 컨테이너 공통 (zlib · `_lib` · 태그 · 길이) + QSF (Z80 + QSound, CPS-2 코드 재사용) (v2.4.6, `PsfFile` · `Qsf`, 실제 QSF 표본으로는 아직 확인 못 함)

### M2 — AdLib 형식 (프로젝트 본업, AdPlug 참고)
- [ ] M2-1 RAD (v1 · v2)
- [ ] M2-2 D00 (EdLib)
- [ ] M2-3 A2M · A2T (AdLib Tracker 2, 큰 편 — OPL3 · 4op)
- [ ] M2-4 나머지 묶음: HSP · SA2 · AMD · DTM · LDS · KSM · BAM · RIX · ADL · XAD · CFF · DMO · MTK · JBM · GOT · SNG · MAD · MKJ — 실제 파일 많은 순으로 몇 개씩
- 실제 파일 확인: Modland AdLib 폴더 등으로 표본 모으기, AdPlug 결과와 귀로 비교

### M3 — 일본 FM 드라이버 단독 파일
- [ ] M3-1 PPZ8 · OPNA ADPCM 공용 (PMD · FMP 함께 씀)
- [ ] M3-2 FMP (OPI · OVI · OZI) — 짧은 쪽부터
- [ ] M3-3 PMD (M · M2 · MZ + P86 · PPZ) — `fmdriver_pmd.c`, 86 PCM은 hoot 86 PCM 무음 (todo 4장)과 같이
- [ ] M3-4 EUP (FM Towns)
- [ ] M3-5 MDX를 mdxplay와 대조 (PCM8 · 템포 차이 확인만)

### M4 — 소프트 신스 · 트래커 더
- [ ] M4-1 AHX · HVL
- [ ] M4-2 PT3 → PT2 · STC · ASC · SQT (ZX AY 트래커)
- [ ] M4-3 AY (ZXAYEMUL, Z80 + AY)
- [ ] M4-4 모듈 로더: MTM · 669 · ULT · FAR · PTM · OKT · MED · AMF · DBM · GDM (libxmp 로더 → `ExtModule`)
- [ ] M4-5 ORG (Organya) · V2M

### M5 — 새 칩 · 작은 새 기계 (hoot와 겹치는 것 먼저)
- [ ] M5-1 VGM 칩: YM2151 × 2 · GA20 · MultiPCM · YMZ280B · C352 · YMF278B(ROM 필요) — 각각 hoot 항목도 같이 엶
- [ ] M5-2 WSR (V30 + WS 음원) — x86 확장, M92 V35 준비
- [ ] M5-3 CMC · RMT · TMC 등 ASAP 네이티브 형식
- [ ] M5-4 SNDH · SC68 (MFP 68901)
- [ ] M5-5 SID (websid 기준으로 6581 · 8580)

### M6 — 큰 것 (나중에, 하나씩 따로 정함)
- [ ] SSF (SCSP — hoot Model 2A · 3과 함께) · SNSF (65816) · GSF (ARM7) · PSF/PSF2 · USF · 2SF/NCSF · DSF · UADE
- 하지 않음: Furnace · vgmstream · APE (칩 음악 재현이 목적이 아님)

---

## 4.1 진행 기록 · 이어 하기 (2026-11-23, v2.4.6 — M1 끝)

- 끝난 단계 (커밋 하나씩, SelfTest 1676 통과): v2.4.0 GYM · v2.4.1 NSFE · v2.4.2 SAP (+ VGM 둘째 POKEY · 스테레오) · v2.4.3 MGS · BGM · MPK · OPX (`KssDrivers`, + `Kss.Render` 확장 I/O 끝 · 되풀이 감지) · v2.4.4 SGC · v2.4.5 LHA 풀기 · YM · VTX · PSG · LZH 압축 폴더 · v2.4.6 PSF 컨테이너 (`PsfFile`) + QSF · MINIQSF (`Qsf`, `HootCps2.Machine` 재사용 + `PlainShared`).
- 시험: `SelfTest --modizer` = 이 계획의 시험만 (`tests/AdlibComposer.SelfTest/ModizerFormatTests.cs`). 실제 표본은 `D:\Retro\fmopldata\Modizer` (환경 변수 `FMOPL_MODIZER_SAMPLES`), 지금 있는 것은 GYM · NSF · GBS · KSS · GSF · PSF2 · SPC. SAP · SGC · MGS · YM · VTX 실제 파일은 없음 → 만든 파일로만 시험함. 표본을 구하면 귀로 확인할 것.
- M1 (있는 CPU · 칩만으로) 전부 끝. 다음 할 일: **M2-1** RAD (AdLib, AdPlug `rad2.cpp`) → M2-2 D00 → M2-3 A2M. PSF 컨테이너가 생겼으므로 M6의 xSF (GSF · PSF2 …)는 CPU만 더하면 됨 — 사용자 표본에 GSF 155개 · PSF2가 있다.
- QSF는 만든 파일로만 확인 (라이브러리 + 미니, 태그, 실제 wof Kabuki 열쇠로 암호화한 작은 롬). 실제 QSF 표본으로 귀 확인 아직.
- 남긴 것: MBM (MoonBlaster — MSX-AUDIO ADPCM + MBK, `mbm2kss.c`), YM5/6 특수 효과 (SID 음 · 디지드럼 · Sinus-SID · Sync-Buzzer), VTX 스테레오 방식 (ABC · ACB …), SAP TYPE R · 코복스, SGC를 hoot SG-1000 7항목에, LZH -lh1-.
- 사용자 표본 폴더에 GSF 155 · PSF2가 많다 (M6 "큰 것"): ARM7 (GBA) · MIPS + SPU2는 새 CPU라 따로 정할 일.
- 작업 요령: bash heredoc 안 파이썬은 `\0` · `\t` · `\x..`를 진짜 바이트로 바꿔 버린다 → C# 글에 역슬래시가 들어가면 파일로 쓴 스크립트 (JSON 바꾸기 목록)를 쓸 것. CRLF 파일 주의.
- 드라이버 바이너리 (MGSDRV 등)는 저작물이라 저장소에 넣지 않음: `resource\kssdrv`에 원래 파일 또는 libkss `.h`를 사용자가 둔다 (시험은 `_incoming/modizer-master`의 `.h`를 읽음).

---

## 5. 주의

- **라이선스**: Adplug · libxmp = LGPL, sidplayfp · uade · zxtune · Timidity = GPL, asap = GPL, 98fmplayer · libkss · webnez · eupmini 등은 단계마다 각 폴더의 LICENSE를 확인 (이 표는 기억 기준, 아직 대조 안 함). 코드를 옮길 때 줄 단위 번역은 원 라이선스를 따른다 → 옮긴 파일 머리에 출처 · 라이선스 적기, GPL 소스는 동작만 참고하고 다시 쓰는 쪽을 기본으로.
- **드라이버 바이너리** (MGSDRV · MPK · OPX4KSS · MBR, ASAP 내장 재생기, UADE 이글플레이어): 저작권이 원작자에 있음 → 앱에 넣을지 · 사용자가 넣게 할지 (`Defaults` 폴더처럼) 단계마다 정함.
- 안드로이드도 같은 `ChipSongs`를 쓰므로 Formats · Audio에만 넣으면 두 쪽에 다 들어간다. 무거운 흉내(xSF 등)는 안드로이드 속도 따로 확인.
- 표본 파일: Modland · HVSC · ASMA · SNDH · zxart (modizer 온라인 DB 목록 = `src/ModizerConstants.h` 앞부분) — 단계마다 표본 몇 개를 `D:\Retro` 쪽에 두고 SelfTest에 경로 옵션으로.
