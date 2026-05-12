# RuneReader Barcode Font — Technical Specification

**Font file:** `RuneReaderBarcode-Regular.ttf`  
**Version:** 1.0  
**Copyright:** Tanstaafl Gaming — GPL v3

---

## Overview

RuneReader Barcode is a machine-readable font-based 2D barcode symbology. Each rendered character encodes one byte of payload data as a pattern of horizontal black bars inside a fixed cell. A full-height vertical guard bar on the right edge of every glyph acts as a glyph clock signal and boundary marker. Start and stop positions are marked by a special double guard bar glyph containing no data zone.

The font is a standard TrueType file. Any application capable of rendering a TrueType font can produce a barcode — no special barcode library is required on the encoding side. Decoding requires capturing the rendered image and applying geometric sampling.

The symbology is adaptive: no fixed pixel size is assumed. The decoder derives all dimensions from the geometry of the rendered image, making it functional across a wide range of font sizes and display resolutions.

---

## Glyph Anatomy

Every data glyph consists of two zones rendered left to right:

```
┌───────────────────────┬────────┐
│      Data Zone        │ Guard  │
│  (8 horizontal lanes) │  Bar   │
└───────────────────────┴────────┘
```

### Data Zone

Contains 8 horizontal bar lanes stacked top to bottom. Each lane encodes one bit of the byte value:

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

A **black bar** in a lane = bit **1**.  
A **white gap** in a lane = bit **0**.

Each lane occupies an equal vertical slot. Within each slot the black bar occupies approximately the top 50% of the slot height, with a white gap below. The decoder must not assume a fixed ratio — sample at the vertical midpoint of the expected slot region.

### Guard Bar

A solid full-height black vertical bar at the right edge of every glyph. Height spans the full em square. Width is approximately 23% of total glyph advance width.

The guard bar serves as:
- Glyph boundary / clock signal
- Right-edge anchor for data zone width calculation
- Row height estimator — guard bar pixel height equals the rendered em height at the current font size

Guard bar width and data zone width are **not fixed pixel values**. They scale with font size. The decoder derives all dimensions geometrically from the captured image.

### Start/Stop Glyph — `§` (U+00A7)

The start/stop glyph has no data lanes. It consists of a left guard bar, an empty data zone (no lane ink), and a right guard bar — identical in layout to a data glyph encoding byte `0x00`, except the presence of the left guard bar distinguishes it:

```
┌────────┬───────────────────────┬────────┐
│ Guard  │    Empty Data Zone    │ Guard  │
│  Bar   │   (no lane ink)       │  Bar   │
└────────┴───────────────────────┴────────┘
```

Visually: `| |` — two vertical bars with a gap between them.

Advance width is the same as a normal data glyph. The decoder identifies a start/stop marker by detecting an **empty cell** between two guard bars — a data zone containing no lane ink. A normal data glyph always has at least some lane ink unless encoding `0x00`, but `0x00` is a valid payload byte. The left guard bar at the start of the cell is the definitive discriminator: it is a full-height solid bar, not a sparse lane pattern.

The decoder detects this by sampling a column near the left edge of the data zone — if that column has guard-bar-density ink (full-height solid), the cell is a start/stop marker.

---

## Character Coverage

- Supported range: **U+0020 – U+00FF** (ISO 8859-1 / Latin-1)
- U+00A7 (`§`) is **reserved** as the start/stop marker — must not appear in payload
- Characters outside U+0020–U+00FF have no glyph and will render as `.notdef`, corrupting the barcode
- Each glyph encodes the Latin-1 byte value of its codepoint directly — no lookup table required

---

## String Format

A barcode string is:

```
§<payload>§
```

- `§` (U+00A7) marks both start and stop — identical to Code 39's use of `*`
- Payload is the raw string to encode, characters U+0020–U+00FF only, U+00A7 excluded
- No length prefix, no checksum, no escape sequences

### Example

To encode the string `Hello`:

```
§Hello§
```

Rendered with this font at any point size on a solid background, this produces a scannable barcode.

---

## Rendering Requirements

| Property | Requirement |
|----------|-------------|
| Kerning | Must be disabled |
| Ligatures | Must not apply |
| Word wrap | Must not break within the payload |
| Shadow / outline | Must be disabled |
| Text color | Black |
| Background | Solid, high contrast against black (light gray, brown, white, etc.) |
| Minimum font size | 12pt minimum; 16pt recommended |

The font has no kern table. If the host renderer applies automatic kerning or ligature substitution, glyph advance widths become non-uniform and guard bar spacing will be inconsistent.

---

## Decoder Implementation

### Step 1 — Capture and Binarize

Capture the region containing the rendered barcode. Convert to grayscale. Apply a threshold to produce a binary image where ink pixels are 1 and background pixels are 0.

```
gray   = toGrayscale(capturedImage)
binary = threshold(gray, threshold=128, mode=BINARY_INV)
```

Threshold value may need tuning per background color. A solid background makes this straightforward.

### Step 2 — Find Guard Bars

Scan the binary image for vertical columns that are predominantly black. A guard bar is a contiguous group of columns where each column contains black pixels spanning ≥ 80% of the row height.

```
for x in 0..imageWidth:
    blackCount = countBlackPixelsInColumn(binary, x)
    if blackCount >= 0.80 * rowHeight:
        mark column x as candidate

guardBars = groupAdjacentCandidateColumns()
// Each group: { xStart, xEnd, height }
```

### Step 3 — Estimate Row Height

Row height = median height of all detected guard bar spans. Because guard bars span the full em square, their pixel height directly equals the rendered em height.

### Step 4 — Identify Glyphs

Process guard bar spans left to right. For each gap between adjacent guard bars, examine the data zone:

```
for each pair (leftGuard, rightGuard) in guardBars:
    dataZone = region between leftGuard.xEnd and rightGuard.xStart

    // Sample a column near the left edge of the data zone
    sampleCol = dataZone.xStart + (dataZone.width * 0.10)
    leftEdgeInk = countBlackPixelsInColumn(binary, sampleCol)

    if leftEdgeInk >= guardBarDensityThreshold:
        // Left edge has full-height solid ink = left guard bar of start/stop glyph
        emit START_STOP
    else:
        // Sparse left edge = data lanes
        emit DATA_GLYPH(dataZone)
```

The start/stop marker is identified by the presence of a full-height left guard bar at the start of the data zone — not by gap width between consecutive guard bars.

### Step 5 — Sample Lane Bits

For each data glyph zone, sample 8 Y positions relative to detected row height:

```
for lane in 0..7:
    slotHeight = rowHeight / 8.0
    sampleY    = rowTop + (lane * slotHeight) + (slotHeight * 0.25)
    sampleX    = dataZone.xStart + (dataZone.width * 0.5)
    bit[lane]  = isBlack(binary, sampleX, sampleY) ? 1 : 0

byteValue = 0
for lane in 0..7:
    byteValue |= bit[lane] << (7 - lane)
// lane 0 = bit 7 (MSB), lane 7 = bit 0 (LSB)
```

### Step 6 — Extract Payload

```
state   = SEEKING_START
payload = []

for each decoded token:
    if token == START_STOP:
        if state == SEEKING_START:
            state = IN_PAYLOAD
        else if state == IN_PAYLOAD:
            break  // stop marker reached
    else if state == IN_PAYLOAD:
        payload.append(token.byteValue)

result = Latin1Decode(payload)
```

---

## Rectangular / Wrapped Barcode

A barcode string may be rendered across multiple lines if the host text container wraps at a fixed width. This allows longer payloads to occupy a compact rectangular screen area rather than a single long horizontal strip.

### Rendering

Set a fixed pixel width on the text container and let the renderer wrap the string visually. Do not insert manual newlines into the payload — they are not part of the format and will decode as corrupt data bytes.

The renderer must wrap at glyph boundaries, not at word boundaries. Verify this behavior for your specific rendering environment before relying on rectangular mode.

### Reading a Wrapped Barcode

Treat the rendered rectangle as rows read top to bottom, left to right within each row.

1. Detect all guard bars across the full captured image
2. **Group guard bars into rows** by Y overlap — two guard bars belong to the same row if their vertical spans overlap by ≥ 50%
3. Sort rows top to bottom by median Y coordinate
4. Within each row, sort guard bars left to right
5. Process glyphs in row-major order: row 0 left→right, row 1 left→right, and so on
6. Start double-guard will appear in the first row near the left edge
7. Stop double-guard will appear wherever the payload ends — not necessarily at the right edge of the last row
8. Discard any content after the stop marker

### Wrapping Limitations

- The renderer must wrap at glyph boundaries, not word/space boundaries. Behavior varies by rendering engine and must be verified
- A glyph split across a line wrap will decode incorrectly
- Rows must not overlap vertically — line spacing must be ≥ em height
- No explicit row markers exist in the format; row detection relies entirely on guard bar Y-overlap grouping

---

## Known Limitations

| Limitation | Detail |
|------------|--------|
| No checksum | The format contains no checksum or CRC. Corrupt decodes cannot be detected unless the application can validate the payload through other means |
| No error correction | A single corrupt lane produces a wrong byte with no recovery |
| U+00A7 reserved | The `§` character cannot appear in the payload. Payloads that may contain this character must escape or substitute it before encoding |
| Latin-1 only | Characters above U+00FF are not supported. Payloads must be expressible in ISO 8859-1 |
| No multi-byte encoding | Each glyph = one codepoint = one Latin-1 byte. UTF-8 multi-byte sequences are not handled |
| Antialiasing | At very small font sizes lane separation may be lost under font antialiasing. Confirmed working at 12pt minimum |
| Kerning / ligatures | Kerning or ligature substitution by the host renderer will corrupt glyph boundary detection |
| Wrap behavior | Rectangular mode depends on the renderer wrapping at glyph boundaries — not guaranteed by all engines |
| Capture noise | Lossy capture pipelines may introduce pixel noise near lane edges requiring threshold tuning |

---

## Font Design Constants (v6)

| Parameter | Em Units | Notes |
|-----------|----------|-------|
| Em square | 1000 | |
| Data glyph advance width | 260 | |
| Start/stop glyph advance width | 260 | Same as data glyph — left guard + empty zone + right guard |
| Guard bar width | 60 | 23% of data glyph advance width |
| Data zone width | 200 | 77% of data glyph advance width |
| Lane slot height | 125 | 1000 ÷ 8 |
| Black bar height per slot | 62 | ~50% of slot height |
| White gap height per slot | 63 | ~50% of slot height |
| Bit order | MSB top | Lane 0 = bit 7, lane 7 = bit 0 |

---

## Appendix — Sample Byte Encodings

| Character | Codepoint | Byte | Binary |
|-----------|-----------|------|--------|
| Space | U+0020 | 0x20 | 00100000 |
| `A` | U+0041 | 0x41 | 01000001 |
| `a` | U+0061 | 0x61 | 01100001 |
| `0` | U+0030 | 0x30 | 00110000 |
| `é` | U+00E9 | 0xE9 | 11101001 |
| `ñ` | U+00F1 | 0xF1 | 11110001 |
| `§` | U+00A7 | 0xA7 | **reserved — start/stop only** |
