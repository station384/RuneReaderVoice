# RuneReader Barcode Font — Technical Specification

**Font file:** `RuneReaderBarcode-Regular.ttf`  
**Version:** 3.2 / v8 bounded wider guards  
**Copyright:** Tanstaafl Gaming — GPL v3

---

## Overview

RuneReader Barcode is a machine-readable font-based 2D barcode symbology. Each rendered character encodes one Latin-1 byte as a pattern of horizontal black bars inside a fixed cell.

v8 changes glyph geometry to make wrapped / rectangular barcodes reliable. Every payload glyph is bounded by full-height vertical guard bars on both sides:

```text
| data |
```

The start/stop glyph uses the same bounded cell, but its data zone is empty:

```text
| empty |
```

This removes the v6/v7 wrap-seam ambiguity where either the first or last glyph on a wrapped row had to be inferred from row/block geometry.

The symbology is adaptive: no fixed pixel size is assumed. The decoder derives dimensions from rendered guard-bar geometry.

---

## Glyph Anatomy

### Data Glyph

Every data glyph consists of three zones rendered left to right:

```text
┌────────┬───────────────────────┬────────┐
│ Guard  │      Data Zone        │ Guard  │
│  Bar   │  (8 horizontal lanes) │  Bar   │
└────────┴───────────────────────┴────────┘
```

### Data Zone

The data zone contains 8 horizontal lanes stacked top to bottom. Each lane encodes one bit of the byte value:

| Lane | Bit |
|------|-----|
| 0 (top) | Bit 7 (MSB) |
| 1 | Bit 6 |
| 2 | Bit 5 |
| 3 | Bit 4 |
| 4 | Bit 3 |
| 5 | Bit 2 |
| 6 | Bit 1 |
| 7 (bottom) | Bit 0 (LSB) |

A black bar in a lane = bit **1**.  
A white gap in a lane = bit **0**.

Each lane occupies an equal vertical slot. The black lane bar occupies roughly the upper half of the slot. A decoder should sample or scan the upper-middle of each lane slot, not the lower gap.

### Guard Bars

Every glyph has a left and right full-height vertical guard bar.

Guard bars serve as:
- glyph boundary / clock signal
- row height estimator
- data-cell bounds
- wrap-safe row start and row end anchors

Adjacent glyphs may visually merge neighboring guards into a wider shared guard span. This is expected:

```text
| data || data |
```

The decoder must treat guard spans as boundaries and decode the cells between adjacent guard spans.

---

## Start/Stop Glyph — `§` (U+00A7)

The start/stop glyph has no data lanes. It consists of:

```text
| empty |
```

A decoder identifies a start/stop marker as a data cell between adjacent guard spans that contains no lane ink.

Payload codepoint U+00A7 is reserved and must not appear in payload text.

---

## Character Coverage

- Supported range: **U+0020 – U+00FF** (ISO 8859-1 / Latin-1)
- U+00A7 (`§`) is reserved as start/stop marker
- Characters outside U+0020–U+00FF have no valid glyph and will corrupt the barcode
- Each glyph encodes the Latin-1 byte value of its codepoint directly

---

## String Format

```text
§<payload>§
```

No length prefix, checksum, CRC, or escape sequence is part of the symbology.

---

## Rendering Requirements

| Property | Requirement |
|----------|-------------|
| Kerning | Must be disabled |
| Ligatures | Must not apply |
| Word wrap | May wrap only at glyph boundaries |
| Shadow / outline | Must be disabled |
| Text color | Black |
| Background | Solid, high contrast against black |
| Quiet zone | 1+ px top/bottom, 10+ px left/right preferred |
| Minimum font size | 12pt currently targeted |

WoW may cache fonts. Reload the game after replacing the TTF.

---

## Decoder Model

1. Capture and threshold to black ink on white background.
2. Find full-height vertical guard spans using vertical black runs.
3. Group guard spans into rows by Y-overlap.
4. Sort rows top-to-bottom; sort guards left-to-right within each row.
5. Decode row-major cells between adjacent guard spans.
6. Empty cell = start/stop marker.
7. Non-empty cell = byte; scan each lane band for ink.
8. Stop marker may appear on a later wrapped row.
9. Decode payload bytes as Latin-1.

The barcode reader is generic. It must not filter application prefixes like `RRVG-` or `RRVN-`; caller/client logic owns that.

---

## Rectangular / Wrapped Barcode

v8 is designed for wrapped FontStrings.

Because every glyph has both left and right guards:
- first glyph on a wrapped row has a left guard
- last glyph on a wrapped row has a right guard
- continuation rows do not require inferred leading/trailing cells
- the decoder can process cells row-major until stop marker

Rows should have visible vertical separation. A 1 px quiet gap can work, but 2–10 px is safer under antialiasing.

---

## Font Design Constants (v8 bounded wider guards)

| Parameter | Em Units | Notes |
|-----------|----------|-------|
| Em square | 1000 | |
| Glyph advance width | 300 | Wider than v6/v7 for thicker guards |
| Left guard width | 50 | Wider guard for better low-size rendering |
| Data zone width | 200 | Same data width as v6/v7 |
| Right guard width | 50 | Wider guard for better low-size rendering |
| Lane slot height | 125 | 1000 ÷ 8 |
| Black bar height per slot | 62 | ~50% of slot height |
| White gap height per slot | 63 | ~50% of slot height |
| Bit order | MSB top | Lane 0 = bit 7, lane 7 = bit 0 |

This variant was created because 30-unit guards proved too thin at 12pt on the current render path. If 50-unit guards are still not robust enough, next fallback should be:

```text
guard 40 + data 200 + guard 40 = advance 280
```

If density matters more than guard robustness, 40/200/40 is likely next compromise. If robustness matters more, stay on 50/200/50.

---

## Known Limitations

| Limitation | Detail |
|------------|--------|
| No checksum | Application must validate payload if corruption matters |
| No error correction | Single bad lane produces wrong byte |
| U+00A7 reserved | Cannot appear in payload |
| Latin-1 only | Payload must fit U+0020–U+00FF |
| Antialiasing | Very small font sizes may blur lanes/guards |
| Renderer wrap behavior | Must wrap at glyph boundaries, not split glyphs |
