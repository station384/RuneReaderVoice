# RuneReaderBarcode v10 test font

Purpose: test whether WoW will render overlapping glyph outlines when glyph advance is smaller than drawn glyph width.

## Geometry

- Units per em: 1000
- Glyph advance: 250 em
- Drawn glyph width: 300 em
- Left guard: x=0..50
- Data zone: x=50..250 (200 em)
- Right guard: x=250..300
- Horizontal overhang: 50 em right side

Adjacent glyphs at advance 250:

- Glyph A right guard: x=250..300
- Glyph B left guard: x=250..300
- Shared boundary guard: 50 em, overlapped exactly

## Marker

U+00A7 (§) is sentinel marker:

- left full-height guard x=0..50
- center full-height marker x=125..175
- right full-height guard x=250..300

## Data encoding

- Direct Latin-1 byte encoding for U+0020..U+00FF
- U+00A7 reserved as start/stop marker
- 8 horizontal lanes, MSB top
- Lane slot height: 125 em
- Black bar height: 62 em
