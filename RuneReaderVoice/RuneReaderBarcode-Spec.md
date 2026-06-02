# RuneReader Barcode Font — Technical Specification

**Font file:** `RuneReaderBarcode-Regular.ttf`  
**Version:** 3.4 / v10 advance-250, 50/200/50 guards  
**Copyright:** Tanstaafl Gaming — GPL v3

---

## Overview

RuneReader Barcode (RRVB) is a machine-readable font-based 2D barcode symbology. Each rendered payload character encodes one Latin-1 byte as 8 horizontal black-bar lanes inside a guarded glyph cell.

v10 is the current working WoW-rendered baseline. It keeps the reliable 50-em guard bars and 200-em data zone, but sets the glyph advance to 250 em while the glyph outline draws to 300 em. Adjacent glyphs therefore overlap their boundary guards exactly, removing the visible inter-glyph gaps seen in WoW while preserving strong guard bars.

The symbology is adaptive. The decoder does not assume a fixed screen pixel size; it derives rows, guard spans, data gaps, and lane slots from rendered geometry.

Obsolete designs are intentionally not part of this spec:

- Code39 side-channel
- RRV2 / BarcodeV2 font
- 13-lane v2 layout
- Empty-cell `§` marker from v8/v9
- Detector prefix filtering
- Old lane/block decoder fallback

---

## Glyph Anatomy

### Data Glyph

Every data glyph has this logical layout:

```text
┌────────┬───────────────────────┬────────┐
│ Guard  │      Data Zone        │ Guard  │
│  Bar   │  (8 horizontal lanes) │  Bar   │
└────────┴───────────────────────┴────────┘
```

v10 font geometry:

```text
advance width = 250 em
drawn width   = 300 em

x=0..50       left full-height guard
x=50..250     200-em data zone
x=250..300    right full-height guard
```

Adjacent glyphs start 250 em apart, so the previous right guard and next left guard occupy the same `x=250..300` space. The visual internal boundary remains one solid 50-em guard span.

```text
Glyph A right guard: 250..300
Glyph B left guard:  250..300
Merged boundary:     250..300 = 50 em
```

This intentional overlap is required for WoW FontString rendering. A 250-em fully-contained glyph with 25-em guards produced visible spacing between glyphs in WoW even though it rendered correctly in a browser.

---

## Data Zone and Bit Order

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

Each lane occupies an equal vertical slot. The black lane bar occupies roughly the upper half of the slot. The implemented decoder handles WoW raster jitter by searching for the strongest 1-pixel horizontal row inside each lane slot instead of relying on a fixed y-band.

---

## Guard Bars

Every payload glyph has a left and right full-height vertical guard bar.

Guard bars serve as:

- glyph boundary / clock signal
- row height estimator
- data-cell bounds
- wrap-safe row start and row end anchors

In v10, internal guard bars are expected to be strong and visually continuous. Edge guards and internal guards use the same 50-em stroke geometry; internal guards overlap exactly because the glyph advance is smaller than the drawn width.

---

## Start/Stop Glyph — `§` (U+00A7)

The start/stop glyph is a sentinel, not an empty data cell.

Current v10 sentinel geometry:

```text
advance width = 250 em
drawn width   = 300 em

x=0..50       full-height left bar
x=125..175    full-height center bar
x=250..300    full-height right bar
```

Visual model:

```text
|   |   |
```

The sentinel emits no data byte. A decoder identifies a marker by detecting three full-height vertical bars with two smaller marker gaps. Payload codepoint U+00A7 is reserved and must not appear in payload text.

---

## Character Coverage

- Supported range: **U+0020 – U+00FF** (ISO 8859-1 / Latin-1)
- U+00A7 (`§`) is reserved as start/stop marker
- Characters outside U+0020–U+00FF have no valid glyph and will corrupt the barcode
- Each payload glyph encodes the Latin-1 byte value of its codepoint directly

---

## String Format

```text
§<payload>§
```

No length prefix, checksum, CRC, or escape sequence is part of the symbology.

The current RuneReaderVoice identity side-channel payload is an application convention, not part of the RRVB font format:

```text
§RRVX-G=<unit-guid>;N=<npc-name>§
```

The RRVB detector must decode raw Latin-1 text generically. Application-level code owns `RRVX`, `G=`, and `N=` parsing and validation.

---

## Rendering Requirements

| Property | Requirement |
|----------|-------------|
| Font | `Interface\\AddOns\\RuneReaderVoice\\Fonts\\RuneReaderBarcode-Regular.ttf` |
| Current WoW font size | 13pt target |
| Kerning | Must be disabled / not used |
| Ligatures | Must not apply |
| Word wrap | May wrap only at glyph boundaries |
| Shadow / outline | Must be disabled |
| Text color | Black |
| Background/panel | Solid light panel; framed/enclosed for region finding |
| Barcode panel inset | Current addon uses 5 px inset |
| Line spacing | Current addon uses 2 px when wrapped |
| Wrap width | Current addon uses 100 px |
| Recommended minimum | 13pt for reliable automatic decode |

13pt is the current reliable WoW-rendered size. Smaller sizes can still be visually/manual-decodable, but become fragile because each lane may only occupy a few screen pixels.

WoW may cache fonts. Reload the game after replacing the TTF.

---

## Region Acquisition Model (Desktop Monitor)

The monitor finds the barcode panel before the detector decodes glyphs.

Current model:

1. Convert frame to grayscale.
2. Threshold using RRVB value **10** with `ThresholdTypes.BinaryInv`.
   - Dark barcode/world pixels become white foreground in `inkMask`.
   - Light panel/background becomes black.
3. Invert `inkMask` to create `panelMask`.
   - Light barcode panel becomes white foreground.
4. Flood-fill all white regions connected to the image edge to black.
   - This removes outside/world light regions.
   - Enclosed white islands remain as candidate panels.
5. Run `ConnectedComponentsWithStats` on `panelMask` using `PixelConnectivity.Connectivity4`.
6. Validate each panel by checking for barcode-like ink inside the same rect using `inkMask`.
7. Return the exact panel rect. No padding is added.

This avoids the old failure mode where dark world/UI junk connected to barcode ink and produced oversized crops.

---

## Decoder Model

The detector receives a clean panel crop and decodes raw RRVB text.

Current model:

1. Convert crop to grayscale.
2. Threshold using RRVB value **10** with `ThresholdTypes.BinaryInv`.
3. Build a vertical guard mask and find full-height vertical bar spans.
4. Group guard spans into rows by Y-overlap.
5. Sort rows top-to-bottom; sort bars left-to-right within each row.
6. Find a start marker by detecting the `§` sentinel triplet.
7. Decode row-major cells after the start marker until the stop marker.
8. For data cells:
   - Data gap is between adjacent guard spans.
   - Sample the center of the data gap to avoid guard bleed.
   - Divide row height into 8 relative lane slots.
   - For each lane, search for the strongest 1-pixel horizontal row inside the slot.
   - Bit = 1 when peak ink across the sampled X columns meets the configured fraction.
9. Decode bytes as Latin-1.
10. Stop frame scanning after the first closed RRVB block is decoded.

The barcode reader is generic. It must not filter application prefixes such as `RRVG-`, `RRVN-`, or `RRVX-`; caller/client logic owns that.

---

## Font Design Constants (v10)

| Parameter | Em Units | Notes |
|-----------|----------|-------|
| Em square | 1000 | |
| Glyph advance width | 250 | WoW-rendered glyph start distance |
| Drawn glyph width | 300 | Intentional 50-em right overhang |
| Left guard width | 50 | Full-height |
| Data zone width | 200 | 8 horizontal data lanes |
| Right guard width | 50 | Full-height; overlaps next glyph left guard |
| Marker center bar | 50 | `x=125..175` |
| Lane slot height | 125 | 1000 ÷ 8 |
| Black bar height per slot | 62 | ~50% of slot height |
| White gap height per slot | 63 | ~50% of slot height |
| Bit order | MSB top | Lane 0 = bit 7, lane 7 = bit 0 |
| Binary threshold | 10 | RRVB detector/monitor threshold; lower avoids antialias/shadow pickup |

TTF name table for this baseline:

```text
Version 3.4; RuneReaderBarcode v10 advance 250, 50/200/50 guards
```

---

## Rectangular / Wrapped Barcode

v10 is designed for wrapped WoW FontStrings.

Because every glyph has both left and right guards:

- first glyph on a wrapped row has a left guard
- last glyph on a wrapped row has a right guard
- continuation rows do not require inferred leading/trailing cells
- decoder processes rows top-to-bottom until the stop marker

Rows should have visible vertical separation. Current addon settings use a fixed wrap height with up to 4 wrapped rows and 2 px line spacing.

---

## Debug / Diagnostics

RRVB image dumps and verbose traces exist only for troubleshooting and are disabled by default.

Current detector flags:

```csharp
private const bool DebugDumpImages = false;
private const bool DebugTraceEnabled = false;
```

Current monitor flag:

```csharp
private const bool RrvbDebugTraceEnabled = false;
```

When enabled, debug images are written under:

```text
<AppContext.BaseDirectory>\\rrvb-debug
```

---

## Known Limitations

| Limitation | Detail |
|------------|--------|
| No checksum | Application must validate payload if corruption matters |
| No error correction | Single bad lane can produce wrong byte |
| U+00A7 reserved | Cannot appear in payload |
| Latin-1 only | Payload must fit U+0020–U+00FF |
| Very small font sizes | 10–12pt may be visually readable but less reliable for automatic decode |
| Renderer-specific behavior | v10 geometry is tuned for WoW FontString rendering |
| Required panel framing | Current monitor expects the light barcode panel to be enclosed so flood-fill can isolate it |
