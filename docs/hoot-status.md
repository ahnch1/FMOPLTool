# hoot 재생 작업 현황 (2026-11-20, v2.1.13)

다른 PC(회사)에서 이어 하기 위한 기록. 자세한 할 일 목록은 원래 `_incoming/Todo.md`(git에 안 들어감)에 있었고, 그중 이어 하는 데 필요한 것을 여기에 옮겼다.

## 0. 이어 하기 전에

- **시험 자료는 저장소에 없다**: `_incoming/Hootarchive/` (hoot 배포본: `xml_new/` 목록 · `pc98/` 1,709 zip · `pc/` 480 zip · `snes/` · `msx/` · `nes/` · `gb/` …)는 `.gitignore`. 회사 PC에도 같은 자리(`_incoming/Hootarchive`)에 두면 실제 파일 시험 · 진단이 돈다. 없으면 실제 파일 시험은 `SKIP`으로 넘어간다 (합성 시험은 그대로 통과).
- 작업 방식: 단계마다 커밋 하나 (`vX.Y.Z: …` 한국어 메시지), `Directory.Build.props` 버전 마지막 번호 올림, README 갱신, SelfTest 전부 통과 후 커밋.
- 시험: `dotnet run -c Release --project tests/AdlibComposer.SelfTest` (전체, 지금 890개 통과), 묶음만: `-- --test pc98` · `hoot98` · `hootgen` · `hootmsx` · `hoot88` · `hootx1` · `hootneo` · `hoottaito` · `hootopm` · `hootarcade` · `hootdeco` · `hootms1` · `hoots16` · `hootcps2` · `hootspcm` · `hootmd` · `hootx68` · `pcat` · `hoot`.
- hoot 문서: http://retropc.net/yui/hoot/PC98.TXT (https 안 됨, http로). 그 밖의 기종(PC/AT · MSX · PC-88 · X1 …) 문서는 없음 — 목록의 시작 코드(rom "patch" 등)를 역어셈블해 규약을 맞춘다.

## 1. 지금까지 (v1.21.0 ~ v2.1.13)

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
| 2.1.68 | 캡콤 Bionic Commando | hoot capcom bionicc: `HootOpmBoard.BionicCommando` (MAME bionicc: 8000h YM2151 상태 폴링, A000h 명령 NMI, RAM C000h, OKI 없음) |
| 2.1.67 | 캡콤 YM2203 × 2 더함 (Commando) | hoot capcom commando: `HootCapcomOpn` 배치에 RAM 자리 (Commando = 4000h — 처음엔 C000h 고정이라 스택이 안 써져 다시 부팅) |
| 2.1.66 | 캡콤 YM2203 × 2 더함 | hoot capcom sidearms (Side Arms) · blktiger (Black Tiger): `HootCapcomOpn` 배치 둘 더 |
| 2.1.65 | 8-9h 더함 (비디오시스템 welltris · fromanc2) | hoot videosystem welltris (Welltris · Quiz 18-Kin) · fromanc2 (Final Romance 2 · R): `HootYm2610Board` 배치 둘 더 |
| 2.1.64 | 8-9n (윤성) | hoot yunsung nmg5 · pclubys (Multi 5 · Search Eye · Wonder Stick · Puzzle Club · Garogun Seroyang · 7 Ordi): `Audio/HootYunsung.cs` (Z80 + YM3812 + OKIM6295) |
| 2.1.63 | 8-9m 더함 (세이부 raiden2) | hoot seibu raiden2 (Raiden II · Raiden DX): `HootSeibu` YM2151 + OKIM6295 × 2 변형 |
| 2.1.62 | 8-9m (세이부 사운드 시스템) | hoot seibu raiden (Raiden · Toki no Mahjong Paradise · Heated Barrel · Seibu Cup Soccer): `Audio/HootSeibu.cs` (Z80 + YM3812 + OKIM6295) |
| 2.1.61 | 8-9l (타이토 Bubble Bobble) | hoot taito bubblebobble (Bubble Bobble · Tokio): `Audio/HootTaitoBubble.cs` (Z80 + YM2203 + YM3526) |
| 2.1.60 | 8-8j 더함 (타이토 B YM2203) | hoot taito masterw (Master of Weapon · Tetris · Champion Wrestler) · viofight (Violence Fight · Hit the Ice · Camel Try): `HootTaitoOpm` YM2203 (+ OKIM6295) 변형 |
| 2.1.59 | 8-9k 더함 (Y8950 ADPCM) | 새 칩 Y8950 ADPCM (`Audio/Chips/Y8950Adpcm.cs`, delta-T, 이 프로그램 안 명령 DCh) — hoot snk 3526_8950 목소리 항목 |
| 2.1.58 | 8-9k 더함 (SNK fitegolf) | hoot snk fitegolf (Fighting Golf · Country Club): `HootSnkOpl` YM3812 하나 (VGM 5Ah 그대로) |
| 2.1.57 | 8-9k (SNK OPL × 2) | hoot snk 3526_8950 · 3526x2 (Victory Road · Psycho Soldier · Bermuda Triangle · TouchDown Fever · Guerrilla War · Athena · Ikari Warriors): `Audio/HootSnkOpl.cs` — YM3526 + Y8950 / YM3526 × 2를 YMF262 하나로 |
| 2.1.56 | 8-9i 더함 (코나미 battlantis) | hoot konami battlantis (Battlantis · Rack 'em Up): `HootKonamiK007232` 배치 — YM3812 × 2를 YMF262 하나로 (#1 = 뱅크 0, #2 = 뱅크 1, C0h 왼 · 오 bit, 01h 파형 허락 → E0h 가림) |
| 2.1.55 | 8-9j (코나미 Z80 + YM2203) | hoot konami ironhors (Iron Horse · Scooter Shooter): `Audio/HootKonamiIronHorse.cs` |
| 2.1.54 | 8-9a 더함 (코나미 frogger) | hoot konami frogger (Frogger · Hustler): `HootKonamiAy` frogger 배치 (AY 하나, I/O bit6 자료 · bit7 주소, RAM 4000h, 사운드 ROM 앞 800h bit0 · 1 되돌림, 타이머 bit3 · 5 바꿈) |
| 2.1.53 | 8-9e 더함 (코나미 lethal) | hoot konami lethal (Lethal Enforcers · G.I. Joe): `HootKonami54539` lethal 배치 (ROM ~EFFFh, RAM F000h, K054539 F800h, K054321 FC00h, YM2151 없음) |
| 2.1.52 | 8-9i 더함 (코나미 Twin16) | uPD7759 마스터 모드 (칩이 ROM 머리 표에서 직접, VGM 8Ch bit31 · ROM 블록 92h · 뱅크 레지스터 3) + hoot konami twin16 (Devil World · Vulcan Venture · Final Round · Cue Brick · M.I.A.): `HootKonamiK007232` 배치 |
| 2.1.51 | 8-9i 더함 (코나미 K007232 보드 셋) | hoot konami salamander (Salamander · Life Force) · hcastle (Haunted Castle · City Bomber · Kitten Kaboodle — YM3812 + SCC + K007232) · crimfght (Crime Fighters · Aliens): `HootKonamiK007232` 배치 표 |
| 2.1.50 | 8-9i (코나미 K007232) | 새 칩 K007232 (`Audio/Chips/K007232.cs`, 이 프로그램 안 명령 DBh) + hoot konami scontra · thundercross (Super Contra · Gang Busters · Crazy Cop · Thunder Cross · Block Hole): `Audio/HootKonamiK007232.cs` |
| 2.1.49 | 8-9h (비디오시스템 · 사이쿄 YM2610) | hoot videosystem turbofrc · aerofgt (Turbo Force · Spinal Breakers · Power Spikes · Karate Blazers · Aero Fighters · Goal! '92), psikyo gunbird (Gunbird · Battle K-Road · Strikers 1945): `Audio/HootYm2610Board.cs` |
| 2.1.48 | 8-9a 더함 (코나미 pooyan) | hoot konami pooyan (Pooyan · Loco-Motion · Tactician): `HootKonamiAy` timeplt 보드, Pooyan 코드는 bit7을 빼고 |
| 2.1.47 | 8-9g (코나미 System GX) | hoot konami systemgx (17항목: Fantastic Journey · Lethal Enforcers II · Golfing Greats 2 · Puzzle Dama · Racin' Force · Soccer Superstars · Dragoon Might · Taisen Puzzle-dama · Tokimeki Memorial · Daisu-Kiss · Run and Gun 2 · Rushing Heroes · Salamander 2 · Sexy Parodius · Tokkae · Versus Net Soccer · Winning Spike): `Audio/HootKonamiGx.cs` (68000 + K054539 × 2) |
| 2.1.46 | 8-9f 더함 (세가 System 32) | hoot sega system32 (Air Rescue · Spider-Man · Arabian Fight · Burning Rival · Golden Axe RoG · Holosseum · Dark Edge · F1 Super Lap · F1 Exhaust Note): `HootSegaSystem18` System 32 배치 |
| 2.1.45 | 8-9f (세가 System 18) | 새 칩 RF5C68: `Audio/Chips/Rf5c68.cs`, VGM 둘째 YM2612 · RF5C68, hoot sega system18 (Shadow Dancer · Alien Storm · Bloxeed · Moonwalker · Clutch Hitter · Pontoon): `Audio/HootSegaSystem18.cs`; `FindFile` 앞부분 맞춤 대안 |
| 2.1.44 | 8-9e (코나미 K054539, Z80 판) | 새 칩 K054539: `Audio/Chips/K054539.cs` (VGM A0h · D3h · 8Ch, 둘), hoot konami 054539 · 054539x2 (Xexex · Bucky O'Hare · Moo Mesa · X-Men · Poly-Net Warriors · Run and Gun · Metamorphic Force · Martial Champion · Mystic Warriors · Violent Storm): `Audio/HootKonami54539.cs` |
| 2.1.43 | 8-9d (코나미 K053260) | 새 칩 K053260: `Audio/Chips/K053260.cs` (VGM ACh · BAh · 8Eh), hoot konami 053260 (Lightning Fighters · Parodius · Escape Kids · Thunder Cross II · Vendetta · Punk Shot · Sunset Riders · Bells & Whistles · TMNT 2 · Quiz Gakumon · Simpsons): `Audio/HootKonami53260.cs` |
| 2.1.42 | 8-9c 더함 (System 2 C140 요청) | PCM 묶음 7200h (FM으로 1.5초 시도 → 없으면 다시 부팅해 7200h), C140 출력 × 8 |
| 2.1.41 | 8-9c (남코 System 2, 일부) | 새 칩 C140: `Audio/Chips/NamcoC140.cs` (VGM A8h · D4h · 8Dh), hoot namco system2: `Audio/HootNamcoSys2.cs` (6809 + YM2151 + C140) — FM 곡만 |
| 2.1.40 | 8-9b (타이토 SJ) | hoot taito taitosj (Elevator Action · Front Line · Alpine Ski · Time Tunnel · Wild Western · Bio Attack · Tinstar · Kick Start · Sea Fighter Poseidon …): `Audio/HootTaitoSj.cs` (Z80 + AY × 3 + DAC), VGM 셋째 AY (안 명령 D9h) |
| 2.1.39 | 8-9a (코나미 Z80 + AY × 2) | hoot konami scramble · timeplt (Scramble · Super Cobra · Amidar · The End · Strategy X · Turtles · Super Fighter X · Time Pilot · Tutankham · Jungler): `Audio/HootKonamiAy.cs`, hoot 롬 이름이 다른 zip은 기판 위치 코드로 찾기 (`HootArchive.ByBoardLocation`) |
| 2.1.38 | 8-8z (아이렘 M62 / M52) | hoot irem m62 (Kung-Fu Master · Lode Runner 1~4 · Spelunker 1 · 2 · Kid Niki · Battle Road · Lot Lot · Horizon · Youjyuden · Moon Patrol · Traverse USA · 10-Yard Fight): `Audio/HootIremM62.cs` (M6803 + AY × 2 + MSM5205), VGM 듀얼 AY |
| 2.1.37 | 8-8y (남코 Pac-Land 계열) | hoot namco wsg63701 (Dragon Buster · Pac-Land · Baraduke · Metro-Cross · Sky Kid · Hopping Mappy): `HootNamcoSys86` 슬롯 요청, 6301 문서 밖 명령 12h · 13h |
| 2.1.36 | 8-8x (6801 계열 CPU · 남코 System 86) | 새 CPU 6801 / HD63701: `Audio/Cpu/Cpu6801.cs`, hoot namco system86 (Sky Kid DX · Return of Ishtar · Genpei · Rolling Thunder · Wonder Momo): `Audio/HootNamcoSys86.cs` (MCU + YM2151 + CUS30) |
| 2.1.35 | 8-8w (남코 매피 계열) | hoot namco wsg6809 (Mappy · Super Pac-Man · Pac & Pal · Phozon · Grobda · Libble Rabble · Druaga · Gaplus · Dig Dug II · Motos · Toy Pop): `Audio/HootNamcoWsg6809.cs` (6809 + 15XX), `NamcoCus30` 15XX 모드 |
| 2.1.34 | 8-8v 더함 (CUS30) | 새 칩 남코 CUS30: `Audio/Chips/NamcoCus30.cs`, VGM 안 명령 D8h (`VgmSong.Cus30Clock`), System 1 효과음 슬롯 시도 |
| 2.1.33 | 8-8v (남코 System 1) | hoot namco system1 (22항목: Shadowland · Dragon Spirit · Blazer · Pac-Mania · Galaga '88 · World Stadium …): `Audio/HootNamcoSys1.cs` (6809 + YM2151, CUS30 소리는 아직) |
| 2.1.32 | 8-8u (6809 CPU · 데이터 이스트 6809) | 새 CPU 6809: `Audio/Cpu/Cpu6809.cs`, hoot dataeast brkthru · exprraid (Break Thru · Darwin 4078 · Express Raider): `Audio/HootDeco6809.cs` (6809 + YM2203 + YM3526) |
| 2.1.31 | 8-8t 더함 (POKEY) | 새 칩 POKEY: `Audio/Chips/Pokey.cs`, VGM 머리말 B0h · 명령 BBh (읽기 · 재생 · 쓰기), 아타리 System 1 효과음 |
| 2.1.30 | 8-8t (아타리 System 1) | hoot atari system1 (Marble Madness · Peter Pack-Rat · Indiana Jones · Road Runner): `Audio/HootAtariSys1.cs` (6502 + YM2151, POKEY · TMS5220 없음) |
| 2.1.29 | 8-8s (캡콤 YM2203 × 2) | hoot capcom opn2 · rushcrsh · tigerroad (Ghosts'n Goblins · Gun.Smoke · Section Z · Legendary Wings · Trojan · 1943 · 1943 Kai · Avengers · Speed Rumbler · Tiger Road · F-1 Dream): `Audio/HootCapcomOpn.cs` (Z80 + YM2203 × 2) |
| 2.1.28 | 8-8r (테크모 YM3812 + MSM5205) | hoot tecmo rygar · gemini (Rygar · Silk Worm · Gemini Wing · Back Fire): `Audio/HootTecmoAdpcm.cs` |
| 2.1.27 | 8-8q (테크모 Ninja Gaiden) | hoot tecmo gaiden (Shadow Warriors · 龍剣伝 · Tecmo Knight · Raiga Strato Fighter): `Audio/HootGaiden.cs` (Z80 + YM2203 × 2 + OKIM6295) |
| 2.1.26 | 8-8e 더함 (테크모 16) | hoot tecmo tecmo16 (Final Star Force · Ganbare Ginkun · Riot): `HootOpmBoard.Tecmo16` 배치, `Layout.BootSeconds` |
| 2.1.25 | 8-8p (UPL robokid) | hoot upl robokid (Psychic 5 · Ark Area · Mutant Night · Atomic Robo-kid · Omega Fighter): `Audio/HootUpl.cs` (Z80 + YM2203 × 2), VGM 재생기 듀얼 YM2203 (`VgmSong.Ym2203Clock2`, 명령 A5h) |
| 2.1.24 | 8-8o (알파 68k2) | hoot alpha 68k2 (Sky Adventure · Gang Wars · Super Champion Baseball · Time Soldiers · Sky Soldiers · Gold Medalist): `Audio/HootAlpha68k.cs` (Z80 + YM2203 + YM2413 + DAC, 주기 NMI) |
| 2.1.23 | 8-8n (니치부츠) | hoot nichibutsu terracre · terraf: `Audio/HootNichibutsu.cs` (Z80 + YM3526/YM3812 + DAC → VGM YM2612 DAC, 주기 IRQ, 명령 (코드 << 1) | 1) |
| 2.1.22 | 8-8m (SNK68) | hoot snk 3812 (P.O.W. · SAR · Street Smart · Ikari III): `Audio/HootSnk68.cs` (Z80 + YM3812 + uPD7759, 명령 0Dh × 2 = 음악 켬) |
| 2.1.21 | 8-8b 더함 (midres) | hoot dataeast midres (Midnight Resistance): `Audio/HootDeco.cs` midres 배치 — YM3812 (타이머 = IRQ2) · YM2203 · OKIM6295 · 명령 138000h |
| 2.1.20 | — (UI) | **Hoot 페이지** (PLAY, Jukebox 옆): 같은 `JukeboxView` + 왼쪽 `Shell/HootBrowser.cs` (목록 고르기 · 설정 · `HootTree`), 왼쪽 목록 접점 `ISongList`, 설정 Preferences → Hoot (HootXmlFolders · HootDataFolder), 처음 열 때 읽음. 왼쪽 목록 [hoot] 버튼은 없앰 |
| 2.1.19 | 8-8l (데이터 이스트 6502) | hoot dataeast cobracom · drgninja · karnov · triothep · lastmisn · makyosen: `Audio/HootDeco6502.cs` (6502 + YM2203 + YM3812/YM3526 + OKIM6295), 6502 `DecimalMode` · `OpcodeMap` |
| 2.1.18 | 8-8k (토아플랜 1 · generic) | hoot toaplan generic (Flying Shark · Twin Cobra · Rally Bike · Truxton · Hellfire · Zero Wing · Demon's World · Out Zone · Gulf War 2): `Audio/HootToaplan.cs` (Z80 + YM3812, 같이 쓰는 RAM 명령) |
| 2.1.17 | 8-8j (타이토 YM2151 · fullt) | hoot taito fullt (Rastan · Top Speed · Asuka · Cadash · Exzisus · Visco 3): `Audio/HootTaitoOpm.cs` (Z80 + YM2151 + TC0140SYT + MSM5205 → VGM OKIM6258) |
| 2.1.16 | 8-6 더함 (메가드라이브 1c00 · koei) | hoot megadrive 1c00 (세가 SMPS Z80) · koei (드라이버 · 우편함 · 곡 표를 카트리지에서 찾음): `Audio/HootMegaDrive.cs`, 진단 `--hootkoeitrace` (MD 버스 68000 부팅) |
| 2.1.15 | 8-8i (CPS-1 QSound · ZN) | hoot capcom cps1qs · zn: `Audio/HootCps2.cs` (Kabuki 풀기 · 래치 + NMI), Z80 `OpcodeRead` (명령 바이트 따로 읽기) |
| 2.1.14 | — (도구) | hoot 목록의 없는 zip 목록 · 폴더 구성 CSV: `Formats/HootMissing.cs` (트리 오른쪽 클릭, `--hootmissing`) |
| 2.1.13 | 8-8h (세가 PCM 보드 · 새 칩 SegaPCM) | hoot sega outrun · X · Y · hangon 계열: `Audio/HootSegaPcmBoard.cs`, 새 칩 `Audio/Chips/SegaPcm.cs` (VGM C0h) |
| 2.1.12 | 8-8g (CPS-2 · 새 칩 QSound) | hoot capcom cps2: `Audio/HootCps2.cs`, 새 칩 `Audio/Chips/QSound.cs` (DSP 없는 HLE, VGM C4h) |
| 2.1.11 | 8-8f (System 16 · 새 칩 uPD7759) | hoot sega system16a · 16b: `Audio/HootSegaSystem16.cs`, 새 칩 `Audio/Chips/Upd7759.cs` (슬레이브 모드, VGM B6h) |
| 2.1.10 | 8-8e (Mega System 1) | hoot jaleco megasys1: `Audio/HootMegaSys1.cs` (MC68000 다시 씀, YM2151 + OKI 둘) |
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
- `App/Shell/HootTree.cs` · `HootBrowser.cs` (Hoot 페이지 왼쪽, v2.1.20 — 그 전엔 `FileBrowser.cs`의 [hoot] 버튼) · `Views/JukeboxView.cs` (.hoot MIDI 판 → MIDI 재생, MT-32 판 강제)

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
| 데이터 이스트 6502 (cobracom · drgninja · karnov · triothep · lastmisn · makyosen) | 20 / 20, 곡 283 / 285 (무음 2 = cobracom SE 12 · ghostb 효과음 하나) | `--test hootdeco65` (HOOT_SONGS=1 = 곡마다) |
| UPL robokid | 5 / 5, 108 / 109곡 (남은 하나 = 정지 명령) | `--test hootupl` (HOOT_SONGS=1) |
| 알파 68k2 | 6 / 6, 83곡 모두 (YM2413 키온) | `--test hootalpha` (HOOT_SONGS=1) |
| 니치부츠 (terracre · terraf) | 10 / 10, 곡 161 / 161 (terracre 0x00 = 목록의 구분 줄) | `--test hootnichi` (HOOT_SONGS=1) |
| SNK68 (snk 3812) | 4 / 4, 146곡 모두 (키온 또는 uPD7759) | `--test hootsnk` (HOOT_SONGS=1, HOOT_SNKINIT=16진,… 부팅 명령 바꾸기, HOOT_SNKSCAN=곡 = 부팅 명령 00h~FFh 훑기, HOOT_PC · HOOT_PCQ=주소,…) |
| 토아플랜 1 · Twin Cobra (generic) | 9 / 9, 125곡 모두 (키온) | `--test hoottoaplan` (HOOT_SONGS=1 = 곡마다, HOOT_PROBE=1 = I/O 포트 · 68000 쪽 RAM 후보) |
| 타이토 YM2151 보드 (fullt) · 타이토 B YM2203 (masterw · viofight) | 15 / 15, 241곡 모두 (FM 키온 또는 MSM 바이트) | `--test hoottaitoopm` (HOOT_SONGS=1 = 곡마다, HOOT_REGS · HOOT_PC · HOOT_PCQ · HOOT_RAM · HOOT_MSMLOG) |
| 비디오시스템 · 사이쿄 YM2610 | 13 / 13, 275곡 모두 (welltris · fromanc2 106곡 더함) | `--test hoot2610` (HOOT_SONGS=1) |
| 코나미 K007232 (scontra · thundercross · salamander · hcastle · crimfght · twin16 · battlantis) | 21 / 21, 431곡 중 424 (무음 = 00h 머리 · 정지 항목 6 · Thunder Cross 8Fh) | `--test hootk7232` (HOOT_SONGS=1) |
| 코나미 System GX (systemgx) | 17 / 17, 489곡 모두 | `--test hootgx` (HOOT_SONGS=1) |
| 세가 System 18 · 32 (system18 · system32) | 15 / 15, 388 / 397곡 (남은 9 = 정지 명령) | `--test hoots18` (RF5C68 합성 포함, HOOT_SONGS=1) |
| 코나미 K054539 (054539 · 054539x2 · lethal) | 12 / 12, 317 / 318곡 (lethal 51곡 모두) | `--test hootk54` (K054539 합성 포함, HOOT_SONGS=1, HOOT_DEBUG=1 = YM2151 타이머) |
| 코나미 K053260 (053260) | 11 / 11, 606 / 616곡 (남은 10 = 정지 명령 7 · 효과음 3) | `--test hootk53` (K053260 합성 포함, HOOT_SONGS=1) |
| 남코 System 2 (system2) | 25 / 26항목, 827 / 870곡 (C140 키온 498곡) — 무음 43 = Four Trax 27 (옵션 foutrax) · Final Lap 9 · Rolling Thunder 2 4 · 그 밖 3 | `--test hootns2` (HOOT_SONGS=1, HOOT_DEBUG=1 = C140 레지스터 분포) |
| 타이토 SJ (taitosj) | 12 / 12, 285곡 모두 | `--test hootsj` (HOOT_SONGS=1) |
| 코나미 Z80 + AY (scramble · timeplt · pooyan · frogger) | 15 / 15, 139 / 200 (pooyan 18 / 18, frogger 34 / 40) — 무음 41 = 한 곡을 채널마다 나눈 ch2 · ch3 파트와 일부 BGM (혼자 보내면 AY 쓰기 없음) | `--test hootkonay` (HOOT_SONGS=1) |
| 코나미 Z80 + YM2203 (ironhors) | 2 / 2, 40곡 모두 | `--test hootih` (HOOT_SONGS=1) |
| SNK OPL (3526_8950 · 3526x2 · fitegolf) | 10 / 10, 372곡 중 347 (무음 25 = 머리 줄 · Victory Road 목소리 D2h~DBh) (무음 77 = Y8950 ADPCM 목소리 · 머리 줄) | `--test hootsnkopl` (HOOT_SONGS=1) |
| 타이토 Bubble Bobble (bubblebobble) | 2 / 2, 28곡 중 26 (무음 = 머리 줄 · SSG-OFF) | `--test hootbub` (HOOT_SONGS=1, HOOT_DEBUG=1 = PC 표) |
| 세이부 사운드 시스템 (raiden · raiden2) | 6 / 6, 99곡 중 90 (무음 = Raiden 북 파트 · Cup Soccer 홀수 번호 · raiden2 둘) | `--test hootseibu` (HOOT_SONGS=1) |
| 윤성 (nmg5 · pclubys) | 6 / 6, 64곡 모두 | `--test hootys` (HOOT_SONGS=1) |
| 아이렘 M62 / M52 (m62) | 15 / 15, 137 / 145 (남은 8 = Traverse USA 효과음 — MSM #2로 보임) | `--test hootm62` (HOOT_SONGS=1) |
| 남코 Pac-Land 계열 (wsg63701) | 6 / 6, 157곡 모두 | `--test hootns86` (HOOT_SONGS=1, HOOT_DEBUG=1 = 슬롯 · 요청 바이트) |
| 남코 System 86 (system86) | 5 / 5, 71곡 모두 (63701X 음성은 없음) | `--test hootns86` (6801 합성 둘 포함, HOOT_SONGS=1) |
| 남코 매피 계열 (wsg6809) | 11 / 11, 262 / 263 (남은 하나 = Phozon "Sound Initialize" 명령) | `--test hootwsg` (HOOT_SONGS=1) |
| 남코 System 1 (system1) | 22 / 22, 799 / 818곡 (남은 19 = 정지 명령) — FM 또는 CUS30 | `--test hootns1` (HOOT_SONGS=1) |
| 데이터 이스트 6809 (brkthru · exprraid) | 3 / 3, 19곡 모두 (YM3526 키온) | `--test hootdeco69` (6809 합성 시험 둘 포함, HOOT_SONGS=1) |
| 아타리 System 1 (system1) | 4 / 4 (Road Blasters는 곡 목록 없음), 179 / 275곡 — 무음 96 = TMS5220 음성 (Indy 92 · Road Runner 3 · 음성칩 시험 1) | `--test hootatari1` (HOOT_SONGS=1, HOOT_DEBUG=1, HOOT_PCS=1 = PC 분포 · RAM 0200h~) |
| 캡콤 YM2203 × 2 (opn2 · rushcrsh · tigerroad · commando · sidearms · blktiger) | 15 / 15, 388 / 390곡 (무음 = Gun.Smoke · Commando 정지 명령) | `--test hootcapopn` (HOOT_SONGS=1, HOOT_DEBUG=1 = 많이 쓴 레지스터) |
| 테크모 YM3812 + MSM5205 (rygar · gemini) | 4 / 4, 122곡 모두 (MSM5205 23곡) | `--test hoottecmo` (HOOT_SONGS=1) |
| 테크모 Ninja Gaiden (gaiden) | 4 / 4, 231 / 300 (곡은 모두 — 무음 = Strato Fighter 효과음 · 목소리 65, 龍剣伝 Round Clear 3 · 4, 코드 모름 1) | `--test hoottecmo` (HOOT_SONGS=1) |
| 테크모 16 (tecmo16, HootOpmBoard) | 3 / 3, 112곡 모두 (8곡 = OKI 효과음 하나) | `--test hootopm` (HOOT_SONGS=1, HOOT_PCQ=주소,… · HOOT_PCR=시작,끝 — PCR은 부팅부터 셈) |
| 캡콤 Bionic Commando (bionicc, HootOpmBoard) | 1 / 1, 14곡 모두 | `--test hootopm` (HOOT_SONGS=1) |
| CPS-1 · Double Dragon 2 · 3 (YM2151 + OKIM6295) | 39 / 39 (FM 키온 16개 또는 OKI 2번 이상, 처음 6곡까지) | `--hootopmsurvey roms 200 6` |
| 세가 System 1 · 아이렘 M72 계열 | 52 / 52 — system1 29/29 · m72 23/23 | `--hootarcadesurvey roms 200 6` (HOOT_ONLY=zip이름,…) |
| 데이터 이스트 HuC6280 보드 | 41 / 43 — 빠진 2: wizdfire (zip에 사운드 ROM 없음), hangzo (시제품, 곡 목록이 틀뿐) | `--hootdecosurvey roms 300 6` |
| 잘레코 Mega System 1 | 16 / 16 | `--hootms1survey roms 100 6` |
| 세가 System 16A · 16B | 37 / 38 (eswat = zip에 사운드 ROM 없음), uPD7759는 처음 10곡 안에서 쓰는 8개에서 확인 | `--hoots16survey roms 100 4` |
| 캡콤 CPS-2 (QSound HLE) | 38 / 38 | `--hootcps2survey roms 100 6` |
| 세가 PCM 보드 (Out Run · X · Y · Hang-On 계열) | 20 / 24 | `--hootspcmsurvey roms 100 6` |
| 메가드라이브 (generic_z80 · iyinc · 1c00 · koei) | 112 / 150 (smd zip에 있는 것) — generic_z80 70/108 · iyinc 23/23 · 1c00 5/5 · koei 14/14 | `--hootmdsurvey smd 400 6` (HOOT_TYPE · HOOT_SONGS) |
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
--hootms1survey <roms 폴더> [zip 수] [초]   잘레코 Mega System 1 (YM2151 타이머 레지스터 표시)
--hoots16survey <roms 폴더> [zip 수] [초]   세가 System 16A · 16B (FM · uPD7759 바이트 수, HOOT_ONLY · HOOT_PC)
--hootcps2survey <roms 폴더> [zip 수] [초] [모양] 캡콤 CPS-2 (명령 묶음 모양 0 = C001h 코드 · 1 = C000h 코드, HOOT_ONLY)
--hootspcmsurvey <roms 폴더> [zip 수] [초]  세가 PCM 보드 (HOOT_ONLY · HOOT_PC)
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
  - **1c00 — ✅ v2.1.16 (5 / 5)**: 세가 SMPS Z80 (변수 칸 1C00h~). 소리 큐 1C0Ah~1C0Ch (1C09h = 지금 소리) — ryukoken 68000 코드 `move.b d7,$A01C0A`. 곡 표 위치 1C02h~1C03h는 68000이 씀 (ryukoken = 8000h, 뱅크 창) → 올린 그림에서 0000h · FFFFh면 8000h로. data · size가 없는 castlillj는 topaddr부터 1C00h바이트. 진단 `--hootmdprobe zip`: 요청 주소를 1C00h~1C3Fh에서 바꿔 보기
  - **koei — ✅ v2.1.16 (14 / 14)**: hoot 옵션이 하나도 없어 카트리지에서 찾는다. 진단 `--hootkoeitrace rom|zip [초]` = 작은 MD 버스 (ROM · 작업 RAM · Z80 창 · VDP 상태 · 패드)에서 MC68000으로 게임을 부팅해 68000이 Z80 RAM에 쓰는 것을 PC와 함께 적음 (r3k2 38초 · uw 26초 · nobu 25초에 첫 곡)
    - 드라이버: 68000 적재 루틴 `lea end(pc),a0 · lea start(pc),a1 · suba.l a1,a0 · … dbf` (41FA · 43FA · 91C9) → ROM 조각을 Z80 0000h에. 두 세대: `F3 31 00 20 C3 E0 00` (옛, 11게임) · `F3 31 00 20 CD 00 05` (새: r3k2 · uw · gemfi)
    - 우편함 = 68000 코드의 가장 작은 `movea.l #$A000xx,a1` (옛 07h · 새 09h): 바탕 = 명령, +1 = 인자, +3~+5 = 24비트 ROM 주소 (위 바이트 먼저), +6 ← 02h (Z80이 받으면 지움). 곡 = 명령 2 (멈춤) → 0 (주소) → 3 (인자 1, 연주). 새 세대는 부팅 때 명령 1 · C8h
    - 곡 표: 68000 쪽 32비트 ROM 주소 줄 — 가리키는 것이 게임마다 다르다 (r3k2 = 곡 머리 `08 00 …`, uw = 8바이트 채널 기록, nobu = 곡 묶음 맨 앞의 표). 길이가 곡 코드 최대 ~ +2인 줄을 후보로, 후보마다 첫 곡을 3초 돌려 키온이 가장 많은 것. 추적과 맞음: r3k2 47E92h (1번 = 05C000h) · nobu 07C000h (1번 = 07C0C8h) · uw D0902h (22번 = 0F81CAh)
    - g_nobbus (Bushou Fuuunroku)는 smd 폴더에 zip 없음
  - 아직 없음: 게임별 형식 수십 개 (68000 드라이버: puyopuyo · sonic · gunstar …), smd 폴더에 없는 generic_z80 29 (g_* 아카이브)
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
- **8-8e. 잘레코 Mega System 1 — ✅ v2.1.10 (16 / 16, `Audio/HootMegaSys1.cs`)**
  - MAME megasys1A_sound_map: 68000 7MHz, ROM 000000h~01FFFFh (hoot 목록의 code 두 줄 = ROM_LOAD16_BYTE 짝수 · 홀수), 명령 040000h 읽기 (System C = 060000h — 64th Street · Big Striker · Chimera Beast는 이것이 빠져 처음 무음), YM2151 080001h · 080003h, OKIM6295 #1 0A0001h · #2 0C0001h (4MHz, 핀7 높음), RAM 0E0000h~0FFFFFh
  - 처음엔 YM2151을 060000h로 잘못 둬서 드라이버가 080002h 바쁨 비트 (OKI 상태 F0h)를 기다리며 멈춤 → 배치를 바로잡음
  - 명령 · YM2151 = IRQ 4 (자동 벡터)지만 실제 드라이버는 SR 마스크 7로 막고 YM2151 타이머 A 플래그와 명령을 폴링한다
- **8-8f. 세가 System 16A · 16B + 새 칩 uPD7759 — ✅ v2.1.11 (37 / 38)**
  - "새 칩"은 실물이 아니라 소프트웨어 흉내 — 샘플 ROM은 hoot zip 안에 있다. 예외: CPS-2 QSound를 정확히 하려면 DSP 내부 ROM (dl-1425)이 필요한데 아카이브에 없음 → 할 때는 근사
  - 사운드 ROM을 먼저 읽어 맞춘 배치: YM2151 포트 00h~3Fh, 제어 40h~7Fh (bit7 START · bit6 /RESET · 뱅크), uPD7759 80h~BFh (읽기 bit7 = 바쁨 아님), 명령 C0h~FFh. IRQ 처리기는 명령만 받는다 (YM2151 IRQ를 이어 두면 8초에 10만 번 들어옴 — 연결 안 된 것). 16A는 명령을 NMI로 (0038h는 비어 있음)
  - uPD7759 슬레이브 모드: DRQ (21클럭) = NMI, 처리기가 보조 레지스터 HL'의 바이트를 OUT (C),B로. 칩은 빈 바이트 · 주소 아랫자리 같은 상태에서도 DRQ를 올리고 그 바이트를 다음 바이트로 덮어쓴다 → 재생기 칩은 "DRQ마다 한 바이트"로 짝지음 (읽을 때마다 꺼내면 순서가 밀려 무음)
  - START는 올라가는 가장자리만 (MAME). Ryu Kyu처럼 곡 시작 전부터 스트림을 흘리던 보드는 곡 시작 때 RESET 높음 · START를 다시 쓰고, 재생기 칩은 밀린 바이트를 소리 없이 따라잡는다
  - 뱅크: 171-5358 = D2~D5 중 0인 비트로 ROM 고르기 + D0 · D1 × 4000h (hoot는 ROM을 adpcm offset 간격으로 둠), 171-5521 · 5704 · 5797 = D3 × 20000h + D0~D2 × 4000h, boardtype이 없으면 offset 간격으로 짐작
- **8-8g. 캡콤 CPS-2 + 새 칩 QSound — ✅ v2.1.12 (38 / 38)**
  - Z80 (MAME cps2 qsound_sub_map): 뱅크 8000h~BFFFh = ROM 8000h + (D003h & 0Fh) × 4000h, 공유 RAM C000h~CFFFh, QSound D000h 위 · D001h 아래 · D002h 레지스터, D007h = 준비 (80h), RAM F000h~, IRQ 250Hz
  - 부팅: Z80이 CFFFh ← 77h를 쓰고 FFh가 될 때까지 기다린다 (68000 응답) — 이것이 없으면 00DDh에서 멈춤. IRQ마다 CFFDh = 88h (68000 준비)이면 C00Fh를 보고, FFh가 아니면 C000h~C00Fh 16바이트를 큐로 옮기고 C00Fh ← FFh
  - 명령 묶음: C000h = 위 바이트 (0), C001h = 곡 코드 (68000 공유 RAM 아랫 바이트 차례) — C000h에 코드를 두면 효과음 수준 활동뿐이라 실험으로 확정
  - QSound: 예전 MAME qsound.cpp HLE (뱅크 레지스터가 다음 채널에 걸리는 버릇 포함), 출력 = 클럭 / 166. 실제 DSP (dl-1425) 3D · 반향 · 필터는 없다 — ROM이 아카이브에 없음
- **8-8h. 세가 PCM 보드 + 새 칩 SegaPCM — ✅ v2.1.13 (20 / 24)**
  - 사운드 ROM에서 확인: Out Run · X · Y 보드 = ROM ~EFFFh, 세가 PCM F000h (256바이트 거울, F088h 등), RAM F800h, YM2151 포트 00h · 01h, 명령 = NMI (처리기가 IN B,(C) · C = 40h), 0038h는 주 루프 (Out Run · X는 IRQ를 쓰지 않음, Y는 YM2151 타이머 IRQ). Hang-On 계열 = RAM C000h (SP C800h), YM2203 D000h (IY로 접근), 세가 PCM E000h, 명령 40h = NMI, YM2203 타이머 IRQ
  - 세가 PCM: 끝 레지스터 (ch × 8 + 6) + 1 = 주소 위 바이트 (256바이트 단위)에서 멈춤 · 되풀이, 86h bit0 = 멈춤 (칩이 씀), 뱅크 = (86h &amp; 마스크) &lt;&lt; 자리 — Out Run · X · Hang-On = BANK_512 (64KB), Y 보드 = BANK_12M · 마스크 F8h. Out Run의 32KB PCM ROM은 MAME처럼 64KB 뱅크마다 하나 (거울)
  - 클럭: Out Run · X · Y = 4MHz (31.25kHz), Hang-On 계열 8MHz — 이 둘은 MAME 기억이고 ROM으로 확인 못 함 (음높이가 이상하면 여기부터)
  - 무음 4: endurob2 (eropnx2 = YM2203 둘 해적판 보드, 7F35h에서 멈춤), outrunb (해적판 보드), smgp (Super Monaco GP — X 보드인데 사운드 배치가 다름)
- **8-8i. 캡콤 CPS-1 QSound · ZN — ✅ v2.1.15 (14 / 14)** — CPS-2 길 (`HootCps2`)에 보드 종류 `Board`를 더함
  - CPS-1 QSound (wof · dino · punisher · slammast): Z80 배치 · 명령 묶음은 CPS-2와 같다. Z80 ROM 0000h~7FFFh가 **Kabuki** 암호 (MAME kabuki.cpp) — 명령 바이트 select = 주소 + addr_key, 자료 바이트 select = (주소 ^ 1FC0h) + addr_key + 1. 뱅크 쪽은 암호 없음
  - 열쇠 4개 (MAME *_decode) 중 풀린 명령이 F3 ED 56 31 (DI · IM 1 · LD SP)로 시작하는 것을 고른다 → 이름(클론)에 기대지 않음. 네 게임 모두 맞음 (자료 쪽도 FF FF · 00 F0 … = ZN 드라이버 시작과 같은 모양)
  - Z80에 `OpcodeRead` 추가: M1 읽기 (첫 바이트 · DD/FD/CB/ED 뒤 명령 바이트)만 따로, 즉시값 · 변위 · DDCB 명령 바이트는 보통 읽기
  - ZN (ZN-1 · ZN-2, sfex · sfex2 · sfex2p · starglad · plsmaswd · rvschool · strider2 · techromn · tgmj · ts2): Z80 ROM 암호 없음, C000h~CFFFh = 그냥 RAM. 명령 = 포트 00h 래치 + NMI, 바이트 하나에 NMI 하나 — 드라이버가 둘씩 묶는다 (sfex: F100h + F015h에 쌓고 F017h로 셈). 곡 코드 16비트 (tgmj 0x409, techromn 0x8020)를 **위 바이트 먼저** (거꾸로면 16비트 코드 게임 4개 무음) · 1ms 사이
  - 곡마다 확인 (`HOOT_SONGS=5`): 곡 코드마다 쓰기 수가 다름 = 정말 곡을 고름. "Credit" 코드는 짧은 효과음 (키온 80~90), tgmj 0xFF04 · 0xFF05 (모노 · 스테레오 전환)는 소리 없는 명령
  - Z80 ROM이 없어 못 하는 ZN: 타이토 ZN (Ray Storm · G Darius · Fighters' Impact = ZSG2, archive 없음)
- **8-8j. 타이토 YM2151 보드 (fullt) — ✅ v2.1.17 (9 / 9, 147곡 모두)** — `Audio/HootTaitoOpm.cs`
  - v2.1.60 타이토 B masterw · viofight (MAME taito_b masterw · viofight_sound_map): 같은 Z80 · TC0140SYT 배치, 9000h = YM2203 3MHz (IRQ = 타이머, 포트 A 0Eh = 뱅크 아래 2비트), viofight B000h · B001h = OKIM6295 1.056MHz 핀7 높음 (ROM = pcm). 처음 EFh (소리 켬)도 같음
  - 배치 (MAME rastan · taito_asuka · topspeed · exzisus): Z80 4MHz, ROM 0000h~3FFFh + 뱅크 4000h~7FFFh (YM2151 1Bh의 CT1 · CT2 = bit6~7 → ROM n × 4000h, 32KB ROM은 뱅크 없음), RAM 8000h~8FFFh, YM2151 9000h · 9001h (타이머 = IRQ), TC0140SYT (PC060HA) A000h 모드 · A001h 통신 — 니블 · NMI는 `HootTaito`와 같음
  - YM2610 드라이버처럼 처음엔 소리 꺼짐 — 명령 EFh = 소리 켬 (Rastan 0368h: EEh → 8F26h = 0, EFh → 1, 곡 시작 02FEh가 8F26h = 0이면 그냥 돌아감). 처리 코드 `FE EF C0 3E`가 있으면 부팅 뒤 EFh (9개 모두)
  - 드라이버가 주 루프에서 1Bh (뱅크)를 쉴 새 없이 씀 (8초에 약 6만 번) → VGM에는 LFO 파형 (bit0~1)이 바뀔 때만
  - MSM5205 (384kHz ÷ 48 = 8kHz): 하드웨어가 VCK마다 ADPCM ROM에서 위 니블 먼저 넣음. VGM에 MSM5205가 없어 같은 4비트 OKI ADPCM인 OKIM6258 (4.096MHz ÷ 512 = 8kHz, 12비트)에 바이트마다 0xB7 쓰기 (니블 바꿔서), 시작 = 명령 2 · 멈춤 = 1
  - Rastan · Asuka 배치: B000h = 주소 위 바이트, C000h = 시작, D000h = 멈춤. 곡에서는 안 씀 (Rastan · Asuka · Mofflott의 ADPCM은 효과음) → 음악은 FM만
  - Top Speed: B000h~ = 칩 1 · C000h~ = 칩 2, 주소 bit10~11: B000h = 주소 위 바이트, B400h = 시작 (약 10ms 뒤), B800h = 멈춤 (쉴 때 틱마다 씀), D000h~D600h = 볼륨 (안 씀). 드럼 = 칩 1 (곡 04h~07h · 09h · 0Ah), 0x09 Accel #1은 MSM만. pcm 둘째 ROM (10000h) = 칩 2, VGM OKIM6258은 하나라 칩 2는 칩 1이 쉴 때만
  - hoot 옵션 opm_mix · pcm_mix (음량 비)는 안 씀. 소리는 귀로 아직 확인 안 함
- **8-8k. 토아플랜 1 · Twin Cobra (toaplan generic) — ✅ v2.1.18 (9 / 9, 125곡 모두)** — `Audio/HootToaplan.cs`
  - hoot 옵션 없음 (rom code 하나뿐). 배치 (MAME toaplan1 · twincobr): Z80 3.5MHz, ROM 0000h~7FFFh, 8000h~87FFh = 68000과 같이 쓰는 RAM, YM3812 3.5MHz (hoot 이름은 YM3526) = I/O 포트, 타이머 1 넘침 = IRQ (IM 1)
  - YM 포트는 게임마다 다름 (fshark · twincobr · demonwld · outzone · gulfwar2 = 00h, rallybik · truxton = 60h, hellfire = 70h, zerowing = A8h — MAME와 같음) → ROM의 `OUT (n),A` 중 n · n + 1이 둘 다 있는 짝수 n에서 가장 많은 것
  - 부팅: 드라이버가 8000h ← FFh, 8001h ← 00h을 쓰고 8001h = AAh (68000)를 기다림 → ROM 합 · RAM 시험 (AAh · 55h) → 주 루프. 0.05초 뒤 8001h ← AAh
  - 명령 = 8000h (FFh = 없음): IRQ에서 읽음 (twincobr 0085h: FFh 그냥, FEh 멈춤, F0h~ 특수, E0h~ 포트 출력, 그 아래 = 곡 · 효과음), 처리하면 FFh로 되돌림. 8002h도 주 루프가 기다리지만 곡 명령 자리가 아님 (처음엔 8002h로 했다가 fshark만 읽고 소리 없음)
  - 드라이버가 IRQ마다 레지스터를 거의 다 다시 씀 (8초에 약 4만9천) → 값이 바뀔 때만 VGM에 (OPL은 같은 값 다시 쓰기가 아무 일 없음) → 8초에 수백
  - 진단 `HOOT_PROBE=1 --test hoottoaplan`: 맞춤 뒤 2초 동안 OUT · IN 포트 횟수, Z80이 읽기만 하는 RAM (68000 쪽 자리 후보)
- **8-8l. 데이터 이스트 6502 보드 — ✅ v2.1.19 (20 / 20)** — `Audio/HootDeco6502.cs`
  - 칩: YM2203 1.5MHz + YM3812 (이름에 YM3526이면 YM3526) 3MHz + OKIM6295 1MHz ÷132 (rom pcm이 있을 때), 6502 1.5MHz. 명령 = 래치 + NMI, IRQ = OPL 타이머 (레벨)
  - 배치 셋 (MAME dec0 · dec8 · karnov) — ROM에서 래치 자리를 LDA abs로 가장 많이 읽는 것으로 고름: dec0 = 래치 3000h, YM2203 0800h, OPL 1000h, OKI 3800h (hbarrel · baddudes · birdtry · robocop · stadhero · hippodrm · actfancr · triothep · lastmisn · shackled), dec8 = 래치 6000h, YM2203 2000h, OPL 4000h (cobracom · garyoret · meikyuh · gondo · ghostb · srdarwin · oscar), karnov = 래치 0800h, YM2203 1000h, OPL 1800h (karnov · wndrplnt · chelnov). 처음 고른 배치로 18개 모두 바로 소리
  - triothep = code offset 0 · 64KB → 4000h~FFFFh를 파일 그대로 (벡터 40xxh)
  - hoot 옵션 decrypt (ghostb · srdarwin · oscar) = DECO222: 8000h 이상의 명령 바이트만 bit5 · bit6 바꿈 → `Cpu6502.OpcodeMap`. 10진 모드는 2A03에 없어 `Cpu6502.DecimalMode`를 새로 (기본 꺼짐, NES · HuC6280 그대로)
  - OPL은 바뀐 레지스터만 VGM에 (토아플랜과 같음). 곡 코드 위 바이트 (extcode, gondo 0x10A 등)는 버림
  - 빠진 것: midres (HuC6280 사운드 CPU), brkthru · darwin · exprraid (6809 사운드 CPU — 스캔하면 벡터가 엉뚱함)
- **midres (v2.1.21, 1 / 1 · 12곡)**: HuC6280 보드인데 YM2151 대신 YM3812. 칩 자리는 ROM이 TAM으로 MPR2에 넣는 쪽 번호 (84h = 108000h YM3812, 8Ch = 118000h YM2203, 98h = 130000h OKI, 9Ch = 138000h 명령)로 확인 — MAME dec0 midres_s_map과 같음. 벡터: IRQ1 = NMI = E111h (명령 처리), IRQ2 = 타이머 = E0CAh (음악 틱, YM3812 타이머 1을 다시 건다) → YM3812 타이머 = IRQ2. 클럭 YM3812 3MHz · YM2203 1.5MHz · OKI 1MHz (MAME 기억). 0x5A (Intermission)는 8초에 OKI 하나 — 짧은 징글일 수 있음
- **8-8m. SNK68 (snk 3812) — ✅ v2.1.22 (4 / 4, 146곡 모두)** — `Audio/HootSnk68.cs`
  - 배치 (MAME snk68): Z80 4MHz, ROM ~EFFFh, RAM F000h~F7FFh, 명령 F800h (NMI), I/O 00h YM3812 주소 · 상태 · 20h 자료 · 40h uPD7759 곡 번호 (START) · 80h uPD7759 /RESET. YM3812 4MHz 타이머 = IRQ (처리기가 상태 60h를 보고 틱)
  - uPD7759 640kHz 마스터 모드 (칩이 ROM을 읽음) → VGM은 슬레이브 모드뿐이라 보드가 DRQ마다 ROM 바이트를 넣는다: FFh (슬레이브 칩은 "마지막 곡 번호"가 10h 이상이어야 계속) · 빈 · 주소 위 (ROM 5 + 2n) · 아래 (6 + 2n) · (위 << 9 | 아래 << 1)부터 차례로 (첫 바이트는 DUMMY2가 버림 = 마스터의 offset++). 곡 번호 쓰기는 VGM 데이터로 내지 않음 (재생기 칩이 쌓아 두므로 한 바이트 밀림). 되풀이 블록 (C0h)은 되감지 않음
  - **음악 켬**: 드라이버 깃발 (P.O.W. F115h, SAR F116h) 처음 0Ch — bit2 = BGM 끔 (곡 처리기 09ACh가 그냥 돌아감), bit3 = 효과음 · 목소리 끔. 명령 형식 표 (ROM, 0 무시 · 1 제어 · 2 BGM · 3 효과음 · 4 …)의 형식 1 중 낮은 니블만 있는 명령 (00h~0Fh)은 **같은 값이 두 번 연달아** 와야 처리 (F12Fh에 앞 값), 처리기는 bit1 = 끔 · bit0 = 켬 (bit2 BGM · bit3 효과음) → 0Dh 두 번 = 둘 다 켬. 35h는 P.O.W. · Ikari III에서만 BGM 켬 (SAR · Street Smart는 형식 표가 달라 그냥 돌아감)
  - 찾은 길: 타이머 쓰기 기록 (부팅 80h · 60h · 21h 뒤 곡 명령에서 r4 = 6Bh = 두 타이머 가림) → 곡 처리기의 깃발 검사 → 깃발 지우는 RES 2 · 그 앞 분기 · 두 번 오기 검사. 진단 `HOOT_SNKSCAN` (부팅 명령 훑기)은 3Ah · BFh처럼 자기 소리를 내는 명령도 걸리니 조심
  - 3Ah = 소리 초기화 (RAM · ROM 시험 뒤 다시 시작), BFh = 짧은 효과 — hoot 목록 맨 앞 줄
- **8-8n. 니치부츠 terracre · terraf — ✅ v2.1.23 (10 / 10)** — `Audio/HootNichibutsu.cs`
  - 배치 (MAME terracre · galivan · armedf가 같은 꼴): Z80 4MHz, ROM (48KB 이하면 ~BFFFh · RAM C000h~, 아니면 ~F7FFh · RAM F800h~ — terraf 계열은 SP 0000h로 FFFFh부터), I/O 00h · 01h OPL (이름에 YM3812면 3812, 아니면 3526, 4MHz), 02h · 03h DAC 둘, 04h 읽기 = 명령 지움, 06h 읽기 = 명령
  - 인터럽트 = 4MHz / 512 = 7812.5Hz 주기 IRQ (HOLD, IM 1 / IM 2) — 음악 틱 (C00Ch를 세어 1Fh마다)과 DAC 출력. OPL 타이머 · 상태는 안 씀
  - 명령: 주 루프가 06h를 읽어 bit0이 1일 때만 받고 오른쪽으로 민다 → 68000이 (코드 << 1) | 1로 쓴다 (처음엔 코드 그대로 넣어 홀수 코드만 소리 났음)
  - DAC 둘 → VGM에 그냥 DAC가 없어 YM2612 DAC (2Bh = 80h, 2Ah = 둘의 평균, 바뀔 때만). 드럼 곡은 8초에 DAC 쓰기 1만~2만 개
- **8-8o. 알파 68k2 (Alpha 68k II · V) — ✅ v2.1.24 (6 / 6, 83곡 모두)** — `Audio/HootAlpha68k.cs`
  - 배치 (MAME alpha68k II sound_map · iomap): Z80 4MHz, ROM 0000h~7FFFh, RAM 8000h~87FFh (SP 87FFh), 뱅크 창 C000h~FFFFh (I/O 0Eh = n → 영역 10000h + n × 4000h, Alpha 68k V 판은 주 루프까지 뱅크 창에서 돎), I/O 00h 명령 읽기 · 쓰기 = 지움, 08h DAC, 0Ah · 0Bh YM2413 (3.58MHz), 0Ch · 0Dh YM2203 (3MHz)
  - hoot 목록은 code 파일을 두 번 (0 · 18000h) 적는다 = MAME ROM_LOAD 32KB + ROM_CONTINUE → 앞 반 · 뒤 반으로 나눔. pcm = 뱅크 영역 소리 ROM
  - 인터럽트 = 7614Hz 주기 **NMI** (0066h: II 판 = 바로 처리기, V 판 = JP 0100h → JP 처리기 — 다시 시작이 아님, 처음에 잘못 읽어 IRQ로 바꿨다가 되돌림). 처리기가 DAC 샘플을 내고 틱을 세어 8004h · 8003h를 세우면 DI인 주 루프가 RST 38h · RST 30h로 음악 틱
  - 명령: 주 루프가 00h를 두 번 읽어 같으면 받는다 (NMI · IRQ 없음 — 처음엔 명령 NMI로 해서 0066h가 틱으로 한 번만 돌고 조용했음)
  - 곡은 모두 YM2413만 (YM2203 · DAC는 목록 곡에서 안 씀 — 효과음 · 목소리일 듯). DAC는 VGM YM2612 DAC로 내도록 해 둠
- **8-8p. UPL robokid — ✅ v2.1.25 (5 / 5)** — `Audio/HootUpl.cs`
  - 배치 (MAME ninjakd2 · psychic5): Z80 5MHz, ROM ~BFFFh, RAM C000h~, 명령 E000h (폴링), I/O 00h · 01h YM2203 #1 · 80h · 81h YM2203 #2 (각 1.5MHz), YM2203 #1 타이머 = IRQ (A = 72 × (1024 − TA), B = 1152 × (256 − TB) 클럭). 다섯 게임이 같은 드라이버 (ROM 스캔: 포트 · E000h · 벡터 같음)
  - VGM 재생기에 듀얼 YM2203을 더함: 머리말 0x44 클럭 bit30 → `Ym2203Clock2`, 명령 A5h = 둘째 칩 (SN76489 · OKIM6295 둘과 같은 방식). 시험 = A5h로만 키온해서 소리
  - ninjakid2 (Ninja-kun 아수라)는 사운드 Z80이 MC-8123 암호 (rom key 파일) — 따로
- **테크모 16 (v2.1.26, 3 / 3)**: `HootOpmBoard.Tecmo16` — ROM ~EFFFh, RAM F000h~FBFFh (SP FC00h), OKI FC00h (1MHz), YM2151 FC04h (4MHz), 명령 FC08h (NMI), FC0Ch = 와치독. NMI 처리기가 명령을 거르는 값 (F5FEh = 곡 표 수 7Fh)은 RAM 시험 뒤 드라이버가 스스로 리셋 (FFh)을 한 번 처리한 다음에야 들어가서, 1초에 보내면 버려짐 → `Layout.BootSeconds` = 3초. 진단할 때 PC 세기를 곡 명령부터 하면 부팅 경로가 안 보인다 (`HOOT_PCR`은 부팅부터)
- **8-8q. 테크모 Ninja Gaiden — ✅ v2.1.27 (4 / 4)** — `Audio/HootGaiden.cs`
  - 배치 (MAME gaiden sound_map, ROM 스캔으로 넷 다 같음): Z80 4MHz, ROM ~DFFFh, RAM F000h~F7FFh (SP F800h), F800h OKIM6295 (1MHz), F810h · F811h YM2203 #1, F820h · F821h YM2203 #2 (각 4MHz), FC00h 와치독, FC20h 명령 (NMI — 처리기가 F000h~ 큐에 쌓음)
  - IRQ = YM2203 #1 타이머만. 처음엔 두 칩의 OR로 했다가 드라이버가 #2의 플래그를 안 지워 IRQ가 끝없이 → 스택이 FFxxh까지 내려가 PC가 85xxh로 (Round 4 · 5 곡이 중간에 멈춤)
  - SSG 레지스터 (00h~0Ch)는 바뀔 때만 VGM에 (틱마다 다시 써서 8초에 6만 개 넘음, 0Dh는 쓰면 엔벌로프가 다시 시작이라 늘)
  - VGM = 듀얼 YM2203 (v2.1.25) + OKIM6295
- **8-8r. 테크모 YM3812 + MSM5205 (rygar · gemini) — ✅ v2.1.28 (4 / 4)** — `Audio/HootTecmoAdpcm.cs`
  - 배치 (MAME tecmo rygar_sound_map · tecmo_sound_map, ROM 스캔으로 확인): Rygar = ROM ~3FFFh (파일 8KB), RAM 4000h (SP 4800h), YM3812 8000h, C000h 명령 읽기 · ADPCM 시작 쓰기, D000h 끝, E000h 볼륨, F000h NMI 확인. Silk Worm · Gemini Wing = ROM ~7FFFh, RAM 8000h (SP 8800h), YM3812 A000h, C000h / C400h / C800h / CC00h. Back Fire = Silk Worm 배치 + RAM 2000h~27FFh (부팅 때 ROM 0103h~의 루틴을 2000h~로 베껴 부름 — 처음엔 쓰기가 막혀 0028h에서 멈춤, hoot 형식이 같아 이름으로 고름)
  - Z80 · YM3812 4MHz, 명령 = NMI, YM3812 타이머 = IRQ
  - MSM5205 400kHz ÷ 48: 하드웨어가 시작 × 100h ~ (끝 + 1) × 100h 앞까지 ADPCM ROM 바이트를 위 니블 먼저 (MAME adpcm_int) → VGM OKIM6258 (4.2667MHz ÷ 512, 12비트), 바이트 니블 바꿔서. 부팅 때 드라이버가 조용한 샘플을 한 번 시작함 (starts 1 · 바이트 0은 이것)
- **8-8s. 캡콤 YM2203 × 2 (opn2 · rushcrsh · tigerroad) — ✅ v2.1.29 (12 / 12)** — `Audio/HootCapcomOpn.cs`
  - 배치 (MAME gng · 1943 · lwings · srumbler · tigeroad sound_map, ROM 스캔으로 확인): ROM ~7FFFh, RAM C000h~C7FFh (SP C800h), 명령 = 래치 읽기 (폴링, IM 1)
  - opn2 = 래치 C800h, YM2203 E000h · E002h, Z80 3MHz · YM 1.5MHz, IRQ 주기 240Hz (Section Z · Legendary Wings · Trojan · Avengers = lwings 222Hz — 이름으로 고름)
  - Avengers: 래치를 E006h (soundlatch2)에 썼다가 다시 읽어 C001h와 비교 → 쓴 값을 돌려주지 않으면 명령을 못 받아 모두 무음 (`Layout.Echo`)
  - rushcrsh (Speed Rumbler) = 래치 E000h, YM2203 8000h · A000h (4MHz), IRQ 240Hz. tigerroad (Tiger Road · F-1 Dream) = 같은 배치, Z80 · YM 3.579545MHz, IRQ = YM2203 #1 타이머. 샘플 CPU (MSM5205) 쪽 포트는 버림
  - 같은 값 다시 쓰기는 VGM에 안 넣음 (드라이버가 IRQ마다 SSG를 모두 다시 써 8초에 4만 5천 → 약 1천), 28h · 0Dh · 27h · A0h~AEh는 늘 넣음
  - 클럭 · IRQ 주기는 MAME 기억 — 귀로 템포 확인 필요
- **8-8t. 아타리 System 1 — ✅ v2.1.30 (4 / 4)** — `Audio/HootAtariSys1.cs`
  - 배치 (MAME atarisy1 sound_map, ROM 스캔으로 확인): 6502 1.79MHz (10진 모드 있음), RAM 0000h~0FFFh, VIA 1000h (TMS5220 — 없음, 읽기 0), YM2151 1800h (3.58MHz, 타이머 = IRQ), 1810h 명령 (메인이 쓰면 NMI) · 응답, 1820h 스위치, 1824h LED, POKEY 1870h (없음, 쓰기만 셈), ROM 4000h~
  - 1820h: bit0~2 동전 (높음 = 안 눌림), bit3 = 명령 있음, bit4 = 응답 안 읽힘 (늘 0 — NMI 처리기가 이게 1이면 기다림), **bit7 = 자가 시험 끔** — 0이면 Indiana Jones가 RAM 시험 + 대기 루프로 부팅이 1초를 넘겨 첫 명령이 큐 대신 자료 받기 길 (0213h = 0)로 빠져 모두 무음
  - YM2151 같은 값 다시 쓰기는 VGM에 안 넣음 (08h · 01h는 늘) — 8초에 3만 → 수백
  - v2.1.31 POKEY (`Chips/Pokey.cs`): 1.79MHz 사이클마다 돌림 (분주기 4, poly 4 · 5 · 9 · 17 LFSR, AUDCTL 64k / 15k · 1.79MHz · 16비트 연결 · 고역 필터, SKCTL 0 = 멈춤), 출력 = 샘플 동안 평균. 다시 채움 = AUDF + 1 / + 4 (1.79MHz) / 16비트 + 7. 소리 레지스터 0~9 · Fh만 VGM에 (같은 값 다시 쓰기 버림, STIMER는 늘), RANDOM (187Ah) 읽기는 LFSR — 효과음 · 칩 시험 49곡이 소리를 냄
  - 남은 것: TMS5220 (음성 96곡, VIA 6522 뒤) — VGM에 TMS5220 명령이 없어 새 칩 + 이 프로그램 안 명령이 필요
- **8-8u. 6809 CPU + 데이터 이스트 6809 (brkthru · exprraid) — ✅ v2.1.32 (3 / 3)** — `Audio/Cpu/Cpu6809.cs`, `Audio/HootDeco6809.cs`
  - 6809: 페이지 1 · 2 (10h) · 3 (11h) 전부 (6309 확장 없음), 색인 포스트바이트 전부 (5비트 · 8 · 16 · A/B/D,R · 자동 증감 · PC 기준 · [간접]), NMI · FIRQ · IRQ · SWI1~3 · CWAI · SYNC, 사이클 = 모토로라 표 근사. 합성 시험: 합 루프 · MUL · DAA · ,X+ 베끼기 · JSR · IRQ/RTI
  - 배치 (MAME brkthru · exprraid sound_map, ROM 스캔으로 확인): 6809 1.5MHz, RAM 0000h~1FFFh, ROM 8000h~, 래치 = NMI, YM3526 타이머 = IRQ. brkthru (Break Thru · Darwin 4078) = YM3526 2000h (3MHz) · 래치 4000h · YM2203 6000h, exprraid = YM2203 2000h · YM3526 4000h (3.58MHz) · 래치 6000h
  - 음악은 YM3526만 (ROM에 YM2203 쓰기가 두 곳뿐)
- **8-8v. 남코 System 1 — ✅ v2.1.33 (22 / 22, FM 710 / 818곡)** — `Audio/HootNamcoSys1.cs`
  - 배치 (MAME namcos1 + 블레이저 · Galaga '88 · Shadowland 디스어셈블): 6809 1.536MHz, 0000h~3FFFh 뱅크 = 사운드 ROM (s0 + s1) 16KB 단위, C000h · C001h에 쓴 값의 bit4~6 (ROM의 뱅크 자가 검사: 뱅크 n의 3FFFh = n × 10h), C000h~FFFFh = s0 첫 16KB, YM2151 4000h, CUS30 5000h~53FFh (+ 거울), 공유 RAM 7000h~77FFh, RAM 8000h~9FFFh, D001h 와치독, E000h IRQ 확인
  - IRQ = 화면 60Hz (E000h까지 걸려 있음), **FIRQ = YM2151 타이머** (FM 드라이버가 FIRQ에서 도는 판: 블레이저 · Pac-Mania · World Stadium · Mappy Maze · Blast Off — 처리기 첫 줄이 14h ← 15h)
  - 부팅 맞춤: 5000h = A6h (5001h = A6h면 자가 시험), 옛 판 (Shadowland · Beraboh Man · Mappy Maze · Blast Off)은 7000h = A6h 다음 7001h = 0
  - 곡 요청 (대부분): 공유 RAM 7100h + 2 × 묶음 = 곡 번호, 다음 바이트 = 명령 (bit5~6 → 시작, 드라이버가 80h로). hoot 코드 = 묶음 << 8 | 곡 (묶음 0 = 음악, 1~ = 효과음 — 묶음 0만 넣었을 땐 549곡). 블레이저만 7100h 한 바이트 (1~84h, 받으면 F0h) + 5240h + 슬롯 = CUS30 효과음 (ROM에 LDB $7100 … CMPB #$85로 고름)
  - v2.1.34 CUS30 (`Chips/NamcoCus30.cs`, MAME namco_audio namcos1 모드): 파형 RAM 000h~0FFh (4비트 둘씩 위 먼저, 32칸 × 16), 레지스터 100h + 목소리 × 8 (+0 왼쪽 음량, +1 파형 · 주파수 위, +2 · +3 주파수, +4 오른쪽 음량 · bit7 = 다음 목소리 노이즈), 음높이 = 주파수 × 24kHz / 2^20, 노이즈 = 17비트 LFSR (주파수 아래 바이트 × 187.5Hz). VGM 안 명령 D8h aa bb dd (NSF의 D7h처럼 렌더가 `VgmSong.Cus30Clock`을 세움 — VGM 파일로 내보내면 다른 플레이어에선 그 파트 없음). 바뀐 쓰기만 기록
  - 코드 1xxh: 게임마다 FM 묶음 1 (Shadowland · Galaga '88 · Rompers …) 또는 CUS30 효과음 슬롯 5240h + n (Blazer · World Stadium · Mappy Maze · Blast Off · Credit 하나뿐인 게임) — `Start`가 FM 묶음으로 1.5초 돌려 보고 소리 (키온 · CUS30 음량)가 없으면 다시 부팅해 슬롯으로
- **8-8w. 남코 매피 계열 (wsg6809) — ✅ v2.1.35 (11 / 11)** — `Audio/HootNamcoWsg6809.cs`
  - 기계 (MAME mappy · gaplus · toypop 사운드 CPU + 매피 디스어셈블): 6809 1.536MHz, 0000h~03FFh = 15XX와 나누는 RAM (00h~3Fh = 레지스터), 래치 2000h~, ROM E000h~ (Super Pac-Man · Pac & Pal은 F000h 4KB). 요청 = 0040h + 소리 번호에 1
  - 15XX = `NamcoCus30(is15xx)`: 목소리 × 8바이트 (+3 음량, +4 · +5 · +6 주파수, +6 bit4~6 파형), 파형 PROM (rom pcm 256바이트, 아래 4비트, VGM D8h 200h~로), 클럭 24kHz, 모노
  - 메인 CPU 몫 흉내 (게임마다, hoot seq_type): MOTO (Grobda · Motos) = 0040h "CK" → 사운드 CPU가 0042h "OK" → 0040h "GO", GAPL (Gaplus) = 0040h 11h, 0041h에 0 아닌 값을 쓰면 0040h · 0041h 지움 (Phozon · Pac & Pal · Super Pac-Man), SUPE (Super Pac-Man) = IRQ 벡터가 리셋이라 IRQ 대신 00FBh를 화면마다 올림
  - IRQ = 화면 60Hz, 처음부터 켜짐 · 받으면 내려감 (Toy Pop은 처리기가 확인을 안 써 끝없이 걸렸음), irq_enable_addr 옵션이 있으면 화면마다 다시 켬 (메인이 켜는 자리), 없으면 2001h · 4000h 쓰기가 켬 (Libble Rabble · Toy Pop = 4000h)
- **8-8x. 6801 계열 CPU + 남코 System 86 — ✅ v2.1.36 (5 / 5, 71곡)** — `Audio/Cpu/Cpu6801.cs`, `Audio/HootNamcoSys86.cs`
  - CPU: 6800 + 6801 (LSRD · ASLD · PSHX · PULX · ABX · MUL · LDD · STD · ADDD · SUBD) + 6301 (XGDX · SLP · AIM · OIM · EIM · TIM), 내부 레지스터 00h~1Fh (포트 1~4 방향 · 자료 → 버스 PortRead/PortWrite, TCSR · FRC · OCR, OCF · TOF 인터럽트), 벡터 FFFEh~FFEEh. 합성 시험: MUL · PSHX/PULX · OIM · JSR, OCF 65536 E 사이클
  - System 86 사운드 = MCU (HD63701, E 1.536MHz): 내부 ROM cus60 (F000h~, 남코 MCU 공용 OS — [8000h] = A6h면 외부 ROM 진입점 [8001h], 벡터는 [00AEh] 표로 넘김), 외부 ROM 4000h~BFFFh, CUS30 1000h~13FFh, RAM 1400h~1FFFh, YM2151 = 옵션 opmaddr (없으면 ROM의 STA 확장 쓰기 짝으로 찾음 — Genpei 2800h). IRQ = 화면, OCF = 작업 전환 (OCR = 0100h)
  - 요청: 1380h에 곡 번호 (받으면 FFh). Return of Ishtar는 hoot가 롬을 0000h에도 올림 → 2000h~3FFFh를 롬으로 (곡 6개가 그 자리 자료를 읽음)
  - 남은 것: 63701X 음성 (rom pcm, Genpei · Rolling Thunder · Wonder Momo의 북 · 목소리)
- **8-8y. 남코 Pac-Land 계열 (wsg63701) — ✅ v2.1.37 (6 / 6, 157곡)** — `HootNamcoSys86` (`Slots`)
  - 같은 MCU · OS (cus60 / Sky Kid = cus63), 외부 ROM 8000h~, RAM C000h~C7FFh, YM2151 없음 (Hopping Mappy만 System 86 보드 — YM2151 2000h를 색인으로 써서 늘 2000h에 둠, 다른 게임은 2000h에 와치독만)
  - 요청 = 슬롯: OS의 기준 [00BDh] (= 1285h) + 코드에 1 (슬롯 목록 = [00AEh]+46h 표, 종류 = +42h 표 — System 1의 5240h 방식과 같음). [BD+41h] ≠ 0이면 데모 목록 (동전 소리만): Baraduke는 [BD+41h] = ~[118Ch] & 1을 화면마다 다시 세워서 118Ch bit0 (메인 CPU의 "게임 중")을 세움
  - CPU: HD6301 문서 밖 12h · 13h (ASX1 · ASX2: X += [S+1]) — OS가 PSHA 다음 표 찾기에 씀, 없으면 TRAP으로 무너짐
- **8-8z. 아이렘 M62 / M52 — ✅ v2.1.38 (15 / 15, 137 / 145)** — `Audio/HootIremM62.cs`
  - MAME irem_audio: M6803 (3.58MHz ÷ 4), AY × 2 (0.89MHz)는 포트로 — 포트 1 = 자료, 포트 2 bit0 1→0 = 스트로브, bit2 = 주소/자료, bit3 · bit4 = AY #1 · #2. 명령 = AY #1 포트 A (레지스터 14) 읽기, 메인이 쓰면 IRQ, 확인 = M62 0800h (거울) · M52 1000h~ · 10-Yard 2000h
  - **NMI = MSM5205 VCK** (AY #1 포트 B bit2~3: S96 4kHz · S48 8kHz · S64 6kHz · 슬레이브 = 없음): 드라이버가 AY 쓰기 박자를 NMI로 맞춤 (없으면 <$BD를 기다리며 멈춤). 첫 NMI는 한 주기 뒤 (리셋 직후 걸면 스택 전이라 무너짐)
  - MSM5205 #1 (자료 = M62 0801h / M52 홀수 0000h~, 리셋 = 포트 B bit0): VCK마다 받은 니블 → VGM OKIM6258 (클럭 = 속도 × 1024, ÷1024, 니블 둘 = 한 바이트) — Moon Patrol 효과음 · Traverse USA Honk
  - VGM 듀얼 AY: `VgmSong.AyClock2` (74h bit30), 명령 A0h aa bit7 → 둘째 `PsgEmulator`
- **8-9a. 코나미 Z80 + AY × 2 — ✅ v2.1.39 (10 / 10, 75 / 116)** — `Audio/HootKonamiAy.cs`
  - MAME galaxian konami_sound · timeplt_audio: Z80 · AY 1.79MHz, 명령 = AY #1 포트 A, 포트 B = 코나미 타이머 표 [(사이클 ÷ 512) mod 10], 메인이 IRQ (hold)
  - scramble: RAM 8000h~ (쓰기 9000h~ = 필터), I/O bit6/bit7 = AY #1 주소/자료, bit4/bit5 = AY #2 (처음엔 반대로 넣어 명령을 못 읽었음). timeplt: 메모리 4000h~7000h, RAM 3000h — Jungler는 locomotn 배치 (RAM 2000h, 3000h = 필터: ROM이 가리키는 RAM 쪽으로 고름)
  - 무음 41: Scramble · Amidar · Strategy X · Turtles · Jungler의 "ch2 · ch3" 파트와 몇 BGM — 명령은 받고 빈 슬롯에 넣지만 AY 쓰기가 없음 (앞 파트와 함께여야 하는 듯, 먼저 보내 본 실험은 결론 없음)
  - hoot 롬 이름 불일치 (amidar.5c ↔ zip s1.5c, 11-7a.bin ↔ s1.7a): `FindFile`이 못 찾으면 기판 위치 코드 (숫자 + 글자 2~3자)가 하나로 맞는 파일
- **8-9b. 타이토 SJ — ✅ v2.1.40 (12 / 12, 285곡)** — `Audio/HootTaitoSj.cs`
  - MAME taitosj + Elevator Action 디스어셈블: Z80 3MHz, ROM ~3FFFh, RAM 4000h~43FFh, AY × 3 (1.5MHz) 4800h · 4802h · 4804h (LD (nn),HL = 주소 + 자료 한 번에), 명령 5000h = NMI, 5001h 읽기 = 깃발 (bit3 = 명령 있음 — 많은 판이 이걸 보고 받음, 5001h 쓰기로 지움), IRQ 주기 36.6Hz, E000h 진단 ROM = 없음
  - DAC = AY #1 포트 A (볼륨 = 포트 B 반전) → VGM YM2612 DAC (Bio Attack 등)
  - VGM 셋째 AY: `VgmSong.AyClock3` + 이 프로그램 안 명령 D9h rr vv 00 (VGM 파일 밖에선 그 칩 없음)
- **8-9c. 남코 System 2 — 일부 v2.1.41 (FM 344 / 870곡)** — `Audio/HootNamcoSys2.cs`, `Audio/Chips/NamcoC140.cs`
  - 기계 (MAME namcos2 + Metal Hawk · Burning Force 디스어셈블): 6809 2.048MHz, 뱅크 0000h~ = ROM n × 4000h (C000h · C001h 값 >> 4, 롬보다 크면 감쌈 — MAME식 C000h/10000h 어긋남은 모두 무음이 되어 버림), YM2151 4000h, C140 5000h~ (200h 거울), 공유 RAM 7000h, RAM 8000h, ROM 첫 16KB = C000h
  - FIRQ = C140 타이머 (1FEh bit0, 주기 1F8h × 2 / 21.3kHz, 1FAh 확인), IRQ 120Hz (처리기는 RTI만). 부팅 맞춤 703Ah = A6h · 703Bh = 0, 뱅크 8 "KOBA"/"KOMO" 확인 실패 (77FEh = FFh)면 메인이 77FDh에 A6h → 6Ah로 답함 (Burning Force · Marvel Land · Golly Ghost · Bubble Trouble)
  - 요청: 옵션 codeaddr (7100h · 7110h) + 2 × 묶음 = 곡, 다음 = 명령 40h (System 1과 같음, ack = (cmd & 3Fh) | 80h)
  - C140 칩 (MAME c140 고전 구현): 목소리 24, 모드 bit7 키온 · bit4 반복 · bit3 압축 ((s>>3)<<(s&7) ± 표), 선형 = 8비트 << 4, System 2 주소 ((bank<<16 + a) & 200000h) >> 2 | & 7FFFFh, VGM A8h · 96h (형식) · D4h · 블록 8Dh. 합성 시험은 통과
  - v2.1.42: **C140 효과음 · 음성은 따로 묶음 7200h~720Fh** (FIRQ의 D7F9h가 훑음 — FM 7100h와 같은 (곡, 명령 40h) 꼴, C140 채널 키온은 상태 6,Y = 40h · 8,Y = 0일 때 D9EAh → DC11h). hoot 코드에 구분이 없어 FM으로 1.5초 돌려 보고 소리가 없으면 다시 부팅해 7200h로 (`Start`) → 무음 526 → 43
  - C140 출력 = 8 × 합 (MAME 고전 c140), 그래도 FM보다 작을 수 있음 — 귀로 확인
  - 남은 것: Four Trax 전부 (hoot 옵션 foutrax — 요청 방식 다른 듯), Final Lap 9 · Rolling Thunder 2 4
- **8-9d. 코나미 K053260 — ✅ v2.1.43 (11 / 11, 606 / 616)** — `Audio/HootKonami53260.cs`, `Audio/Chips/K053260.cs`
  - 배치는 hoot 옵션 (ram_addr · ram_size · opm_addr · pcm_addr · opm_mirror · bank_addr), Z80 · YM2151 · K053260 3.58MHz
  - 명령 = K053260 포트 0 · 1 (pcm_addr + 0 · 1 읽기, 코드 위 바이트 = 포트 1) + IRQ (hold). 박자 = YM2151 타이머 상태 폴링 또는 NMI: K053260이 FC00h면 FA00h (FA00h면 FC00h)에 쓰면 무장 → SH1 끝 (64클럭 뒤) NMI 한 번 (MAME z80_arm_nmi_w, 처리기가 다시 무장) — Vendetta · Escape Kids · Punk Shot · Simpsons · Thunder Cross II
  - Parodius · Vendetta: 부팅 때 K053260 ROM 읽기 포트 (2Eh, 목소리 0 시작부터 하나씩)로 PCM 롬 전체 검사 → 부팅을 IRQ가 켜질 때까지 (최대 8초) 돌림. Thunder Cross II: opm_mirror 0011h (YM2151 F810h · F811h)
  - K053260 칩 (MAME): 4목소리, 클럭/64마다 카운터 += 64 · 1000h 넘으면 − 1000h + 피치, 8비트 부호 PCM 또는 KADPCM (아래 니블 먼저, 표 0 1 2 4 8 16 32 64 −128 −64 …), 반복, 팬 3비트 (cos/sin 근사), 2Fh bit1 = 소리 켬
- **8-9e. 코나미 K054539 (Z80 판) — ✅ v2.1.44 (10 / 10, 266 / 267)** — `Audio/HootKonami54539.cs`, `Audio/Chips/K054539.cs`
  - MAME xexex · xmen · mystwarr: Z80 8MHz, ROM 0000h~7FFFh, 뱅크 8000h~BFFFh (F800h 아래 4비트 × 16KB), RAM C000h~DFFFh, K054539 #1 E000h~E22Fh · #2 E400h~E62Fh (사이는 RAM), YM2151 E800h (EC00h 거울), K054321 F000h~F003h (F002h = 명령, F003h = 음량)
  - v2.1.53 lethal (MAME lethal · gijoe 사운드 맵): Z80 8MHz, 0000h~EFFFh ROM, F000h~F7FFh RAM, F800h~FA2Fh K054539 (타이머 = NMI), FC02h = 명령 (IRQ hold)
  - IRQ = 명령 (K054321 — 메인이 쓰면, hold; Xexex는 EI … DI 창에서만 받고 박자는 YM2151 타이머 B 상태를 폴링) 또는 YM2151 타이머 (Bucky · Moo Mesa · X-Men), NMI = K054539 #1 타이머 (227h: 38 × (255 − 값) / 18.432MHz마다 바뀜, 22Fh bit5 켬 — 054539x2 판)
  - K054539 칩 (MAME): 채널 8, 피치 24비트 16.16 걸음, 8비트 (끝 80h) · 16비트 (끝 8000h) · 4비트 DPCM (끝 88h, 표 0 1 4 9 16 25 36 49 −64 …), 반복 (200h+ch×2 +1 bit0), 역재생 (+0 bit5), 감쇠 −36dB/40h, 팬 √, 22Ch = 재생 비트, 22Dh = ROM 읽기 포트, 22Fh bit0 = 켬, 48kHz. 잔향 없음. 같은 값 다시 쓰기 버림
- **8-9g. 코나미 System GX — ✅ v2.1.47 (17 / 17, 489곡)** — `Audio/HootKonamiGx.cs` (스크래치 68000 디스어셈블러로 확인)
  - 68000 8MHz, 사운드 롬 두 파일 바이트 엇갈림 (짝수 = 첫 파일 → SSP 00103E00h · PC 1400h), RAM 100000h~10FFFFh
  - **K054539 둘 = 200000h~2004FFh** (레지스터 = (주소 − 200000h) ÷ 2, 짝수 바이트 = #1 · 홀수 = #2 — 처음 MAME 새 맵을 기억해 180000h로 넣어 칩 쓰기가 하나도 안 보였음), TMS57002 = 300000h · 500000h (없음, 상태 500001h = 07h)
  - K056800 400000h~: 사운드 쪽 읽기 400011h~17h (오프셋 8~11) = 메인 레지스터 0~3, IRQ 1 처리기가 네 바이트를 큐 (103C80h~)에. 레지스터 0 = 소리 번호, 1 = 묶음 (아래 3비트) → hoot 코드 = 묶음 << 8 | 번호
  - IRQ 2 = K054539 #1 타이머 (227h = 6Dh, 22Fh = 21h → 올라가는 끝마다), 처리기가 틱을 셈 (2틱마다 음악 한 걸음)
- **8-9f. 세가 System 18 — ✅ v2.1.45 (6 / 6, 246 / 251)** — `Audio/HootSegaSystem18.cs`, `Audio/Chips/Rf5c68.cs`
  - MAME segas18: Z80 8MHz, ROM 0~9FFFh, 뱅크 A000h~BFFFh = PCM 롬 8KB 단위 (포트 A0h bit6~7 = 롬 번호, 아래 6비트 = 뱅크 — hoot pcm 파일 차례대로), RF5C68 레지스터 C000h~ · 메모리 창 D000h~DFFFh (뱅크 = 레지스터 7), RAM E000h~, YM3438 #1 · #2 = 포트 80h · 90h (8MHz), 명령 = 포트 C0h + NMI
  - RF5C68 (MAME): 8채널, 부호-크기 8비트, FFh = 반복 표시, 걸음 11비트 소수, 팬 아래 니블 = 왼쪽, 클럭 10MHz ÷ 384. VGM: 요청 때 RAM 그림을 블록 C0h로, 그 뒤 메모리 쓰기는 C1h (바뀐 바이트만)
  - VGM 둘째 YM2612 (`Ym2612Clock2`, A2h · A3h). Shadow Dancer의 xml 이름 "mpr-12715.b4b4" → `FindFile` 앞부분 맞춤
  - v2.1.46 System 32 (MAME segas32): Z80 · YM3438 8.05MHz, RF5C68 12.5MHz, 명령 = 공유 RAM E000h~E008h 큐 (빈 칸 80h 또는 00h — 판마다, 드라이버가 한 틱에 하나씩 밀어냄), 박자 = YM 타이머 A 폴링 (인터럽트 안 씀), 뱅크 = 포트 A0h (아래 6비트) · B0h (bit2 → 6, bit0~1 → 7~8) → 사운드 영역 100000h + 뱅크 × 2000h (100000h~ = 코드 롬 거울, 200000h + hoot 오프셋 = PCM 롬, 1MB 칸 거울)
- **8-9h. 비디오시스템 · 사이쿄 Z80 + YM2610 — ✅ v2.1.49 (9 / 9, 169곡)** — `Audio/HootYm2610Board.cs`
  - v2.1.65 welltris (MAME welltris: YM 08h, 뱅크 00h — ROM 처음부터, 명령 10h, RAM 7800h) · fromanc2 (MAME fromanc2: Z80 8MHz, ROM ~DFFFh, RAM E000h~FFFFh, 명령 00h, YM 08h, rom adpcm 하나를 ADPCM-A · B가 같이). f1gp는 아직 (f1gp 11곡 중 6 무음, f1gp2는 IRQ가 쉬지 않음 — 타이머 A 확인 필요)
  - 같은 드라이버 계열 (NMI 처리기가 래치를 7801h 링에, IRQ = YM2610 타이머), 포트만 다름: turbofrc = YM 18h · 래치 14h · 뱅크 00h (값 & 3, **영역 10000h부터** — 처음 0부터로 해 Spinal Breakers · Power Spikes 곡 절반이 무음), aerofgt = YM 00h · 뱅크 04h · 래치 0Ch, gunbird = YM 04h · 뱅크 00h ((값 >> 4) & 3, 창은 8200h · 뱅크의 200h부터) · 래치 08h · RAM 8000h
- **8-9i. 코나미 Z80 + YM2151 + K007232 — ✅ v2.1.50 (6 / 6, 84 / 89곡)** — `Audio/HootKonamiK007232.cs`, 새 칩 `Audio/Chips/K007232.cs`
  - MAME thunderx scontra_sound_map: Z80 3.58MHz, RAM 8000h, A000h 래치 (IRQ hold), B000h~B00Dh K007232 (12 = 바깥 포트 → 음량 위 니블 채널 0 · 아래 채널 1), C000h · C001h YM2151 (타이머 폴링), F000h 뱅크 (채널마다 2비트, 128KB 단위)
  - K007232 (MAME 옛 구현): 2채널, 피치 9비트 → 걸음 32 / (512 − f) (클럭 ÷ 128), 표본 bit7 = 끝, reg 13 = 반복. 음량 · 뱅크는 보드 바깥이라 가짜 레지스터 10h~15h
  - Block Hole은 E00Ch에 쓰지만 MAME에도 K007232가 없음 → YM2151만
  - v2.1.56 battlantis (MAME battlnts): K007232 없음, A000h · C000h YM3812 × 2 3.58MHz (박자 = #1 상태 폴링), E000h 명령. VGM 재생기에 둘째 YM3812가 없어 YMF262 (14.32MHz, NEW = 1)로 옮김 — #2의 리듬 (BDh)은 버림
  - v2.1.52 twin16 (MAME twin16): scontra 배치 + RAM ~8FFFh, 9000h bit1 = uPD7759 /RESET, D000h = 번호, E000h bit0 = START, F000h 읽기 = 쉬는 중 (1). uPD7759 마스터 모드 (MAME): 시작 때 번호 = 데이터 포트, ROM[0] = 마지막 번호, ROM[5 + 2n] · [6 + 2n] = 주소 (× 512 · × 2), 빈 바이트 뒤 블록, C0h 되풀이 = 자리로 돌아감. 음악은 uPD를 안 씀 (목소리 효과음)
  - v2.1.51 배치 표 (`Layouts`): salamander (MAME nemesis salamand — RAM 8000h~8FFFh, E000h 읽기 = VLM5030 바쁨 → 0을 돌려줘야 부팅 · D000h · F000h VLM 빼고, 뱅크 없음; K007232는 효과음 채널 8 · 9만 — 음악은 FM만), hcastle (9800h SCC 3.58 / 2MHz → VGM D2h, A000h YM3812 — 타이머 IRQ = NMI, C000h 뱅크, D000h 명령), crimfght · aliens (A000h YM2151, C000h 명령, E000h K007232, 음량 아래 니블 = 채널 0, 뱅크 = YM2151 1Bh CT 핀 bit7 · bit6)
- **8-9j. 코나미 Z80 + YM2203 — ✅ v2.1.55 (2 / 2, 40곡)** — `Audio/HootKonamiIronHorse.cs`
  - MAME ironhors · scotrsht: Z80 3.072MHz, ROM ~3FFFh, RAM 4000h, 8000h 명령 (IRQ hold), I/O 00h · 01h YM2203 3.072MHz. 박자 = 본 고리가 YM2203 상태 bit1 (타이머 B)을 폴링
- **8-9k. SNK Z80 + OPL × 2 — ✅ v2.1.57 (8 / 8, 235 / 312곡)** — `Audio/HootSnkOpl.cs`
  - MAME snk.cpp YM3526_YM3526 · YM3526_Y8950 사운드 맵: Z80 4MHz, ROM ~BFFFh, RAM C000h, E000h 명령, E800h · EC00h YM3526 #1, F000h · F400h YM #2, F800h 상태 (bit0 · 1 = YM IRQ, bit2 바쁨, bit3 명령 IRQ, 쓰기 bit4~7 = 0이면 지움), IRQ 줄 = 상태 & 0Bh (레벨)
  - 드라이버는 켤 때 받는 설정 명령 0Dh (00h~0Fh: bit0 켬 · bit1 끔, bit2 음악, bit3 효과음) 전에는 곡 명령을 버림 (C100h bit2 · 3) → 부팅 뒤 0Dh를 먼저. 박자 = YM #1 타이머 2 (4h bit5 = 가림을 곡 시작 때 풂)
  - v2.1.58 fitegolf (MAME YM3812_sound_map): 같은 배치에 YM #2 없음, YM3812 4MHz → VGM YM3812 그대로 (01h 파형 허락 남김)
  - OPL 둘 → YMF262 (16MHz) 뱅크 0 · 1 (리듬 쓰는 칩이 뱅크 0), 파형 0, C0h 왼 · 오
  - v2.1.59 Y8950 ADPCM (MAME ymdeltat): 07h 제어 (bit7 시작 · bit4 되풀이 · bit0 리셋), 09h~0Ch 시작 · 끝 × 32바이트, 10h · 11h 델타-N (속도 = 4MHz ÷ 72 × N ÷ 65536), 12h 음량 → 칩 `Y8950Adpcm`, 이 프로그램 안 명령 DCh rr vv 00, ROM = rom pcm. Victory Road 목소리 D2h~DBh는 아직 무음 (리셋 줄 뒤 항목 — 앞 명령이 필요할 수 있음)
- **8-9l. 타이토 Bubble Bobble — ✅ v2.1.61 (2 / 2, 26 / 28곡)** — `Audio/HootTaitoBubble.cs`
  - MAME bublbobl sound_map: Z80 3MHz, RAM 8000h~8FFFh, 9000h YM2203 · A000h YM3526 (둘 다 3MHz, 타이머 = IRQ), B000h 명령 (대기 → NMI, B001h 켬 · B002h 끔). Tokio는 hoot가 고친 ROM이라 같은 배치 (YM3526 없음, 9800h 읽기 = 응답 래치 상태 → 0)
  - 부팅 때 YM2203 SSG 7h에 3Fh를 써서 되읽음 (9001h) — 다르면 RST 0으로 다시 부팅. 소리 켬 = 명령 EFh (8FAAh 깃발, EEh = 끔) — 부팅 뒤 먼저 보냄
- **8-9m. 세이부 사운드 시스템 — ✅ v2.1.62 (4 / 4, 61 / 68곡)** — `Audio/HootSeibu.cs`
  - MAME seibusound.cpp: Z80 3.58MHz, ROM 0000h~7FFFh (hoot ROM은 SEI80BU 암호를 푼 것) + 8000h~FFFFh = 뒤 32KB, RAM 2000h~27FFh, 4008h · 4009h YM3812, 4010h · 4011h 명령 낱말, 4012h 답 대기 (0), 6000h OKIM6295 1MHz 핀7 높음
  - 인터럽트 IM 0: YM3812 IRQ = RST 10h, 명령 = RST 18h (4003h = 받음). 명령 낱말 4011h = 동작 (80h 곡 시작 · 81h 멈춤 · 84h · 90h 리셋 …), 4010h = 번호 (0이면 버림) — hoot 코드는 번호
  - v2.1.63 raiden2 (MAME raiden2): 4008h · 4009h = YM2151 3.58MHz (상태는 4009h로 읽음, 타이머 IRQ = RST 10h), 6000h · 6002h = OKIM6295 × 2 (1.023MHz 핀7 높음, ROM = pcm 자리 0 · 나머지)
  - YM3812 IRQ 받기: 드라이버가 4h에 42h를 써서 (T1 가림 + 깃발 지움) IRQ를 내림 — MAME fmopl처럼 4h bit6 · 5 = 그 깃발 지움 + 남은 게 다 가려지면 IRQ 내림 (bit7 리셋만 따르면 IRQ가 쉬지 않음)
- **8-9n. 윤성 Z80 + YM3812 + OKIM6295 — ✅ v2.1.64 (6 / 6, 64곡)** — `Audio/HootYunsung.cs`
  - MAME nmg5.cpp: Z80 4MHz, ROM ~DFFFh, RAM E000h (pclubys 배치는 F800h~FFFFh, 스택 0000h → FFFFh), I/O 00h OKI 뱅크 · 10h · 11h YM3812 · 18h 명령 (NMI) · 1Ch OKIM6295 1MHz. IRQ = YM3812 (fmopl 깃발 지움)
- 다음: 타이토 F3 (ES5505 + MC68681, 36항목), 아이렘 M92 (V35 + GA20), 코나미 K007232 보드 (매피 계열, 6809 + 남코 WSG 15xx) · system86 (HD63701)
- 그 다음 (8-8v): Z80 + YM2151 + OKIM6295 작은 보드 (tecmo16 · raizing mahou · bgaregga · banpresto dbz · dooyong …) — MAME 배치를 하나씩 확인해 `HootOpmBoard.Layout`에, atari system1 (6502), 그 뒤 새 칩
  - 남은 것 (8-8a): "소리 남" 기준이 처음 6초에 키온 하나라 몇 항목은 ADPCM 하나뿐 (driftout · liquidk · megablst 등 첫 곡이 짧은 효과음일 수 있음) — 귀로 확인, bshark (code0 · code1 둘), taito z 계열 다른 보드

### 남은 문제 (단계와 따로)
- PC-98 FM 무음 (300 zip 표본): vector-not-hooked 25 (98stdium · g3: 0으로 찬 메모리로 뛰어감), writes-no-keyon 24 (FUGA fgplay_h · MAKO · ARTDI_98이 hoot 인터럽트를 끔), unknown-op 9 (386 접두어 66h · 65h), no-chip-writes 8, koroshi 같은 이름 불일치 (xml = file01.MUS, zip = 일본어 원래 이름)
- PC-98 MIDI 무음 (드라이버별): DOFMDX98 50 · FUGA 50 · ARTDI 32 · FMX 16 · MINT/NLP 16 · MFD 12 · valky 11, RS-MIDI (내장 8251) 없음
- PC/AT: 무음 6 (rakuen · kurodan · eshoutei · darksun · globaleffect · nestory2), SB16 PCM (DSP DMA) 없음, Game Blaster 8 · Tandy 4 (칩은 있음: SAA1099 · SN76489), CPU 기준 클럭 (1.193MHz × clockmul로 추정) 확인
- 공통: 이동(seek) = 기계를 처음부터 그만큼 돌림 (오디오 스레드에서 기다림) → 상태 저장 · 되돌리기, 곡 길이 3분 고정 → 끝 · 되풀이 찾기, 86 PCM · OPNA ADPCM, fm_mix · ssg_mix 옵션, 트리 장비 이름 정리 (PC-98 / PC-9801 …), zip은 있지만 .kss가 없는 KSS 71항목을 트리에서 미리 빨강으로
