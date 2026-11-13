# Instrument Maker 재현 — 필요 기능 정리

Visual Composer는 악기를 만들거나 고치지 못하고, 악기 제작은 별도 프로그램인 **Instrument Maker**(와 Bank Manager)가 맡았음.
이 문서는 BNK 악기 편집기를 C#으로 재현하기 위한 기능 목록. (원본 스크린샷으로 확인한 내용 반영)

---

## 0. 원본 화면에서 확인된 내용 (스크린샷 기준)

640×400 흑백 DOS 그래픽 화면, 제목 표시줄 우측에 `Untitled`(새 악기/뱅크 상태로 보임).

### 0.1 메뉴·조작
- 상단 메뉴: `F5 File` · `F6 Options` · `F10 Help` (**Edit 메뉴 없음**)
- 두 열의 제목: `F1 Carrier` · `F2 Modulator` → **F1/F2 키로 해당 오퍼레이터 열에 포커스**
- 마우스 조작: 슬라이더 양끝 화살표 버튼(◁ ▷)과 체크박스
- 화면 하단에 **피아노 건반**이 있고, 키 하단에 컴퓨터 키보드 키 표시가 있음 (시음용으로 보임. 표시된 키는 `Z`로 읽힘, 정확한 매핑은 미확인)

### 0.2 레이아웃
- **캐리어가 왼쪽, 모듈레이터가 오른쪽** (직관적 순서와 반대. BNK 파일은 모듈레이터가 먼저 저장됨)
- 행 = 파라미터, 열 = 오퍼레이터. 값은 슬라이더 우측에 숫자로 표시

### 0.3 파라미터 행 (위에서 아래 순서, 원본 표기)

| 순서 | 원본 표기 | 입력 방식 | BNK 필드 | 비고 |
|---|---|---|---|---|
| 1 | Attack Rate | 슬라이더 + 숫자 | attack | |
| 2 | Decay Rate | 슬라이더 + 숫자 | decay | |
| 3 | Sustain Level | 슬라이더 + 숫자 | sustain | |
| 4 | Release Rate | 슬라이더 + 숫자 | releaseRate | |
| 5 | Sustaining Sound | 체크박스 | eg | |
| 6 | Envelope Scaling | 체크박스 | ksr | |
| 7 | Frequency Multiplier | 슬라이더 + 숫자 | multiple | |
| 8 | Modulation Feedback | 슬라이더 + 숫자 | feedback | **모듈레이터 열에만 있음** (캐리어 열은 비어 있음) |
| 9 | Pitch Vibrato | 체크박스 | vib | |
| 10 | Output Level | 슬라이더 + 숫자 | totalLevel | 캐리어 63 / 모듈레이터 48 표시 |
| 11 | Level Scaling | 슬라이더 + 숫자 | ksl | |
| 12 | Amplitude Vibrato | 체크박스 | am | |
| 13 | Wave Form | 슬라이더 + **파형 아이콘** | iModWaveSel / iCarWaveSel | 숫자 대신 파형 그림 표시 |

### 0.4 화면에 나온 예시 값 (테스트 데이터로 활용 가능)

| 파라미터 | Carrier | Modulator |
|---|---|---|
| Attack Rate | 15 | 15 |
| Decay Rate | 2 | 1 |
| Sustain Level | 8 | 10 |
| Release Rate | 4 | 3 |
| Sustaining Sound | 꺼짐 | 꺼짐 |
| Envelope Scaling | **켜짐** | 꺼짐 |
| Frequency Multiplier | 1 | 1 |
| Modulation Feedback | — | 3 |
| Pitch Vibrato | 꺼짐 | 꺼짐 |
| Output Level | 63 | 48 |
| Level Scaling | 0 | 2 |
| Amplitude Vibrato | 꺼짐 | 꺼짐 |
| Wave Form | 첫 번째 파형(사인형) | 첫 번째 파형(사인형) |

### 0.5 이 화면에 **없는** 것 (원본에 없거나 다른 곳에 있음)
- **Connection(FM/가산) 항목이 없음** → Options 메뉴 안에 있는지, 고정값인지 미확인 (BNK에는 `con` 필드가 존재)
- 엔벨로프 그래프, 파형 미리보기, 레벨 미터 없음
- 악기 이름 입력란과 악기 목록도 이 화면에는 없음 → File 메뉴 쪽에서 열고 고르는 구조로 추정(미확인)
- Melodic/Percussive 모드 설정은 Options 메뉴에 있다고 알려져 있음(다른 자료). 화면으로는 미확인

### 0.6 확인 필요한 것
- **Output Level의 방향**: 화면에서 캐리어 63이 슬라이더 최대 위치. 원본 UI는 "출력 세기(클수록 큼)"로 보이는데 OPL 레지스터의 Total Level은 클수록 작은 소리임 → BNK에 저장된 값이 UI 값과 같은지, `63 - 값`인지 **실제 BNK로 대조 필요**
- Sustain Level도 OPL은 "작을수록 큰 소리"이므로 UI 표시 방향 확인 필요
- Wave Form 슬라이더 범위(OPL2는 4종)

---

## 1. 길이·높낮이·음색은 어떤 수치로 조합되나

악기 1개 = **오퍼레이터 2개(캐리어 + 모듈레이터)** × 파라미터 + 피드백/연결.

| 느낌 | 원본 표기의 관련 수치 | 설명 |
|---|---|---|
| **길이** (소리가 얼마나 지속되나) | Attack Rate, Decay Rate, Sustain Level, Release Rate, Sustaining Sound | 엔벨로프(ADSR). Sustaining Sound를 켜면 건반을 누르는 동안 유지, 끄면 감쇠해서 사라짐(타악기형) |
| **높낮이** (음정 성격) | Frequency Multiplier, Level Scaling, Envelope Scaling, Pitch Vibrato | 두 오퍼레이터의 배수 관계가 배음 구조와 음정감을 결정. 노트 자체의 음높이는 악보에서 정함 |
| **세기** | Output Level (각 오퍼레이터) | 캐리어 = 최종 음량, 모듈레이터 = 변조 깊이(밝기/거친 정도) |
| **음색** | Modulation Feedback, Wave Form | 모듈레이터 자기 피드백, 파형 모양 |
| **떨림** | Amplitude Vibrato, Pitch Vibrato | 주기적 진폭/음정 흔들림 |

---

## 2. 편집 대상 파라미터 범위 (OPL2 기준)

| 원본 표기 | 범위 | 설명 |
|---|---|---|
| Attack Rate | 0~15 | 클수록 빠르게 올라옴 (화면 예시 15) |
| Decay Rate | 0~15 | 클수록 빠르게 감쇠 |
| Sustain Level | 0~15 | 서스테인 도달 레벨 (방향 확인 필요) |
| Release Rate | 0~15 | 클수록 빠르게 사라짐 |
| Sustaining Sound | 켬/끔 | 소리 유지 여부 |
| Envelope Scaling | 켬/끔 | 높은 음일수록 엔벨로프를 빠르게 (KSR) |
| Frequency Multiplier | 0~15 | 주파수 배수 (OPL 표준: 0=×½, 1~10=×1~×10, 11=×10, 12·13=×12, 14·15=×15) |
| Modulation Feedback | 0~7 | 모듈레이터 자기 피드백 (모듈레이터만) |
| Pitch Vibrato | 켬/끔 | 비브라토 |
| Output Level | 0~63 | 오퍼레이터 출력 (방향 확인 필요) |
| Level Scaling | 0~3 | 음이 높아질수록 출력을 줄이는 정도 (KSL) |
| Amplitude Vibrato | 켬/끔 | 트레몰로 |
| Wave Form | 0~3 | 사인, 반파, 절대값, 펄스형 |

**화면에 없지만 BNK에는 있는 필드**
- Connection (FM/가산): 모듈레이터만 유효. 파일 값은 **반전 저장**(0이면 OPL 비트 1) → 실제 파일로 확인
- Percussive 플래그 / Voice Number: 재생에는 안 쓰임. 읽은 값 보존
- 악기 이름 (최대 8글자)

---

## 3. 뱅크(BNK) 관리 기능

원본 화면에서 확인되지 않아 **File 메뉴에 있었을 것으로 추정**하는 항목들.

- [ ] 새 뱅크 / 열기 / 저장 / 다른 이름으로 저장
- [ ] 뱅크 내 악기 선택(열기), 새 악기, 삭제, 이름 변경
- [ ] 이름순 정렬 유지 (저장 시 알파벳 정렬 보장)
- [ ] 사용/미사용 레코드(flags), `numUsed` / `numInstruments` 관리
- [ ] 이름 중복·8글자 초과 검증
- [ ] 변경 사항 있음 표시 + 닫기 전 저장 확인
- [ ] (선택) 96개 초과 경고 — SCI 변환용 제약이라 선택 기능
- [ ] (v2) HMI 변형(Version 0.0) BNK 열기

## 4. 악기 편집 화면 기능 (원본 방식 우선)

- [ ] **한 화면에 두 열**: 왼쪽 Carrier, 오른쪽 Modulator
- [ ] 슬라이더(◁ ▷ 버튼) + 숫자 표시, 체크박스 방식의 행 구성 (0.3절 순서)
- [ ] `F1` / `F2`로 편집 열 전환
- [ ] Modulation Feedback은 모듈레이터 열에만 표시
- [ ] Wave Form은 숫자 대신 **파형 아이콘** 표시
- [ ] 값 범위 제한
- [ ] Melodic / Percussive 모드 전환 (Options)
- [ ] 퍼커션 모드: 대상 드럼 선택(베이스 드럼 / 스네어 / 탐탐 / 심벌 / 하이햇)
  - 스네어·탐탐·심벌·하이햇은 **모듈레이터 값만 사용** (파일의 캐리어 값은 미사용)
  - 베이스 드럼은 두 오퍼레이터 모두 사용
- [ ] Connection(FM/가산) 편집 (원본 위치 확인 전까지는 Options 안에 배치)
- [ ] Undo / Redo (원본에는 없을 가능성이 크지만 현대식 확장)

## 5. 시음(미리듣기) 기능

- [ ] 화면 하단 건반을 마우스로 클릭해 연주
- [ ] 컴퓨터 키보드 키로 연주 (건반 위 키 표시)
- [ ] 파라미터를 바꾸면 **실시간 반영**
- [ ] 누른 만큼 지속 + 떼면 Release (Sustaining Sound 동작 확인)
- [ ] 퍼커션 모드에서는 지정된 드럼 보이스로 재생 (리듬 모드 레지스터 사용)
- [ ] 마스터 음량

## 6. 확장 기능 (원본에는 없음, 있으면 편하지만 후순위)

- [ ] 엔벨로프 그래프 (Attack→Decay→Sustain→Release 곡선)
- [ ] 파형/스펙트럼 미리보기
- [ ] 악기 복제, 다른 뱅크로 복사
- [ ] 값 되돌리기, 초기값 리셋

## 7. 파일 입출력

- [ ] `.BNK` 읽기/쓰기 (포맷은 `adlib-file-formats.md` 참고)
- [ ] `.INS` 개별 악기 가져오기/내보내기 (54B / 80B 두 변형)
- [ ] BNK ↔ INS 일괄 변환 (Bank Manager 역할)
- [ ] (v2) `.IBK`, `.SBI` 등 다른 OPL 악기 포맷
- [ ] round-trip 테스트: 읽고 다시 저장했을 때 원본과 바이트 동일한지
- [ ] 읽은 알 수 없는 필드(`unknown`, `unknown2`)는 보존해서 다시 기록

## 8. Visual Composer 본체와의 연동

- [ ] 작곡 화면의 `Instr.` 행에서 선택하는 악기 목록 = 현재 BNK
- [ ] 악기 편집기에서 수정/저장하면 열려 있는 곡에 반영
- [ ] ROL의 Timbre 이벤트 이름이 BNK에 없으면 경고

---

## 9. 우선순위

| 단계 | 기능 |
|---|---|
| **MVP** | BNK 열기, 악기 선택, 두 열 파라미터 편집(0.3절 13개 행), 하단 건반 시음, 저장 |
| **v1** | 새 악기/삭제/이름 변경, INS 가져오기/내보내기, 퍼커션 모드, Connection 편집 |
| **v2** | 엔벨로프 그래프, 파형 미리보기, HMI BNK, 다른 포맷 가져오기 |

## 10. 구현 시 참고

- 시음과 곡 재생은 **같은 OPL 에뮬레이터/레지스터 쓰기 코드**를 공유 (악기 → 레지스터 변환 함수 1개)
- 악기 모델(`AdlibInstrument`)은 BNK/INS/편집기가 공유 (정의는 `adlib-file-formats.md` 5.1절)
- 슬라이더 변경 → 모델 갱신 → 해당 채널 레지스터 재기록 → 소리 즉시 반영
- **UI 값 ↔ 저장 값 변환 계층**을 따로 두기 (Output Level / Sustain Level 방향 확인 결과에 맞춰 한 곳에서 처리)

---

## 출처

- 사용자 제공 스크린샷 (Visual Composer 편집 화면, Instrument Maker 편집 화면)
- Sierra Wiki — AdLib Instrument Maker: https://wiki.sierrahelp.com/index.php/AdLib_InsMaker
- Sierra Wiki — AdLib Visual Composer: https://wiki.sierrahelp.com/index.php/AdLib_Visual_Composer
- ModdingWiki — AdLib Instrument Bank Format: https://moddingwiki.shikadi.net/wiki/AdLib_Instrument_Bank_Format
