# AdLib 파일 포맷 정리 (INS / BNK / ROL)

Visual Composer 재현용 포맷 레퍼런스. 모든 정수는 **리틀엔디언**, `Single`은 float32.

출처 (ModdingWiki)
- INS: https://moddingwiki.shikadi.net/wiki/AdLib_Instrument_Format
- BNK: https://moddingwiki.shikadi.net/wiki/AdLib_Instrument_Bank_Format
- ROL: https://moddingwiki.shikadi.net/wiki/ROL_Format

---

## 0. 관계도

```
ROL (곡: 노트/템포/볼륨/피치/악기교체 이벤트)
 └─ Timbre 이벤트의 instName(9바이트 문자열)
      └─ BNK 안의 악기 이름으로 조회  (구버전은 INSTNAME.INS 파일명으로 조회)
           └─ 악기 1개 = 2오퍼레이터 파라미터 (OPLREGS ×2 + 웨이브셀렉트 ×2)
```

- INS(1987): 악기 1개 = 파일 1개 (약 80바이트, 파일명이 악기 이름)
- BNK(Visual Composer 1.5, 1989): 여러 악기를 한 파일로 묶음. INS 대체
- 퍼커션/멜로딕 구분은 **재생할 때 어느 채널에 쓰이느냐**로 결정됨 (악기 자체는 구분 없음)

---

## 1. OPLREGS 구조 (INS/BNK 공통 오퍼레이터 파라미터)

필드 13개. 순서 그대로 저장됨. INS는 필드당 UINT16LE, BNK는 필드당 UINT8.

| # | 필드 | 의미 | OPL 레지스터 (오퍼레이터 기준) |
|---|---|---|---|
| 1 | ksl | Key Scale Level | 0x40 bit 6-7 |
| 2 | multiple | 주파수 배수 | 0x20 bit 0-3 (`& 0x0F`) |
| 3 | feedback | 피드백 (**op0만 유효**, op1은 무시) | 0xC0 bit 1-3 |
| 4 | attack | Attack rate | 0x60 상위 4비트 |
| 5 | sustain | Sustain level | 0x80 상위 4비트 |
| 6 | eg | EG 타입 (0 아님 = on) | 0x20 bit 5 |
| 7 | decay | Decay rate | 0x60 하위 4비트 |
| 8 | releaseRate | Release rate | 0x80 하위 4비트 |
| 9 | totalLevel | Total level | 0x40 bit 0-5 |
| 10 | am | 트레몰로 | 0x20 bit 7 |
| 11 | vib | 비브라토 | 0x20 bit 6 |
| 12 | ksr | Key Scale Rate | 0x20 bit 4 |
| 13 | con | 연결 방식 (**op0만 유효**) | 0xC0 bit 0, **반전**: 0이면 OPL 비트=1, 그 외 0 |

> 위키 표에 대부분 `[verify this]` 표기가 있음. 실제 값 범위/마스킹은 실제 파일로 검증 필요.

### 레지스터 조합 (표준 OPL2 지식 기반, 구현 시 참고)

```
reg 0x20+op : AM<<7 | VIB<<6 | EG<<5 | KSR<<4 | MULT(4bit)
reg 0x40+op : KSL<<6 | TL(6bit)
reg 0x60+op : AR<<4 | DR
reg 0x80+op : SL<<4 | RR
reg 0xE0+op : WaveSelect(0~3)
reg 0xC0+ch : FB<<1 | CNT      (CNT: con==0 → 1, 아니면 0)
```

채널별 오퍼레이터 오프셋 (OPL2, 채널 0~8)

```
모듈레이터: 0x00 0x01 0x02 0x08 0x09 0x0A 0x10 0x11 0x12
캐리어    : 0x03 0x04 0x05 0x0B 0x0C 0x0D 0x13 0x14 0x15
```

---

## 2. INS 포맷 (.ins) — 악기 1개

| 타입 | 이름 | 설명 |
|---|---|---|
| UINT16LE | unknown | 항상 0x0000 |
| OPLREGS(26B) | oplModulator | 모듈레이터(op0) |
| OPLREGS(26B) | oplCarrier | 캐리어(op1) |
| UINT16LE | iModWaveSel | 모듈레이터 웨이브셀렉트 (bit 0-1만 유효) |
| UINT16LE | iCarWaveSel | 캐리어 웨이브셀렉트 (bit 0-1만 유효) |
| char[22] | unknown2 | 용도 불명 예약 영역 (끝에서 두 번째 바이트가 보통 0x00/0x01) |

- 전체 크기: 2 + 26 + 26 + 2 + 2 + 22 = **80바이트**
- **초기 버전은 54바이트**: `unknown` + 모듈레이터 + 캐리어까지만 있고 웨이브셀렉트/unknown2 없음
- 일부 파일은 `oplCarrier`가 아예 없음 (단일 오퍼레이터 퍼커션용으로 추정)
- 악기 이름 필드 없음 → **파일명이 악기 이름**
- 구현 팁: 파일 크기(54 / 80 / 캐리어 없음)로 변형 판별

---

## 3. BNK 포맷 (.bnk) — 악기 뱅크

### 3.1 전체 레이아웃

```
[ 헤더 28B ]
[ 이름 목록: numInstruments × 12B ]   ← offsetName 위치
[ 악기 데이터: numInstruments × 30B ] ← offsetData 위치
```

### 3.2 헤더 (28바이트)

| 타입 | 이름 | 설명 |
|---|---|---|
| UINT8 | verMajor | 메이저 버전 |
| UINT8 | verMinor | 마이너 버전 |
| char[6] | signature | `"ADLIB-"` |
| UINT16LE | numUsed | 사용 중인 악기 레코드 수 |
| UINT16LE | numInstruments | 파일 내 전체 악기 레코드 수 |
| UINT32LE | offsetName | 이름 목록 시작 오프셋 |
| UINT32LE | offsetData | 악기 데이터 시작 오프셋 |
| BYTE[8] | pad | 0x00 패딩 |

### 3.3 이름 목록 (항목당 12바이트 × numInstruments)

| 타입 | 이름 | 설명 |
|---|---|---|
| UINT16LE | index | 데이터 섹션 인덱스. `offsetInstr = offsetData + index * 30` |
| UINT8 | flags | 0 = 미사용 레코드, 1 = 사용 |
| char[9] | name | 악기 이름 (NULL 종료 필수, 최대 8글자) |

- **이름순(알파벳) 정렬 필수** — Ad Lib 도구가 올바르게 처리하려면 정렬되어 있어야 함
- 이름 목록의 순서와 데이터 섹션 순서는 별개 (index로 연결)

### 3.4 악기 데이터 (항목당 30바이트)

| 타입 | 이름 | 설명 |
|---|---|---|
| UINT8 | iPercussive | 0 = 멜로딕, 1 = 퍼커션 |
| UINT8 | iVoiceNum | 보이스 번호 (퍼커션만 의미 있음) |
| OPLREGS(13B) | oplModulator | 모듈레이터 (UINT8 ×13) |
| OPLREGS(13B) | oplCarrier | 캐리어 (UINT8 ×13) |
| UINT8 | iModWaveSel | 모듈레이터 웨이브셀렉트 |
| UINT8 | iCarWaveSel | 캐리어 웨이브셀렉트 |

- 합계: 2 + 13 + 13 + 1 + 1 = **30바이트**
- INS와 동일한 구조인데 필드가 UINT16 → UINT8로 줄어든 형태
- `iPercussive` 플래그는 **공식 ROL 플레이어가 사용하지 않음**. 퍼커션 여부는 ROL에서 사용된 채널 번호가 결정 (같은 악기를 멜로딕/퍼커션 양쪽에 쓸 수 있음)

### 3.5 HMI 변형 ("Version 0.0")

- Human Machine Interfaces 사운드 엔진(HMP/HMI)용으로 쓰이는 변형
- 특징: 메이저/마이너 버전이 모두 0, flags 바이트가 0/1 외의 값일 수 있고 0이어도 미사용이라고 단정할 수 없음
- 알려진 파일명: `DRUMS.BNK`, `MELODIC.BNK`, `BNKDRUM.BNK`
- 알려진 파일은 모두 **5,404바이트, 레코드 128개**
- 악기 데이터 자체의 차이는 아직 미조사 → 일반 BNK 파서와 호환 안 될 수 있음
- 지원 우선순위는 낮게 (필요하면 v2)

---

## 4. ROL 포맷 (.rol) — 곡

### 4.1 헤더

| 타입 | 이름 | 설명 |
|---|---|---|
| UINT16LE | majorVersion | 보통 0 |
| UINT16LE | minorVersion | 보통 4 |
| char[40] | signature | 보통 `\roll\default` (NULL 종료) |
| UINT16LE | tickBeat | 박당 틱 수 |
| UINT16LE | beatMeasure | 마디당 박 수 |
| UINT16LE | scaleY | 에디터 Y 스케일 |
| UINT16LE | scaleX | 에디터 X 스케일 |
| BYTE | reserved | 0 |
| UINT8 | isMelodic | 1 = 멜로딕(9채널), 0 = 리듬 모드(11채널) |
| UINT16LE[45] | counters | 트랙별 카운터 (4.3 참조) |
| BYTE[38] | filler | 0 패딩 |

### 4.2 트랙 (항상 45개)

순서: **Tempo 1개 → (Voice, Timbre, Volume, Pitch) × 11**. 9채널 모드여도 45개 전부 존재.

**Tempo 트랙**

| 타입 | 이름 | 설명 |
|---|---|---|
| char[15] | trackName | 보통 "Tempo" |
| Single | basicTempo | BPM |
| UINT16LE | nEvents | 이벤트 수 |
| E_TEMPO[nEvents] | | `{ UINT16LE atTick; Single multiplier(0.01~10.0) }` |

재생 주파수(Hz) = `(basicTempo / 60) * tickBeat * multiplier`

**Voice 트랙** (노트)

| 타입 | 이름 | 설명 |
|---|---|---|
| char[15] | trackName | "Voix ##" |
| UINT16LE | nTicks | 트랙 총 틱 |
| E_NOTE[] | | `{ UINT16LE note; UINT16LE duration }` — duration 합이 nTicks에 도달할 때까지 반복 |

- `note`: 0 = 쉼표, 12~107 = 노트온 (MIDI 번호, **60 = 가운데 도**. 칩 노트 0~95 = ROL − 12. 실제 곡 파일과 adlib.c `MID_C 60`으로 확인)
- 노트 이벤트에 시작 시각이 없음 → **duration을 누적해서 시작 틱 계산**
- 피아노롤 ↔ ROL 변환 시 빈 구간은 쉼표(note=0) 노트로 채워야 함

**Timbre 트랙** (악기 교체)

| 타입 | 이름 | 설명 |
|---|---|---|
| char[15] | trackName | "Timbre ##" |
| UINT16LE | nEvents | 이벤트 수 |
| E_TIMBRE[nEvents] | | `{ UINT16LE atTick; char[9] instName; BYTE filler; UINT16LE unknown }` |

- `instName`: BNK의 악기 이름 (또는 `INSTNAME.INS` 파일)
- `unknown`: 용도 불명, 가끔 악기 인덱스와 같음 → **읽은 값 보존 후 그대로 쓰기** 권장

**Volume 트랙**

| 타입 | 이름 | 설명 |
|---|---|---|
| char[15] | trackName | "Volume ##" |
| UINT16LE | nEvents | 이벤트 수 |
| E_VOLUME[nEvents] | | `{ UINT16LE atTick; Single volume(0.0~1.0) }` |

**Pitch 트랙**

| 타입 | 이름 | 설명 |
|---|---|---|
| char[15] | trackName | "Pitch ##" |
| UINT16LE | nEvents | 이벤트 수 |
| E_PITCH[nEvents] | | `{ UINT16LE atTick; Single pitch(0.0~2.0) }` — 1.0이 기준 (피치벤드) |

`atTick`은 모두 **절대 틱**.

### 4.3 counters[45] 구성

| 구간 | 내용 |
|---|---|
| `UINT16LE[11]` cTicks | 보이스별 총 틱 |
| `UINT16LE[11]` cTimbreEvents | 보이스별 Timbre 이벤트 수 |
| `UINT16LE[11]` cVolumeEvents | 보이스별 Volume 이벤트 수 |
| `UINT16LE[11]` cPitchEvents | 보이스별 Pitch 이벤트 수 |
| `UINT16LE` cTempoEvents | Tempo 이벤트 수 |

→ 저장 시 실제 트랙 내용으로 **재계산**해서 기록.

### 4.4 리듬 모드 (isMelodic = 0)

| 채널 | 악기 | 파일에서 쓰는 오퍼레이터 | OPL 셀 |
|---|---|---|---|
| 0~5 | 멜로딕 | 모듈레이터 + 캐리어 | 각각 모듈레이터/캐리어 |
| 6 | 베이스 드럼 | 모듈레이터 + 캐리어 | 13(Ch7 Mod), 16(Ch7 Car) |
| 7 | 스네어 | 모듈레이터 | 17 (Ch8 Car) |
| 8 | 탐탐 | 모듈레이터 | 15 (Ch9 Mod) |
| 9 | 탑 심벌 | 모듈레이터 | 18 (Ch9 Car) |
| 10 | 하이햇 | 모듈레이터 | 14 (Ch8 Mod) |

- 스네어/심벌/하이햇/탐탐은 단일 오퍼레이터: 값은 **파일의 모듈레이터 필드**에서 읽어 **OPL 캐리어 레지스터**에 씀 (파일의 캐리어 필드는 미사용)
- 음높이 연동 (채널 8 탐탐 기준)

| 채널 | 악기 | 음높이 |
|---|---|---|
| 7 | 스네어 | 마지막 탐탐 노트보다 +7 반음 |
| 8 | 탐탐 | 노트온에 지정된 음높이 그대로 |
| 9 | 탑 심벌 | 마지막 탐탐 노트의 음높이 |
| 10 | 하이햇 | 마지막 탐탐 노트보다 +7 반음 |

- 탐탐 초기 주파수는 공식 `adlib.c` 주석상 가운데 도 2옥타브 아래인데, 실제로는 그렇지 않은 것으로 보임 (위키 표기)

---

## 5. C# 구현 메모

### 5.1 모델 (BNK/INS 공통)

```csharp
public sealed class OplOperator
{
    public byte Ksl, Multiple, Feedback, Attack, Sustain, Eg,
                Decay, ReleaseRate, TotalLevel, Am, Vib, Ksr, Con;
}

public sealed class AdlibInstrument
{
    public string Name = "";
    public bool Percussive;      // BNK의 iPercussive (재생엔 미사용)
    public byte VoiceNum;        // BNK의 iVoiceNum
    public OplOperator Modulator = new();
    public OplOperator Carrier = new();
    public byte ModWaveSel, CarWaveSel;
}
```

### 5.2 BNK 리더 순서

1. 헤더 28바이트 읽고 `signature == "ADLIB-"` 확인
2. `offsetName`으로 이동 → `numInstruments`개의 (index, flags, name[9]) 읽기
3. 각 항목에서 `offsetData + index * 30`으로 이동 → 30바이트 파싱
4. `flags == 0` 항목은 목록에서 제외 (단, HMI 변형은 예외 가능)
5. ROL의 `instName` 조회는 이름 비교 (대소문자 무시 권장)

### 5.3 BNK 라이터 주의

- 이름 목록 **알파벳 정렬**
- `numUsed`, `numInstruments`, `offsetName(=28)`, `offsetData(= 28 + numInstruments*12)` 재계산
- 이름은 최대 8글자 + NULL

### 5.4 ROL 리더/라이터 주의

- 45트랙 고정 순서 (9채널이어도 11보이스 분 전부 기록)
- 라이터에서 counters 재계산
- Timbre의 `unknown`, 헤더 `scaleX/scaleY`, `signature`는 읽은 값 보존
- 노트 → 시작틱은 duration 누적으로 계산, 빈 구간은 note=0 쉼표로 채우기
- 문자열 필드(char[15], char[9], char[40])는 고정 길이 NULL 패딩

---

## 6. 아직 불확실한 부분

- OPLREGS 각 필드의 **실제 값 범위/마스킹** (위키 `[verify this]` 다수)
- Timbre 이벤트의 `unknown` 값의 정확한 의미
- INS의 `unknown2` 22바이트 용도
- HMI 변형 BNK의 데이터 영역 차이
- 리듬 모드 탐탐 초기 주파수

→ 실제 `STANDARD.BNK` 및 ROL 곡 파일로 읽어 확인하면서 보완
