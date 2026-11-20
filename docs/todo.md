# FMOPLTool 할 일 (2026-11-20, v2.1.28 기준)

- 이 파일 = **해야 할 일**과 **남겨 둔 일**(보류)을 체크박스로. 끝난 일의 자세한 내용 · 진단 명령 · 원인 분석은 [`hoot-status.md`](hoot-status.md), 사용법 · 구조는 [`../README.md`](../README.md).
- 옛 할 일 기록(v1.x ~ v2.0 진행 일지)은 `_incoming/Todo.md`(git에 안 들어감)에 그대로 둔다.
- 두 PC에서 푸시한다 → 작업 시작 전에 `git fetch` 후 `main..origin/main` 확인.

---

## 1. hoot 목록 기준 현황 (`_incoming/Hootarchive/xml_new`, `HootArchive.Playable` 규칙으로 집계)

| | 항목 | 곡 |
|---|---:|---:|
| 목록 전체 | 9,489 | 221,712 |
| 아카이브 zip이 있는 항목 | 8,432 | — |
| **재생 규칙에 든 항목 (zip 있음)** | **7,434 (88.2%)** | **196,656** (그 형식 전체) |
| 아직 재생 못 하는 항목 (zip 있음) | 998 | 약 25,060 (그 형식 전체) |
| zip이 없는 항목 | 1,057 | — |

곡 수는 zip 유무와 상관없이 그 형식의 titlelist 전체. "재생 규칙에 듦" = 트리에서 두 번 클릭하면 기계 흉내로 곡을 만든다는 뜻. 그 안에서도 무음인 항목이 있다 (아래 2장 "소리 남" · 4장).
집계 다시 하기: 스크래치 스크립트 `hoot_census.py` (xml_new의 `<driver type>` · `archive` · `midiout` · `titlelist`를 세어 이 규칙표와 맞춤) — 필요하면 `SelfTest --hootcensus`로 옮길 것 (5장).

---

## 2. 구현된 것 (zip 있는 항목 · 곡, 조사 결과)

| 기종 | 형식 (zip 있는 항목) | 곡 | 방식 · 클래스 | 소리 남 (조사) |
|---|---|---:|---|---|
| PC-98 DOS | opn 1416 · opna 230 · 86 92 · beep 80 | 65,005 | x86 + MS-DOS + PC-98 기계 (`HootPc98`) | 300 zip 표본 226 / 301 |
| PC-98 DOS MIDI 판 | beep 444 · opn 237 · 86 5 | 17,778 | MPU-PC98 → 살아 있는 MIDI | 360 / 641 |
| PC-98 (DOS 없음) | pc98vx opn 198 · beep 40 · opna 25 · 86 9, pc98 opn 81 · beep 4 · opna 2 | 12,054 | `BootGeneric` | 314 / 354 |
| pc98vx MIDI 판 | opn 93 · beep 12 | 3,143 | MPU-PC98 | 97 / 109 |
| PC/AT DOS | adlib 412 · sb16 71 · beep 5 | 6,453 | `PcAtMachine` | 482 / 488 |
| PC/AT MIDI 판 | beep 579 · adlib 7 | 8,180 | MPU-401 | 571 / 586 |
| MSX | generic 498 · kss 230 | 13,334 | MSX 기계 (`HootMsx`) · KSS | 481 / 498 (KSS 71항목은 zip에 .kss 없음) |
| PC-88 | opn 389 · opna 142 | 13,623 | `HootPc88` | 487 / 527 |
| X1 | psg 83 · opm 71 | 3,430 | `HootX1` | 143 / 152 |
| X68000 | generic 553 | 12,814 | MC68000 + X68000 (`HootX68k`) | 499 / 553 |
| X68000 MIDI 판 | generic 166 | 4,098 | YM3802 | 141 / 166 |
| 슈퍼 패미컴 | spc 531 | 15,139 | SPC700 + S-DSP | 파일 있음 |
| NES · 게임보이 | nsf 14 · gbs 13 | 633 | 6502 · LR35902 | 파일 있음 |
| 네오지오 | generic 146 | 4,070 | `HootNeoGeo` | 137 / 146 |
| 메가드라이브 | generic_z80 108 · iyinc 23 · koei 14 · 1c00 5 | 2,551 | `HootMegaDrive` (koei · 1c00 v2.1.16) | 112 / 150 |
| 타이토 YM2610 | f2system 40 · bsystem 11 · dual68 10 · fx1a 3 · sonicb 2 · xsystem 1 | 2,491 | `HootTaito` (v2.1.6) | 67 / 67 |
| 타이토 YM2151 | fullt 9 | 147 | `HootTaitoOpm` + MSM5205 → OKIM6258 (v2.1.17) | 9 / 9 (147곡 모두) |
| 토아플랜 1 · Twin Cobra | generic 9 | 125 | `HootToaplan` (Z80 + YM3812, v2.1.18) | 9 / 9 (125곡 모두) |
| 데이터 이스트 6502 | cobracom 8 · drgninja 6 · karnov 3 · triothep 2 · lastmisn 2 · makyosen 1 | 287 | `HootDeco6502` (6502 + YM2203 + YM3812/YM3526 + OKI, v2.1.19) | 20 / 20 (곡 283 / 285, 무음 2 = 효과음) |
| 캡콤 CPS-1 · 테크노스 | cps1 29 · ddragon2 7 · ddragon3 2 · ctribe 1 | 약 2,600 | `HootOpmBoard` (v2.1.7) | 39 / 39 |
| 세가 System 1 · 아이렘 M72 | system1 29 · m72 23 | 약 1,200 | `HootSegaSystem1` · `HootM72` (v2.1.8) | 52 / 52 |
| 데이터 이스트 HuC6280 | deco32 14 · wolffang 14 · thndzone 7 · supbtime 3 · generic 3 · thunderzone 2 | 910 | `HootDeco` (v2.1.9) | 41 / 43 |
| 잘레코 Mega System 1 | megasys1 16 | 391 | `HootMegaSys1` (v2.1.10) | 16 / 16 |
| 세가 System 16 | system16b 28 · system16a 10 | 약 1,000 | `HootSegaSystem16` + uPD7759 (v2.1.11) | 37 / 38 |
| 캡콤 CPS-2 | cps2 38 | 약 2,700 | `HootCps2` + QSound HLE (v2.1.12) | 38 / 38 |
| 캡콤 CPS-1 QSound · ZN | zn 10 · cps1qs 4 | 1,038 | `HootCps2` + Kabuki 풀기 · 래치 NMI (v2.1.15) | 14 / 14 |
| 세가 PCM 보드 | toutrun 7 · gforce 4 · aburner 3 · shangon 2 · eropnx2 2 · pdrift · outrun · hangon · sharrier · spmonaco · bootleg outrun | 약 600 | `HootSegaPcmBoard` + SegaPCM (v2.1.13) | 20 / 24 |

---

## 3. 해야 할 일 — hoot 목록에서 아직 재생 못 하는 것 (zip 있는 항목 수 순)

> 칸: 항목(zip 있음) · 곡 · 필요한 것. "있음" = CPU · 칩이 이미 있어 보드 배치만 맞추면 됨. 순서 = 들이는 품 대비 곡 수.

### 3.1 CPU · 칩이 다 있음 — 보드 배치만 (먼저)
- [x] 캡콤 ZN (capcom zn) 10 · 341곡 — v2.1.15, 래치 포트 00h + NMI, 16비트 코드 위 바이트 먼저 (14 / 14)
- [x] 캡콤 CPS-1 QSound (capcom cps1qs) 4 · 697곡 — v2.1.15, Z80 ROM Kabuki 풀기 (열쇠는 ROM으로 고름)
- [x] 메가드라이브 Koei (megadrive koei) 14 · 330곡, 1c00 5 · 66곡 — v2.1.16: 1c00 = SMPS Z80 큐 1C0Ah, koei = 드라이버 · 우편함 · 곡 표를 카트리지에서 찾음 (19 / 19)
- [x] 타이토 fullt 9 · 147곡 — v2.1.17: Z80 + YM2151 + TC0140SYT, 소리 켬 EFh, MSM5205는 VGM OKIM6258로 (9 / 9, 147곡 모두 소리)
- [x] 토아플랜 generic 9 · 125곡 — v2.1.18: Z80 + YM3812 (포트는 ROM에서 찾음), 68000 맞춤 8001h ← AAh, 명령 8000h (9 / 9, 125곡 모두 소리)
- [x] 데이터 이스트 6502 (cobracom 8 · drgninja 6 · karnov 3 · triothep 2 · lastmisn 2 · makyosen 1) — v2.1.19: 래치 자리로 배치 셋 (dec0 · dec8 · karnov), 6502 10진 모드, decrypt = DECO222 (20 / 20)
- [x] 데이터 이스트 midres 1 · 12곡 — v2.1.21: `HootDeco`에 midres 배치 (YM3812 108000h 타이머 = IRQ2 · YM2203 118000h · OKI 130000h · 명령 138000h), 12곡 모두 소리
- [x] 데이터 이스트 brkthru 2 (Break Thru · Darwin 4078) · exprraid 1 · 19곡 — v2.1.32: 새 CPU 6809 (`Cpu6809`) + `HootDeco6809` (6809 + YM2203 + YM3526), 19곡 모두
- [x] SNK68 (snk 3812) 4 · 146곡 — v2.1.22: Z80 + YM3812 + uPD7759 (마스터 모드 → 보드가 ROM 바이트를 넣는 슬레이브 스트림), 부팅 뒤 명령 0Dh 두 번 = 음악 · 효과음 켬 (146곡 모두 소리, uPD7759 59곡)
- [ ] SNK 3526_8950 6 (+ 3526x2 2 · chopper (Y8950 × 2) · fitegolf 2 · aso) — Z80 + YM3526 + Y8950: **VGM 재생기에 OPL 둘** (지금은 하나만 — 3526 + 8950이면 8950만 남)과 **Y8950 ADPCM** (델타-T, ROM 블록 88h)이 먼저 필요, 명령 = snk.cpp 사운드 상태 레지스터 (F800h, IRQ 원인 비트)
- [x] 니치부츠 terracre 6 · terraf 4 · 164곡 — v2.1.23: Z80 + YM3526/YM3812 + DAC 둘 (VGM YM2612 DAC로), 7812.5Hz 주기 IRQ, 명령 = (코드 << 1) | 1 폴링 (10 / 10, 곡 161 / 161 + 구분 줄 1)
- [x] 알파 68k2 6 · 83곡 — v2.1.24: Z80 + YM2203 + YM2413 + DAC (VGM YM2612 DAC), 7614Hz 주기 NMI, 명령 폴링, 뱅크 C000h (6 / 6, 83곡 모두 소리 — 곡은 YM2413만 씀)
- [x] UPL robokid 5 · 109곡 — v2.1.25: Z80 + YM2203 × 2, 명령 E000h 폴링, YM2203 #1 타이머 IRQ, **VGM 재생기에 듀얼 YM2203** (클럭 bit30 + A5h) (5 / 5, 108곡 + 정지 명령 1)
- [ ] UPL ninjakid2 1 (Ninja-kun: 아수라) — Z80 MC-8123 암호 (rom key) + YM2203 × 2 + PCM
- [x] 테크모 tecmo16 3 · 112곡 — v2.1.26: `HootOpmBoard.Tecmo16` 배치 (FC00h OKI · FC04h YM2151 · FC08h 명령 NMI), 부팅 3초 (3 / 3, 112곡 모두 — 8곡은 OKI 효과음 하나)
- [x] 테크모 gaiden 4 · 300곡 — v2.1.27: `HootGaiden` (Z80 + YM2203 × 2 + OKI, 명령 FC20h NMI, IRQ = YM2203 #1만) — 곡 모두, 무음 69 = Strato Fighter 효과음 · 목소리 65 + 龍剣伝 Round Clear 3 · 4 + 코드 모름 1
- [x] 테크모 gemini 3 · rygar 1 · 122곡 — v2.1.28: `HootTecmoAdpcm` (Z80 + YM3812 + MSM5205 → VGM OKIM6258, 배치 Rygar · Silk Worm · Back Fire) (4 / 4, 122곡 모두, MSM 23곡)
- [x] 캡콤 opn2 8 · rushcrsh 2 · tigerroad 2 · 282곡 — v2.1.29: `HootCapcomOpn` (Z80 + YM2203 × 2, 배치 셋: gng · lwings 222Hz · srumbler / tigerroad, Avengers는 E006h 되읽기) (12 / 12, 281 / 282곡 — 남은 하나 = 정지 명령). todo의 3.3 "6809: 캡콤 opn2"는 잘못 — 사운드 CPU는 Z80
- [ ] 테크모 tbowl (YM3812 × 2 + MSM5205 × 2) · spbactn (YM3812 + OKI) · wc90 (YM2608) · solomon (AY × 3)
- [ ] 세이부 raiden 4 (YM3812 + OKI, Z80 암호화일 수 있음) · raiden2 2
- [ ] 세가 System 24 4 — 68000 + YM2151 (MC68000 있음)
- [ ] 세가 SG-1000 7 — Z80 + SN76489
- [x] 아타리 System 1 4 · 275곡 — v2.1.30: `HootAtariSys1` (6502 + YM2151, 1820h bit7 = 자가 시험 끔), 179 / 275곡 (v2.1.31 POKEY), 무음 96 = TMS5220 음성 (4장), Road Blasters는 곡 목록 없음
- [ ] 남코 wsgz80 4 — Z80 + 남코 WSG (칩 있음)
- [x] 비디오시스템 turbofrc · aerofgt 6 · 사이쿄 gunbird 3 · 169곡 — v2.1.49 `HootYm2610Board`, 모두
- [x] 코나미 pooyan 3 · 44곡 — v2.1.48 (Pooyan 모두, Loco-Motion · Tactician은 파트 2 · 3 무음)
- [x] 코나미 scramble 8 · timeplt 4 · 116곡 — v2.1.39 `HootKonamiAy`, 75곡 (무음 = ch2 · ch3 파트 등)
- [x] 타이토 taitosj 12 · 285곡 — v2.1.40 `HootTaitoSj` (AY × 3 — 셋째는 안 명령 D9h, DAC → YM2612 DAC), 모두
- [ ] YM2151 + OKI 작은 보드: banpresto (dbz 2 · agallet 2 · ultraman · godzilla · sdgndmps · metmqstr) · raizing (mahou 2 · bgaregga · batrider — NMK112 뱅크) · dooyong superx · technosoft hyperduel · atlus wingforc · tatsumi apache3 · toaplan enmadaio · tp 3 — 각 1~3항목, 보드마다 사운드 ROM을 읽어 `HootOpmBoard.Layout`에 줄 더하기
- [ ] PC/AT Game Blaster 8 · Tandy 4 — `PcAtMachine`에 SAA1099 · SN76489 포트 (칩 있음)
- [ ] PC-98 DOS soundblaster16 9 · pc9821dos opn 6 · opna 3 · 86 3 · pc98 rashin 5 · pc98vx amd98 4 (AY × 3) — 기존 PC-98 기계 변형
- [ ] PC-88VA (pc88va opna 12 · opn 12 · pc88vados opn 11 · opna 7) — V30 (x86 있음) + OPN/OPNA, PC-88VA 기계 새로

### 3.2 새 칩이 필요 (CPU는 있음)
- [ ] 타이토 F3 (f3system) 36 · 739곡 — 68000 + **ES5505** (+ ES5510 DSP는 빼고) + MC68681 DUART 타이머
- [x] 코나미 K053260 11 · 616곡 — v2.1.43 `HootKonami53260` + K053260 칩, 606곡
- [x] 코나미 K054539 Z80 판 (054539 4 · 054539x2 6) · 267곡 — v2.1.44 `HootKonami54539` + K054539 칩, 266곡
- [ ] 아이렘 M92 (m92) 16 — V35 (x86 계열) + YM2151 + **GA20**
- [x] 세가 System 18 6 · 251곡 — v2.1.45 `HootSegaSystem18` + RF5C68 칩 + VGM 둘째 YM2612, 246곡
- [x] 세가 System 32 9 · 146곡 — v2.1.46 (`HootSegaSystem18` System 32 배치), 정지 명령 빼고 모두
- [x] 코나미 System GX 17 · 489곡 — v2.1.47 `HootKonamiGx` (68000 + K054539 × 2 at 200000h), 모두
- [x] 코나미 scontra 3 · thundercross 3 · 89곡 — v2.1.50 `HootKonamiK007232` + K007232 칩, 84곡
- [x] 코나미 salamander 3 · hcastle 3 · crimfght 2 · 231곡 — v2.1.51 같은 보드 배치 표, 229곡 (무음 = 정지 항목)
- [x] 코나미 twin16 5 · 82곡 — v2.1.52 uPD7759 마스터 모드 + 같은 보드, 모두
- [x] 코나미 lethal 2 · 51곡 — v2.1.53 `HootKonami54539` lethal 배치, 모두
- [ ] 코나미 gx400 5 (VLM5030 · K005289)
- [ ] 세가 model2 4 · system_multi 4 (315-5560 = MultiPCM)
- [ ] 코나미 hornet 8 · gticlub 4 — RF5C400 (CPU 68000)

### 3.3 새 CPU가 필요
- [x] 남코 System 1 22 · 818곡 — v2.1.33 `HootNamcoSys1` (6809 + YM2151, 요청 = 7100h 묶음 (곡, 명령)), 799 / 818곡 (남은 19 = 정지 명령)
- [x] CUS30 칩 (남코 System 1 · 86의 파형 8목소리) — v2.1.34, VGM 안 명령 D8h
- [x] 남코 wsg6809 11 · 263곡 — v2.1.35 `HootNamcoWsg6809` (6809 + 15XX), 262곡
- [x] 남코 system86 5 · 71곡 — v2.1.36 새 CPU 6801 계열 (`Cpu6801`) + `HootNamcoSys86`, 71곡 모두 (63701X 음성 없음)
- [x] 남코 System 2 26 · 870곡 — v2.1.41~42 `HootNamcoSys2` + C140 칩, 827곡 (C140 = PCM 묶음 7200h), 남은 무음 43 (Four Trax 27)
- [ ] **6809 (CPU는 v2.1.32에 있음)**: 후지쯔 FM77AV 22 · FM-7 7 — 6809 하나로 약 100항목 (+ C140 칩)
- [x] 남코 wsg63701 6 · 157곡 — v2.1.37 (`HootNamcoSys86` 슬롯 요청), 모두
- [x] 아이렘 M62 15 · 145곡 — v2.1.38 `HootIremM62` (M6803 + AY × 2 + MSM5205 → OKIM6258), VGM 듀얼 AY, 137곡
- [ ] 남코 System 11 · 12 · 22 · NA-1 · NB-1 (C352 · M37702 / H8) 약 80항목 — 큼
- [ ] 세가 Model 2A (SCSP + 68000) 24 · Model 3 9 — SCSP 큼

### 3.4 그 밖 (긴 꼬리)
- [ ] 위에 없는 형식 413가지 · 484항목 (대부분 1~3항목) — `hoot_census.py` 결과에서 하나씩

---

## 4. 해야 할 일 — 재생은 되지만 무음 · 품질 문제

- [ ] PC-98 DOS FM 무음 (300 zip 표본 75개): vector-not-hooked 25 (98stdium · g3), writes-no-keyon 24 (FUGA fgplay_h · MAKO · ARTDI_98 hoot 끔), unknown-op 9 (386 접두어 66h · 65h), no-chip-writes 8, 이름 불일치 (koroshi: xml file01.MUS ↔ zip 원래 이름)
- [ ] PC-98 MIDI 무음: DOFMDX98 50 · FUGA 50 · ARTDI 32 · FMX 16 · MINT/NLP 16 · MFD 12 · valky 11, RS-MIDI (내장 8251)
- [ ] pc98vx 무음 40: writes-no-keyon 19 · no-chip-writes 13 · unknown-op 2 · vector 2 · hoot 끔 2
- [ ] PC/AT 무음 6 (rakuen · kurodan · eshoutei · darksun · globaleffect · nestory2), SB16 PCM (DSP DMA)
- [ ] MSX 무음 17 · PC-88 무음 40 · X1 무음 9 · 네오지오 무음 9 (암호화 V ROM · m1_decrypt) · 메가드라이브 무음 38 (generic_z80 요청 주소) · X68000 무음 54 (같이 쓰는 zmusic.x 판 차이) · X68000 MIDI 무음 25 — 원인 목록은 `hoot-status.md`
- [ ] 아케이드 무음: deco wizdfire (zip에 사운드 ROM 없음) · hangzo, System 16 eswat (ROM 없음), 세가 PCM endurob2 × 2 · outrunb · smgp
- [ ] 86 PCM · OPNA ADPCM RAM · fm_mix / ssg_mix · pcm_mix 옵션 반영 (지금 무시)
- [x] POKEY 칩 + VGM BBh — v2.1.31 (아타리 System 1 효과음 · 칩 시험 49곡 더 소리)
- [ ] TMS5220 음성 (아타리 System 1 96곡: Indy 92 · Road Runner 3, VIA 6522 뒤)
- [ ] X68000 PCM8 (TRAP #2) 12항목, mxdrv 형식 3
- [ ] M72 DAC 샘플 (북 · 목소리) — 포트 80h~84h · 주기 NMI · v0 ROM
- [ ] uPD7759 마스터 모드 (자기 ROM) — VGM bit31
- [ ] QSound를 진짜 DSP로 (dl-1425 ROM이 생기면) — 지금 HLE는 3D · 반향 · 필터 없음
- [ ] 세가 PCM 클럭 확인 (Out Run 계열 4MHz · Hang-On 계열 8MHz는 MAME 기억)
- [ ] **귀로 확인**: 아케이드 보드들 (8-8a~h) · PC-98 · X68000 템포 · 음높이를 hoot / MAME와 비교 — "소리 남"은 처음 몇 초 키온 수만 본 것
- [ ] 곡 길이 · 끝 찾기 (지금 기계 흉내 곡은 3분 고정), 이동(seek) 빠르게 (기계 상태 저장 · 되돌리기)
- [ ] VGM 내보내기 때 듀얼 칩 클럭 (SN76489 2MHz + 4MHz, OKI 1MHz + 2MHz)이 같은 클럭으로 적히는 문제

---

## 5. 해야 할 일 — 형식 · 기능 · 코드

- [x] hoot 목록의 없는 zip 목록 · 폴더 구성 CSV (v2.1.14, `HootMissing` · 트리 오른쪽 클릭 · `SelfTest --hootmissing`) — xml_new 기준 없음 678 / 6,570
- [ ] `SelfTest --hootcensus <hoot 폴더>`: 이 파일 1 · 3장 숫자를 다시 만드는 진단 (지금은 스크래치 파이썬)
- [ ] 트리: 장비 이름 정리 (PC-98 / PC-9801 / PC-9821, Genesis / Mega Drive …), 제작사 표시 이름, zip은 있지만 .kss가 없는 KSS 71항목 미리 빨강
- [ ] 트리: 기계 흉내 항목의 무음 · 확인 안 됨 표시 (조사 결과를 색으로)
- [ ] 칩 곡 불러오기를 UI 스레드 밖으로 (NSF 0.4~0.8초 · HES 1.5초 · 기계 흉내 곡 렌더 동안 멈춤)
- [ ] 곡 길이 자동 (KSS · NSF · HES는 m3u 없으면 2분): 무음 · 되풀이 감지
- [ ] 칩 음량 맞추기 (N163 · FDS · 아케이드 칩 사이 비율)
- [ ] VRC7 내장 악기 표를 emu2413 원본과 대조
- [ ] 형식: .ay (ZX · CPC) · .ym (아타리 ST, LHA 풀기 필요) · D00 · RAD · A2M (AdLib 트래커)
- [ ] 형식: SAP (6502 + POKEY) · SID (6502 + SID)
- [ ] VGM 듀얼 칩 나머지: YM2151 둘째 칩 (YM2203 v2.1.25 · AY-3-8910 v2.1.38은 됨) (3.1의 robokid · gaiden · taitosj에 필요)
- [ ] 칩 곡 VGM 내보내기 (KSS · NSF · HES · S98 → .vgm, 0xD7 확장은 경고)
- [ ] LHA / LZH · 7z 압축 폴더
- [ ] HES 효과음 줄 숨기기, Jukebox 곡 정보에 칩별 채널 · 확장 음원 표시

---

## 6. 남겨 둔 일 (보류 — 지금은 하지 않음)

- [ ] XM · IT를 Tracker 화면에서 열기 · S3M으로 저장 — main은 다른 PC의 `ExtModule` (Jukebox 재생 전용)을 쓰기로 함. 이 세션의 다른 구현 (`XmItFormat` · `XmItPlayer`, Tracker 화면 · S3M 저장 · `SelfTest --tracker`)은 로컬 브랜치 `backup/xmit-2.0.11`에만 있음 (푸시 안 함)
- [ ] PMD · FMP · mucom88 드라이버 이식 — 지금은 x86 · Z80 위에서 원래 드라이버를 돌리는 길로 대신함
- [ ] MGS (MSX, MGSDRV 바이너리 필요)
- [ ] YMF278B (OPL4) — 샘플 ROM 덤프 필요
- [ ] OPNA 리듬 ROM 쓰기 (`2608_BD.wav` 등을 사용자가 넣으면), OPN SSG-EG · CSM, Y8950 ADPCM
- [ ] Nuked-OPN2 · Nuked-OPM 급 정확도 (지금은 근사)
- [ ] 안드로이드 이식 (Avalonia / MAUI 결정, `AudioTrack` 출력, 터치 피아노롤, SAF)
- [ ] 앱 시작 페이지 (지금 Jukebox) 바꾸기 여부

---

## 7. 알려진 한계

- OPM · OPN 계열 · 새 아케이드 칩 (uPD7759 · QSound HLE · SegaPCM)은 근사 에뮬레이션
- VRC6 · N163 · MMC5는 VGM 예약 명령 0xD7, 남코 CUS30은 0xD8을 안에서만 씀 → VGM 파일로 내보내면 다른 플레이어에서 그 파트가 안 남
- 듀얼 칩 클럭이 다른 hoot 곡 (System 1 · 데이터 이스트)은 VGM 파일로 내보내면 둘째 칩 클럭이 첫째와 같아짐
- zip 안 곡을 고쳐도 zip은 바뀌지 않음 (다른 이름으로 저장)
- 시험 자료 (`_incoming/Hootarchive` · `D:\Retro`)는 저장소에 없음 — 없으면 실제 파일 시험은 SKIP
