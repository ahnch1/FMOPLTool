# hoot 게임 목록 XML 사양 · 정리 규칙 (hootxml.md)

hoot 게임 목록(xml · xml2 · xml3 …)을 읽고 · 비교하고 · 중복 없이 합치는 **별도 정리 코드**를 만들기 위한 자료.
실제 배포본(`_incoming/Hootarchive/`)을 훑어 확인한 사실과, 지금 프로그램 안에 있는 구현
(`src/AdlibComposer.Formats/HootLists.cs`, UI `App/Views/HootCompareForm.cs`)의 규칙을 정리했다.

> 숫자는 2026-11-16에 `xml`(315파일) · `xml2`(247) · `xml3`(536) 을 훑은 값. `xml_new`(577)는 지금 구현으로 합친 결과물.

---

## 1. 파일 배치

```
Hootarchive/
  hoot.xml          ← 최상위 목록: <bind> (확장자 → 드라이버) + <childlists> (<list>xml…/제작사.xml</list>)
  hoot_old.xml      ← 합치기 전 hoot.xml (xml/ 310 · xml2/ 247 · xml3/ 536 줄)
  hoot.xsl · drivers.xsl · hoot.css   ← 브라우저 표시용 (목록 폴더에도 같은 이름으로 있음)
  xml/  xml2/  xml3/                  ← 목록 묶음 (set). 숫자가 클수록 새 묶음
  xml_new/                            ← 합친 결과 (577파일 + _report.txt + xsl · css)
```

- 묶음 폴더 하나 = 제작사(maker)마다 파일 하나 (`13cm.xml`, `konami.xml` …). 파일 이름은 소문자 + 밑줄이 보통.
- 같은 제작사가 묶음마다 다른 이름일 수 있다: `birdysoft.xml` ↔ `birdy_soft.xml`, `hummingbird.xml` ↔ `hummingbirdsoft.xml`.
- hoot는 `hoot.xml`의 `<childlists>`에 적힌 파일만 읽는다. **`<list>` 줄은 들여쓰기 없이** 붙어 있고 사이사이에
  `<!-- ===== A ===== -->` 같은 구분 주석이 있다 (주석은 그대로 둬도 된다).

```xml
	<childlists>
		<!-- ============ Old Sets ============= -->
<list>xml_new/13cm.xml</list>
<list>xml_new/21st_century.xml</list>
		<!-- ================ Z ================ -->
	</childlists>
<!-- ================ END ================ -->
</gamelist>
```

## 2. 인코딩 · 줄 끝 · 머리말

| 항목 | 실제 | 정리 코드에서 |
|---|---|---|
| 인코딩 | 거의 모두 **Shift_JIS (CP932)**, BOM 없음. 예외 2개: `xml/ecsecosoft.xml` (UTF-8, BOM 있음), `xml2/zzz_vermouth.xml` (`encoding="utf-8"`) | 앞 3바이트 BOM 또는 앞 100바이트의 `encoding="utf-8"`이면 UTF-8, 아니면 CP932 |
| 줄 끝 | CRLF (LF만인 파일 2개) | 쓸 때 CRLF로 통일 |
| 들여쓰기 | 탭 | `<game>` 앞에 탭 하나 |
| 외자 · 확장 문자 | NEC 특수 문자 (①, Ⅱ …) 다수 | **항목은 원래 바이트를 그대로 옮긴다** (디코드 → 인코드 왕복 금지, 9장) |

> .NET에서는 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 후 `Encoding.GetEncoding(932)`.
> Python은 `cp932` (`shift_jis`로 읽으면 NEC 특수 문자에서 깨진다).

모든 목록 파일의 머리말 (`<gamelist` 앞까지, 파일마다 같음):

```xml
<?xml version="1.0" encoding="Shift_JIS"?>
<?xml-stylesheet type="text/xsl" href="hoot.xsl"?>
<!DOCTYPE gamelist [
	<!ELEMENT gamelist (game|bind|childlists)*>
	<!ATTLIST gamelist date CDATA #IMPLIED>
	<!ELEMENT childlists (list)*>
	<!ELEMENT list (#PCDATA)>
	<!ELEMENT bind (exts,driver,options?)>
	<!ELEMENT exts (ext)*>
	<!ELEMENT ext (#PCDATA)>
	<!ELEMENT game (name,driver,driveralias?,options?,romlist?,titlelist?)>
	<!ELEMENT name (#PCDATA)>
	<!ELEMENT driver (#PCDATA)>
	<!ATTLIST driver type CDATA #IMPLIED>
	<!ELEMENT driveralias (#PCDATA)>
	<!ATTLIST driveralias type CDATA #IMPLIED>
	<!ELEMENT options (option)*>
	<!ELEMENT option EMPTY>
	<!ATTLIST option name CDATA #REQUIRED
					 value CDATA #REQUIRED>
	<!ELEMENT romlist (rom)*>
	<!ATTLIST romlist archive CDATA #IMPLIED>
	<!ELEMENT rom (#PCDATA)>
	<!ATTLIST rom type CDATA #REQUIRED
				  offset CDATA #IMPLIED
				  crc32 CDATA #IMPLIED>
	<!ELEMENT titlelist (title)*>
	<!ELEMENT title (#PCDATA)>
	<!ATTLIST title code CDATA #REQUIRED
					type CDATA #IMPLIED>
]>
<gamelist date="2005/09/25">
	<game> … </game>
	…
</gamelist>
```

**DTD는 실제와 다르다**: `<range>` (titlelist 안), `extcode` 속성, `romlist archiver`(오타)가 DTD에 없다.
검증(validating) 파서를 쓰지 말고, DTD는 읽고 버린다(내부 DTD 처리를 끄거나 `<gamelist` 앞을 잘라 낸다).

## 3. 항목 `<game>` 구조

```xml
	<game>
		<name>[PC-9801] Yume no Sei (夢の精) (YM2203)</name>
		<driver type="opn">pc98dos</driver>
		<driveralias type="PC-9801">13cm</driveralias>
		<options>
			<option name="funcvect" value="0x7e"/>
			<option name="clockmul" value="8"/>
		</options>
		<romlist archive="yumenosi_98">
			<rom type="file" offset="-1">PMD_98.COM</rom>
			<rom type="file" offset="0x10">MC_1.MMM</rom>
			<rom type="shell" offset="0">pmd_98</rom>
		</romlist>
		<titlelist>
			<title code="0x10">MC_1.MMM   : Stardust Dance</title>
			<range min="0x0800" max="0x080d" extcode="0">YUME.EFC   : SE %02X</range>
		</titlelist>
	</game>
```

항목 수: xml 4,939 · xml2 3,175 · xml3 7,343 = **15,457** → 합치면 9,494.
모든 항목에 `name` · `driver` · `romlist` · `titlelist`가 있다 (`options`는 약 90%, `driveralias`는 거의 모두).

### 3.1 `<name>` — 표시 이름

형식(관례, 강제 아님): **`[기계] 영문 제목 (일본어 제목) (칩) (만든 이)`**

| 예 | 기계 | 바탕 | 일본어 | 칩 | 만든 이 |
|---|---|---|---|---|---|
| `[PC-9801] Yume no Sei (夢の精) (YM2203)` | PC-9801 | Yume no Sei | 夢の精 | YM2203 | |
| `[PC-8801] Revolter ver1.00 (OPNA) (A.S.C.Group)` | PC-8801 | Revolter ver1.00 | | OPNA | A.S.C.Group |
| `[MSX] ロードス島戦記 福神漬(OPLL)` | MSX | (일본어만) | | OPLL | |
| `[PC-9801] Majoriko … (OPN+MT-32 Emulation)` | | | | OPN+MT-32 Emulation | |

- 옛 묶음(xml)은 칩을 별명으로 (`OPN`, `OPM+MSM`), 새 묶음(xml3)은 칩 번호로 (`YM2203`, `YM2151+MSM6258`) + 일본어 제목을 붙인다.
- `[` 없이 시작하는 이름 3개. `… (칩) <- 메모` 같은 꼬리글이 붙은 것도 있다 (` <-` 뒤는 뗀다).
- 괄호 안에 괄호가 있을 수 있다 → 끝에서부터 괄호 깊이를 세어 덩어리를 뗀다.
- 기계 표기는 묶음마다 다르다: `PC-9801` 5,307 · `PC-98` 503 · `PC-9821` 236, `PC-8801` 901 · `PC-88` 111, `Mega Drive` 249 · `Genesis` 69, `MSX` · `MSX2` …
- 글자 참조: `&amp;` 1,890 · `&lt;` 550 · `&gt;` 529 · `&quot;` 32 · `&#228;` 2. 예전 코드는 `&nbsp;`도 대비했다 (XML 파서에 넣기 전에 `&#160;`으로 바꿈). 이스케이프 안 된 `&` 3곳 → 그 항목은 XML 파서가 실패한다 (6장).

### 3.2 `<driver type="…">이름</driver>`

드라이버(기종) + 종류. 개수 (xml+xml2+xml3):

| driver | type (개수) |
|---|---|
| pc98dos | opn 3415 · beep 1003 · opna 489 · 86 185 · soundblaster16 13 |
| pc98vx / pc98 | opn 559 / 165 · beep 96 / 8 · opna 54 / 4 · 86 17 · amd98 11 · rashin 10 · soundorchestra 6 |
| pcatdos | beep 1158 · adlib 837 · soundblaster16 121 · gameblaster 17 · tandy 8 |
| x68k | generic 1341 |
| msx | generic 713 · kss 420 |
| pc88 / pc88va / pc88vados | opn 707 · opna 282 · 8801-11 4 / opn · opna |
| snes · nes · gb | spc 532 · nsf 14 · gbs 13 |
| neogeo | generic 290 |
| megadrive | generic_z80 146 · koei 29 · iyinc 23 · 1c00 5 |
| x1 | psg 159 · opm 144 |
| fm7 · fm77av · pc9821dos · mucomfm | generic · xanadu · opn · ysav |
| 아케이드 | taito (f2system 80 · f3system 72 …), capcom (cps2 76 · cps1 58 …), namco, sega, konami, irem … 수백 가지 |

### 3.3 `<driveralias type="…">…</driveralias>`

- type = 기계 표시 이름. 표기가 제각각: `NEC PC-9801` 3245 · `PC-9801` 1946 · `PC-98` 503, `ARCADE` · `Arcade`, `IBM PC/AT` · `PC/AT` · `PC`, `MEGA DRIVE` · `Genesis` …
  가끔 제작사 이름이 들어 있다 (`doujin`, `onion_soft`, `firecracker` …), `pc98dos` 2.
- 값 = 보통 **제작사 = 파일 이름** (9,644개 일치). 5,814개는 다르다 (파일 이름 변경 · 동인 파일에 모은 것 등) — 파일 이름과 같다고 가정하지 말 것.
- 항목 수보다 1개 많다 → `driveralias`가 둘인 항목이 있다.

### 3.4 `<options>` / `<option name value/>`

이름 · 값 모두 문자열. 값은 `0x…` 16진 또는 10진 (음수 있음: `ssg_mix -18`). 이름 대소문자가 섞일 수 있어 비교는 대소문자 무시.

| 무리 | 이름 (개수) |
|---|---|
| **장치를 고르는 것** (다르면 다른 판) | `midiout` 2952 · `midiout_type` 2952 · `subtype` 57 |
| 소리 섞기 (판과 무관) | `*_mix` 전부: ssg_mix 2410 · fm_mix 2076 · pcm_mix 980 · opm_mix 978 · opl2_mix 834 · midiout_mix 681 … · `total_mix` · `reverb1/2` |
| 클럭 | `clockmul` 6966 · `clock_mul` 151 · `baseclock` · `cpuclock` |
| 시작 · 메모리 배치 | `funcvect` · `init_pc` · `bootcs` · `bootip` · `mdata_addr` · `mdata_size` · `mfile_size` · `dataaddr` · `filesize` · `datamax` · `adressing` 245(오타) · `addressing` 30 · `topaddr` · `data1` · `size1` · `reqaddr` · `trigadr` · `trigvalue` … |
| 장치 켜기 | `mfp` · `use_vrtc` · `use_rtc` · `use_opll` · `use_scc` · `use_ctc0/3` · `dummysndrom` · `pcm8` · `mt32` … |

옵션은 120가지가 넘는다 — 정리 코드는 이름을 몰라도 **그대로 옮길 수 있어야** 한다.

### 3.5 `<romlist archive="…">` / `<rom type offset crc32>`

- `archive` = **zip 이름 (확장자 없음)**. hoot는 `<기종 폴더>/<archive>.zip`을 연다. 같은 게임 판단의 첫 열쇠 (8장).
  - 단일 15,387 · **쉼표로 여러 개** 39 (`fukujin1_msx,fmpac_msx` — 공용 롬 zip을 함께) · 없음 32.
  - 앞뒤 공백이 있을 수 있음 → Trim. 비교는 대소문자 무시.
  - `archiver="…"` 18곳 = `archive` 오타 (hoot는 못 읽음). 정리 코드에서 경고 대상.
- `rom` 내용 = zip 안 파일 이름 또는 명령 줄 (`shell`, `conin`), 16진 문자열 (`binary`).
- `offset` = 16진 또는 10진. `-1` = 자리 없음(먼저 올려 둘 파일), `bgm` · `file`에서는 곡 번호/코드와 이어진다.
- `crc32` 486곳 (주로 아케이드 롬).
- type 개수: file 144,828 · bgm 52,678 · code 28,719 · data 15,229 · shell 12,137 · conin 6,528 · pcm 2,202 · voice 2,108 · adpcm 1,627 · x 1,451 · adpcma 827 · bgm2 383 · device 156 · binary 91 · prog 86 · main0~3 · sub0~1 · bios · sccp · fmbios · disk · … (40가지 넘음)

### 3.6 `<titlelist>` — 곡 목록

```xml
<title code="0x14">ON_1.MMM   : Daydream</title>
<range min="0x0800" max="0x080d" extcode="0">YUME.EFC   : SE %02X</range>
```

- `title code` = 곡 코드. 16진 `0x..` 349,880 · 10진 11,785 · 맨 16진(`0x` 없이) 14 · `code="0x"`처럼 망가진 것 7 · code 없음 58.
  **비교 전 정규화**: 소문자 + 숫자로 읽어 같은 값으로 (`0x0A` = `0xa` = `10`). 지금 구현은 문자열(소문자)만 비교한다 — 개선 여지.
- 같은 항목 안에 **같은 code가 두 번** 나오는 항목 337개 (같은 곡을 다른 제목으로, 또는 실수).
- `title type` 속성은 DTD에만 있고 실제로는 쓰이지 않는다.
- `range min max extcode` = 코드 범위 (보통 효과음). 내용은 printf 무늬 (`%02X` · `%2X` · `%02x` · `%d`). extcode 0/1 (뜻 미확인, 그대로 옮김). 2,797개.
  제목 없이 range만 있는 항목은 range가 곧 곡 목록 (`HootArchive.Parse`).
- 곡 제목 앞의 `파일이름 : ` 접두는 관례. 공백 정렬이 묶음마다 다르다 → 비교할 때 공백을 하나로.

### 3.7 `<gamelist date="…">`

`YYYY/MM/DD` (1,095) · 자리 표시 `200x/xx/xx` 꼴 3개. 합칠 때 = 합친 파일에 들어간 항목들의 날짜 중 가장 큰 것 (문자열 비교).

### 3.8 `<bind>` (hoot.xml에만)

확장자 → 드라이버 연결. 정리 대상 아님, 그대로 둔다.

```xml
	<bind>
		<exts><ext>kss</ext></exts>
		<driver type="kss">msx</driver>
		<options><option name="ay_mix" value="0x5a"/></options>
	</bind>
```

## 4. 주석 — 꼭 처리해야 함

목록의 1/3쯤이 주석을 쓴다 (파일 1,209개). 세 가지 경우:

1. **항목 밖 구분 주석** (`<!-- ===== A ===== -->`) — 버린다 (또는 그대로).
2. **항목 전체를 주석으로 끔** (`<!-- <game> … </game> -->`) — **꺼 둔 항목이다. 읽지 않는다.**
3. **항목 안 주석** 1,094곳 — 대부분 곡 줄을 끔 (`<!-- <title code="0x18">…</title> -->`).
   주석이 `</game>`을 넘어 이어지는 경우가 있다 (목록 일부를 통째로 막음) → 주석이 끝난 뒤의 `</game>`까지를 한 항목으로.

그래서 `<game>.*?</game>` 정규식만으로 자르면 틀린다. 지금 구현의 자르기 (`HootLists.GameBlocksAt`):

```
p = 0
loop:
  c = indexOf("<!--", p);  g = indexOf("<game>", p)
  if g < 0: break
  if c >= 0 && c < g: p = indexOf("-->", c+4) + 3; continue      # 항목 밖 주석 (꺼 둔 항목 포함) 건너뜀
  q = g + 6
  loop:                                                           # 항목 끝 찾기: 안쪽 주석은 건너뜀
    c2 = indexOf("<!--", q);  e2 = indexOf("</game>", q)
    if c2 >= 0 && c2 < e2: q = indexOf("-->", c2+4) + 3; continue
    end = e2 + 7; break
  yield [g, end);  p = end
```

Shift_JIS 바이트에서 그대로 찾아도 안전하다: `<` `>` `!` `-` `/`(0x21~0x3E)는 2바이트 글자의 둘째 바이트(0x40~0xFC)가 될 수 없다.
→ 파일을 Latin-1(바이트 = 글자)로 읽어 위치를 찾고, 그 자리 바이트를 그대로 잘라 둔다.

## 5. 숫자 읽기 (공통)

```
"0x1F" / "0X1f" → 16진,  "-1" / "31" → 10진,  그 밖 → 문자열 그대로 비교
```

옵션 값 비교(`NormNum`)도 같은 규칙: `0x04` = `4`.

## 6. 망가진 항목

- 이스케이프 안 된 `&` 등으로 XML 파서가 실패하는 항목이 있다. **버리지 말 것**: 정규식으로 `<name>` · `archive`만이라도 읽고, 원래 바이트는 그대로 출력에 넣는다.
- 항목 하나씩 파서에 넣는다 (파일 전체를 한 번에 파싱하면 한 군데 오류로 파일 전체를 잃는다).

## 7. 칩 이름 정규화

이름 괄호 중 칩으로 볼 것 · 같은 칩끼리 같은 이름으로 (`HootLists.ChipAlias` · `ChipWord` · `NormChip`).

| 별명 | 정규 | 별명 | 정규 |
|---|---|---|---|
| OPN | YM2203 | OPLL · FM-PAC · FMPAC · MSX-MUSIC | YM2413 |
| OPNA · SPB · Speak Board · PC-9801-86 · 86 | YM2608 (+보드 이름) | MSX-AUDIO | Y8950 |
| OPNB | YM2610 | PSG · SSG · AY8910 · AY-3-8912 · YM2149 | AY-3-8910 |
| OPN2 · OPNC | YM2612 | SN76496 · SN76489A · Tandy | SN76489 |
| OPM | YM2151 | MSM · ADPCM | MSM6258 |
| OPP | YM2164 | SCC / SCC+ | K051649 / K052539 |
| OPL / OPL2 / OPL3 / OPL4 | YM3526 / YM3812 / YMF262 / YMF278B | SC55 · SC-55mk2 | SC-55 |
| MT32 · MT-32 Emulation · MUNT | MT-32 | TG100 | TG-100 |

- 칩 이름은 `+ , / : & and with`로 나눈 낱말 집합 → 정렬해 `+`로 잇는다 (`OPN+MT-32 Emulation` → `MT-32+YM2203`).
- 끝말 `EMULATION · SIMULATION · EMU · SIM`은 뗀다.
- 낱말 하나라도 `ChipWord` 무늬(YM…, AY…, SN7…, MSM…, OP…, PSG, GS, GM, MIDI, BEEP, PCM, SC-55, MT-32, 2A03 …)에 맞으면 칩 괄호. 일본어가 든 괄호는 칩이 아님.
- 칩 괄호 **뒤의** 괄호는 만든 이 (`(OPNA) (Firecracker)`).

## 8. 같은 항목 판단 (지금 구현)

여러 묶음에서 온 항목들을 "같은 게임 · 같은 판"끼리 묶는다.

1. **archive로 나눈다** (소문자). archive가 없으면 `이름 바탕 + 이름`으로.
2. 같은 archive 안에서 두 항목 a · b를 **함께 둘 수 있는가 `CanJoin`**:
   - 원래 글자가 완전히 같으면 → 예.
   - **같은 묶음의 같은 파일이면 → 아니오** (한 파일 안 두 항목은 일부러 나눈 판: OPLL / PSG, 데모 / 정식 …).
   - `StructKey`가 같아야 함 = `archive | driver | driver type | midiout | midiout_type | subtype` (옵션 값은 숫자 정규화).
   - 칩이 맞아야 함: 같거나, 한쪽이 없거나, 한쪽 집합이 다른 쪽에 포함 (`MT-32` ⊂ `MT-32+YM2151`).
   - `*_mix` · `clockmul` 등 나머지 옵션은 달라도 된다.
3. **근거 점수 `MatchScore`** (CanJoin이 참일 때):
   - 이름 닮음 n: 바탕 키(영문 소문자 · 숫자만)가 같으면 1, 아니면 낱말 Jaccard (불용어 `the a of no and version ver vol disk music edition` 뺌; 일본어만 있는 이름은 0).
   - 곡 겹침 t: title code 집합의 Jaccard.
   - 받아들임: `n ≥ 0.999` 또는 `t ≥ 0.8` 또는 (`n ≥ 0.5` 이고 (`t ≥ 0.5` 또는 한쪽에 곡 없음)).
   - 점수 = `2n + t + 0.01`.
4. **점수 높은 짝부터 합친다** (union). 합칠 때 두 묶음의 모든 짝이 CanJoin이어야 한다
   → "Last Armageddon"은 "Last Armageddon"과, "… Demo"는 "… Demo"와 짝이 된다.
5. **근거가 약한 남은 짝**: 함께 둘 수 있고, 서로에게 유일한 후보이며, 겹치는 묶음(set)이 없으면 합치고 `Uncertain`(확인 권장) 표시.
6. 그래도 따로 남았지만 CanJoin인 묶음끼리는 `Similar`(비슷함, 사람이 확인)로 서로 걸어 둔다.

지금 결과 (xml · xml2 · xml3): 15,457 → 9,494 (중복 5,963 뺌), 확인 권장 9 · 비슷함 1쌍.

## 9. 남길 것 고르기 · 출력

**남길 항목** (`Score`, 큰 것이 이김 — 비트 자리로 우선순위):

1. 곡 목록이 있음
2. 칩 괄호가 있음 · 일본어 제목이 있음 (새 이름 형식)
3. 새 묶음 (set 순위)
4. 곡 수 → 5. 옵션 수

단, 다른 항목의 곡 목록이 **1.5배 이상이고 5곡 넘게 많으면** 그것을 남긴다 (`PickBest`).

**출력 파일** (`Merge`):

- 출력 파일 이름 = 남긴 항목이 있던 제작사 파일. 밑줄 · 붙임표 · 공백만 다른 이름은 하나로 (가장 많이 쓰인 이름, 같으면 밑줄 있는 쪽).
- 머리말 = 가장 새 묶음 첫 파일의 `<gamelist` 앞부분, `encoding="Shift_JIS"`로 고침 (없으면 기본 머리말).
- `<gamelist date="최대 날짜">` CRLF → 각 항목: `\t` + **원래 바이트** (LF만 있으면 CRLF로) + CRLF → `</gamelist>` CRLF.
- UTF-8 파일에서 온 항목은 CP932로 바꿔 쓰고, CP932로 못 쓰는 글자가 있으면 `?`로 + 보고서에 수를 적는다.
- 출력 폴더의 옛 `*.xml`은 먼저 지운다. `hoot.xsl · drivers.xsl · hoot.css`는 가장 새 묶음에서 복사.
- `_report.txt` (UTF-8 BOM): 요약 · 비슷함 쌍 · 확인 권장 · 뺀 중복 (`남김 ← 뺌` 목록).
- `hoot.xml`이 주어지면 `hoot_new.xml`: `^\s*<list>\s*xml\d*/…</list>\s*$` 줄을 모두 빼고, 처음 있던 자리에 `<list>출력폴더/파일</list>`를 이름순으로 넣음 (CP932, CRLF).

## 10. 정리 코드가 지켜야 할 것 (요약 체크리스트)

- [ ] CP932로 읽고, **항목은 원래 바이트 그대로** 쓴다 (왕복 변환 금지). UTF-8 파일 2개 대비.
- [ ] 주석 처리 (4장): 꺼 둔 항목은 빼고, `</game>`을 넘는 주석을 한 항목으로.
- [ ] DTD에 없는 요소 · 속성 (`range`, `extcode`, `archiver`)과 처음 보는 옵션 · rom type을 잃지 않는다.
- [ ] 망가진 항목도 버리지 않는다 (6장).
- [ ] 같은 파일 안 두 항목은 합치지 않는다.
- [ ] 장치 옵션(`midiout` · `midiout_type` · `subtype`)이 다르면 다른 판.
- [ ] 결과를 `hoot_new.xml`로 따로 쓰고, 원본 `hoot.xml` · 묶음 폴더는 건드리지 않는다.
- [ ] 보고서로 사람이 확인할 것(확인 권장 · 비슷함 · 경고)을 남긴다.

정리 코드에서 더 해 볼 만한 것 (지금 구현에 없음):

- 곡 코드를 **숫자로** 비교 (`0x0A` = `10`), 같은 항목 안 중복 code 경고 (337항목).
- `archiver` → `archive`, `adressing` → `addressing` 같은 오타 경고 (고쳐 쓰는 것은 hoot 동작이 바뀔 수 있어 선택).
- 쉼표 archive (`a,b`)는 첫 zip을 열쇠로 할지, 집합으로 볼지 결정.
- 기계 이름 · `driveralias type` 표기 통일 (선택: 원래 바이트 보존 원칙과 부딪힘 → 하려면 그 항목만 다시 쓴다).
- archive zip이 기종 폴더에 실제로 있는지 · rom 파일이 zip 안에 있는지 확인 (`HootArchive.MissingRom` 참고).

## 11. 참고 구현 · 검증

| 무엇 | 어디 |
|---|---|
| 읽기 · 자르기 · 이름 풀기 · 묶기 · 합치기 · 보고서 | `src/AdlibComposer.Formats/HootLists.cs` |
| 항목 하나 해석 (재생용: code 숫자 읽기, range 곡 목록, `RangeTitle`) | `src/AdlibComposer.Formats/HootArchive.cs` |
| 비교 · 합치기 UI | `src/AdlibComposer.App/Views/HootCompareForm.cs` |
| 시험 | `tests/AdlibComposer.SelfTest/HootTests.cs` (`-- --test hoot`) |
| 명령 줄 합치기 | SelfTest `Program.cs` (`HootLists.Merge(dirs, outDir, hootXml)`) |

새 코드의 검증 기준: 같은 입력(xml · xml2 · xml3 순서)으로 지금 구현과 **항목 수 9,494 · 파일 577개**가 같은지,
다르면 `_report.txt`끼리 비교해 어느 판단이 바뀌었는지 설명할 수 있어야 한다.
`xml_new/`를 hoot에 넣어 목록이 열리는지 (XSL 표시 포함) 마지막으로 확인.
