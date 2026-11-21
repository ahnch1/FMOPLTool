# hoot 원본 소스 대조 · 무음 항목 정리 작업 노트 (v2.3.0 ~ v2.3.28)

> 2026-11-21 작업 기록. 다음 작업 때 이 파일부터 읽는다. 판별 상세는 `docs/hoot-status.md` 2.3.x 줄, 할 일 체크박스는 `docs/todo.md`.

## 1. 출발점

- 사용자가 hoot 2001년판 소스를 `_incoming/hoot code/hootsrc20011006` (Shift-JIS)에 넣고 "모호한 부분을 이 소스로 해결, 아니면 이 소스의 루틴을 우선" 요청.
- 2001년판에 있는 것: Z80 기종 드라이버 (PC-88 mucom88 · oldfalcom · hydlide3 · xtalsoft · ed2 · snatcher …, X1 mucomx1, MSX scc · kss · ds4, X68000 x68k · mxdrv, HES), 공통 칩 래퍼 (`ssFMTimer` · `ssYM2203/2608/2151` · `ssAY8910` · `ss051649` …).
- **없는 것**: PC-98 x86 드라이버 전부, 뒤에 생긴 generic 규약 (pc98vx 7D0h, x68k E00010h, pc88/x1/msx generic 포트). 2001년 쪽이 더 낡은 부분은 따르지 않음 (아래 3절).
- 다음 판 hoot.exe의 옵션 설명 문자열은 `_incoming/Hootarchive/hoot.exe`에서 뽑아 쓸 수 있다 (예: `baseclock` = "Z80 execute base clock (MHz)", `use_vrtc` = "use VSYNC interrupt 1:15KHz 2:24KHz", X1 `data` = "I/O port memory").

## 2. 판별 요약

| 판 | 내용 | 결과 |
|---|---|---|
| 2.3.0 | PC-98 OPN 타이머 = hoot 규칙 (깃발은 LOAD만, IRQ는 ENABLE일 때 넘칠 때마다), 8259 종 요청 보존, VA 사운드 보드 II | PC-98 표본 283 → 285 |
| 2.3.1 | X1 `data` = I/O 메모리, 나머지 포트 되읽기, CTC 카운터 TC 없이 256 | X1 143 → 148 |
| 2.3.2 | X68000 24비트 버스, DMA SCR = 주소 증가, PPI 처음 값, zip 안 ZMSC.X | X68000 499 → 510 |
| 2.3.3 | PC-88 타이머 규칙 · SINTM (32h/AAh) · 사운드 보드 II A8h~ADh | PC-88 487 → 499 |
| 2.3.4 | MSX BIOS 영역 = RET · 빠진 진입점 · JIFFY · PSG 되읽기 · SCC 미러 | MSX 481 → 494 |
| 2.3.5 | PC-88 `baseclock` (기본 8MHz) | 499 → 503 |
| 2.3.6 | PC-88 옛 팔콤 (oldfalcom.cpp 이식: xanadu · xanadu2 · asteka2 · romancia), 8801-11 · generic | 509 / 533 |
| 2.3.7 | MSX ds4 (ds4.cpp 이식, Legacy of the Wizard) | 58 / 58 |
| 2.3.8 | KSS: BIOS = RET, PSG 되읽기, `--kssurvey` | 314 / 330 그대로 |
| 2.3.9 | MSX SCC+ 카트리지 RAM 따로 (kgc3) | 495 / 498 |
| 2.3.10 | X1 · PC-88 · MSX Z80을 레지스터 0으로 시작 (RAZE) | X1 150, ASTEKA II 7 / 8 |
| 2.3.11 | SCC VGM 클럭 = 1.789772MHz (÷16) | K007232 보드 SCC 옥타브 고침 |
| 2.3.12 | HES VBlank IRQ1, PC-98 OPN FM 레지스터 읽기 = 0 | — |
| 2.3.13 | X68000 mxdrv → MDX 재생기 | sz2w · kowin3 |
| 2.3.14 | archive="a,b" 항목 · MSX fmbios, 푼 zip 표시 62 | MSX 524 / 530 |
| 2.3.15 | PC-88 곡 덩어리 위치 = 곡 코드 8~23비트 (KOEI · Snatcher) | 517 / 535 |
| 2.3.16 | PC-98 `dummysndrom` = 메모리 스위치 4 bit3 | majoriko |
| 2.3.17 | PC-98 파일 이름 맞추기 (폴더 없는 묶기 · fileNN 별칭) | 289 / 303 |
| 2.3.18 | DOS 시작 DI = 0 | 291 |
| 2.3.19 | PC-98 8259 주 PIC SFNM | 293 |
| 2.3.20 | DOS 핸들 256, PC-98 조사 sound-later | 295 / 303 |
| 2.3.21 | MSX FM-PAC 부팅 상태 (SLTWRK · 슬롯 0 확장) | 526 / 530 |
| 2.3.22 | MSX 시스템 타이머 E6h · E7h | 527 / 530 |
| 2.3.23 | PC-88 RTC · VRTC는 옵션으로만 (E6h 무시) | 522 / 535 |
| 2.3.24 | 타이토 ringrage · ridingf · gseeker → F3 보드 | 43 / 46곡 |
| 2.3.25 | 타이토 rainbow · opwolf · daisenpuu (Taito X 배치) | Twin Hawk 8 / 8 |
| 2.3.26 | 타이토 volfied | 12 / 12 |
| 2.3.27 | 타이토 darius (YM2203 둘) | 18 / 19 |
| 2.3.28 | 타이토 mlanding | 7 / 7 |

현재 숫자: PC-98 DOS 300 zip 표본 295 / 303, PC-88 522 / 535, X1 150 / 152, MSX generic 527 / 530, X68000 generic 510 / 553 (`docs/hoot-status.md` 표).

## 3. 꼭 기억할 규칙 (다시 틀리기 쉬운 것)

- **OPN 타이머 (hoot ssFMTimer)**: 깃발은 LOAD (27h bit0/1)만으로 선다, IRQ는 ENABLE (bit2/3)일 때 넘칠 때마다 한 번씩. PC-98 · PC-88에 적용. X68000 OPM은 실기 규칙 그대로 (hoot 규칙으로 바꾸면 bw68snd가 무음).
- **RAZE**: hoot의 Z80은 레지스터 0으로 시작 (KOEI X1 PATCH가 E239h `NOP · RET M`으로 들어감). Mega Drive Z80에는 적용하면 하나 줄어서 안 함.
- **DOS (hoot식, 실제 DOS 아님)**: 프로그램 시작 DI = 0, 주 8259 = SFNM (ICW4 1Dh), 핸들 256개 (rom file offset = 핸들 번호), `dummysndrom` = A3FEEh bit3, file 이름: 폴더 없으면 잎 이름, fileNN.EXT는 zip 순서 NN번째.
- **PC-88**: `baseclock` 기본 8MHz; mfile_size > mdata_size = 파일 버퍼 · 곡 코드 8~23비트 = 위치; RTC · VRTC는 `use_rtc` · `use_vrtc`로만; 사운드 보드 II (A8h~)는 모든 형식에 둠 (OPN 판에서 빼면 thexder88이 무음); OPN 판 46h/47h를 FFh로 하면 f_crisis가 멈춤.
- **MSX**: BIOS 0000h~3FFFh = C9h (단 0006h/0007h = 98h, 002Bh~002Fh = 0); FM-PAC 롬이 있으면 SLTWRK (FD09h + sec×8 + 2) = 3000h · FCC1h bit7; 시스템 타이머 E6h; SCC+ 카트리지는 A8h 슬롯 ≠ 3인 페이지에만.
- **SCC VGM 클럭**: 규약 1.789772MHz (÷16). 재생기는 2.5MHz 아래면 두 배로 읽음 (`SccEmulator.InputClock`).
- **푼 zip 표시 (`HootArchive.Mark`, 지금 `.fmopl_hoot67`)**: 색인은 첫 풀 때 Playable인 항목만 넣는다 → **Playable에 형식을 더할 때마다 숫자를 올린다**. archive="a,b" = 이 zip + 같이 쓰는 zip (`HootArchive.Files`).
- **SelfTest 실제 파일 시험은 저장소의 bin에서 돌릴 때만** 돈다 (스크래치에 복사한 exe는 `_incoming`을 못 찾아 SKIP).
- 조사 결과 비교는 줄마다 상태를 맞대어 본다 (게임 이름에 깨진 글자가 있으면 키가 어긋나 놓친 적 있음 — Blassty).

## 4. 도구 · 진단 방법

- **Z80 역어셈블**: 사용자가 z80dis를 anaconda에 설치 → `/c/Users/Lenovo/anaconda3/python.exe` 로 실행 (기본 python에는 없음). 스크래치에 만든 `zdis.py` 사용법: `python zdis.py 파일 적재주소 시작주소 개수 [파일오프셋]`. 내용:
  ```python
  from z80dis import z80
  def dis(data, base, start, count):
      pc = start
      for _ in range(count):
          off = pc - base; dec = z80.decode(data[off:off+4], pc); n = dec.len or 1
          print('%04X  %-12s %s' % (pc, data[off:off+n].hex(' '), z80.disasm(data[off:off+4], pc))); pc += n
  ```
- **x86 · 68000 역어셈블**: 기본 python의 capstone (`capstone.Cs(CS_ARCH_X86, CS_MODE_16)`).
- **조사 명령** (SelfTest): `--hoot98survey pc98 300 6` · `--hoot88survey pc88 1000 8` · `--hootx1survey x1 1000 8` · `--hootmsxsurvey msx 1000 8` · `--hootx68survey X68k 1000 6` · `--hootatsurvey pc 1000 6` · `--hootmdsurvey smd 1000 6` · `--kssurvey msx 1000 6` (폴더는 `_incoming/Hootarchive/…`). 곡마다: `HOOT_SONGS=1 … --test hoottaitoopm` 등.
- **PC-98 추적**: `--hoot98trace zip 항목 초` + 환경 `HOOT_STEP=세그먼트 HOOT_STEP_NOW=1` (명령 하나씩), `HOOT_TITLE=n` (몇째 곡).
- **임시 진단 (커밋 안 함)**: MSX · PC-88 기계를 부팅 → 곡 → `Run(작게)` 반복하며 PC 표본 · 메모리 덤프 · "SP가 튈 때 / IFF가 꺼질 때 / 어떤 바이트가 바뀔 때" 직전 PC 고리를 찍는 `DiagMsx.cs` (`--diagmsx` · `--diag88`)를 만들어 썼다. 다시 필요하면 같은 모양으로 만든다 (Boot · Play · `m.Run(1)` 고리 + `Queue<string>`).
- 버전별 재현: `git worktree add 스크래치/wt 커밋` → 그 안에서 빌드 · 한 zip만 든 폴더로 조사 → `git worktree remove`.

## 5. 남은 무음 · 미지원과 알아낸 원인

| 기종 | 항목 | 알아낸 것 |
|---|---|---|
| PC-98 | 5th2 (UKKY MDZN) | 드라이버가 파일 열기 실패 때 `OR CS:[BP+4],1`로 자기 코드 (02A4h)를 1비트 고침. mdznplay가 DS:DX = CS:07E2h (쓰레기 이름)로 기능 0을 부름. hoot가 이 열기를 어떻게 넘기는지 모름 |
| PC-98 | regpow3s | 아무도 안 가로챈 INT EFh를 부름 (다른 상주 프로그램 필요) |
| PC-98 | strodai2 · zenith | 여러 드라이버 얽힘 / MIDI 판 — 못 봄 |
| PC-88 | Lizard | 메인 고리에서 도는 시퀀서 (00B2h 대기 루틴이 EI 후 타이머 IRQ로 B1h를 지움). 더 추적 필요 |
| PC-88 | Gall Force · Byouin · Shutendouji · P1 데모 × 2 | 인터럽트가 안 들어옴 (byouin은 N88-BASIC F3xxh 벡터 · E814h 훅 기대) |
| PC-88 | Prajator × 2 | 부팅이 명령 대기에 안 닿음 |
| PC-88 | Yokosuka Wars · Super Pit Fall | 칩 쓰기는 있는데 키온 없음 |
| PC-88 | Mazekun (ADPCM만) · Wingman Special (zip에 곡 파일 없음) | ADPCM RAM 미구현 / 자료 문제 |
| X1 | Super Laydock × 2 | PROG 재생 루틴이 곡 자료를 HL = 6000h에서 읽음. xml에 mdata_addr 없음 (기본 4000h를 바꾸면 다른 40여 항목 무음) |
| MSX | Rona × 2 | 시작 코드 049Dh가 6개 넣고 CALL D2F0h, D2F0h 끝은 6개 꺼내고 RET → 돌아갈 곳 어긋남 (항목 자체 결함) |
| MSX | fukujin1 PSG | 목록의 첫 곡들이 FM 자료 (FM00~FM04) |
| X68000 | cos68snd + tank.x 묶음 약 13 | xml 오프셋 ↔ zip의 큰 TANK.X 판 차이 |
| 타이토 | Legend of Kage (kage) | HootCapcomOpn 배치 (RAM 8000h · YM2203 9000h/A000h · 래치 B000h + NMI)로 명령은 큐 (8780h~)에 들어가고 3100h 표로 분기하지만 채널이 안 시작됨 — 되돌려 둠 |
| 타이토 | galastrm | zip 표본 ROM 이름 (c99-06~09) ≠ 목록 (c99-07~10) |
| 타이토 | opwolf ADPCM · darius ADPCM | ADPCM 칩 자리 다름 / 둘째 Z80 (MSM5205) 미구현 |

## 6. 다음 할 일 (추천 순)

1. **MSM5232 칩** 추가 → 타이토 flstory 5 · nycaptor 3 · msisaac · lsasquad · buggychl (Z80 + AY + MSM5232). 한 번에 10항목 남짓.
2. 타이토 래치식 보드: tnzs 계열 (tnzs · kageki · chukatai · plumppop · arkanoid2 · insectx · kabukiz), halleysc (AY × 4), kikikai · kicknrun, Legend of Kage 마무리. MAME 사운드 맵을 보고 `HootCapcomOpn` (래치 + YM2203 둘) · `HootTaitoOpm` (TC0140SYT) 배치로 넣는다.
3. 68000 사운드 보드: bshark (Z 시스템, 68000 + YM2610), cybertnk (68000 + Y8950 둘).
4. PC-88 남은 13 · Lizard 추적, ADPCM RAM (mazekun · hardrank 효과음 · PMD88).
5. `docs/todo.md` 4장의 나머지 (곡 길이 자동, seek, VGM 듀얼 칩 클럭 …).

## 7. 작업 방식 (이번에 쓴 것)

- 한 단계 = 커밋 하나 (`vX.Y.Z: …` 한국어), `Directory.Build.props` 판 올림, `README.md` · `docs/hoot-status.md` · `docs/todo.md` · `_incoming/Todo.md` 갱신, SelfTest 시험 추가 → 전체 SelfTest 통과 후 커밋. 푸시는 요청이 있을 때만.
- 바꾸기 전 · 뒤 조사를 같은 설정으로 돌려 줄어든 항목이 없는지 줄마다 비교. 근거 없는 변경 · 효과 없는 변경은 되돌린다 (이번에 되돌린 것: 명령 포트 래치, OPN 판 46h/47h = FFh, starship 형식, FM-PAC 7FF4h/7FF5h, Mega Drive 레지스터 0, PC-88 보드 II를 OPNA만, X1 기본 6000h, Kage).
