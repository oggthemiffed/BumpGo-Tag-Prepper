# BumpGo tag format (SkyRC D200neo+)

Worked out from about 90 tags saved on a D200neo+ (SK-100196-06), each changing one setting from a known baseline, then dumped with `BumpGoPrep.Console dump` and compared with `diff`.

## Summary

- The profile is **28 bytes in NTAG213 user pages `0x04`–`0x0A`**. The rest of user memory (`0x0B`–`0x27`) is left as zeros.
- Values are plain big-endian integers in mA and mV. There's **no checksum, no encryption, and nothing tied to the tag UID**: the same profile saved to two different tags is byte-for-byte identical, and a profile copied to another tag is accepted by the charger.
- The charger **never sets password protection** (AUTH0 stays `0xFF`). It only needs the BumpGo password (`05 07 09 03`) and PACK in pages `0x2B`/`0x2C` to accept a tag.
- Bumping a tag without saving writes nothing.
- A new SkyRC tag ships with LiPo 2S, Balance Charge, 10.0 A charge / 1.0 A discharge.

## Layout

| Offset | Page | Size | Field |
|---|---|---|---|
| 0–7 | 04–05 | 8 | Label, ASCII, right-aligned in 8 characters: type name right-aligned in 4, a space, cells + `S`. `" LiPo 2S"`, `"NiMH 10S"`, `"   PB 6S"`. LiIon is written `LiIo`, Pb as `PB`. |
| 8 | 06 | 1 | Battery type |
| 9 | 06 | 1 | Cells |
| 10 | 06 | 1 | Mode (numbering depends on battery type) |
| 11–12 | 06–07 | 2 | Charge current, mA |
| 13–14 | 07 | 2 | Discharge current, mA |
| 15–16 | 07–08 | 2 | Discharge cut-off per cell, mV |
| 17–18 | 08 | 2 | Target: charge voltage per cell (mV) in lithium charge modes and for Pb; storage voltage in lithium storage mode; −ΔV in mV for NiMH/NiCd |
| 19 | 08 | 1 | Cycle order (NiMH/NiCd cycle): 0 charge→discharge, 1 discharge→charge |
| 20 | 09 | 1 | Cycle count (also the re-peak count, and a lithium cycle count) |
| 21–22 | 09 | 2 | Trickle current, mA (NiMH/NiCd; 0 = off) |
| 23 | 09 | 1 | Always `0x0A`. Probably the 10-minute rest time between cycles; unconfirmed |
| 24–26 | 0A | 3 | Always zero |
| 27 | 0A | 1 | Always `0xA5`: end marker |

### Battery type (byte 8)

| Value | Type |
|---|---|
| 0 | LiPo |
| 1 | LiIon |
| 2 | LiFe |
| 3 | LiHV |
| 4 | NiMH |
| 5 | NiCd |
| 6 | Pb |

### Mode (byte 10)

| Value | Lithium (LiPo/LiIon/LiFe/LiHV) | NiMH / NiCd | Pb |
|---|---|---|---|
| 0 | Balance Charge | Charge | Charge (Normal) |
| 1 | Charge | — (not seen) | Discharge |
| 2 | Discharge | Discharge | AGM Charge |
| 3 | Storage | Re-Peak | Cold Charge |
| 4 | — | Cycle | — |

### Charger defaults (as written to a fresh profile)

| Type | Cut-off /cell | Charge /cell | Storage /cell |
|---|---|---|---|
| LiPo | 3.30 V | 4.20 V | 3.85 V |
| LiHV | 3.40 V | 4.35 V | 3.90 V |
| LiIon | 3.20 V | 4.10 V | 3.80 V |
| LiFe | 2.90 V | 3.65 V | 3.30 V |
| NiMH / NiCd | 0.90 V | −ΔV 6 mV | — |
| Pb | 1.90 V | 2.40 V (Normal), 2.45 V (AGM, Cold) | — |

### Limits seen on the charger

- LiPo charge voltage 4.00–4.25 V, storage 3.80–3.90 V seen, discharge cut-off 3.00–3.40 V.
- Charge current 0.1–20.0 A in 0.1 A steps.
- NiMH up to 15 cells.

## Not stored on the tag

The safety timer, capacity cut-off, and LiPo rest time / cycle order made no difference to the tag, so they are charger-wide settings. There is no way to save a profile to a particular channel.

## Still open

- Byte 23 (`0x0A`): change the NiMH cycle rest time from 10 minutes and see if it follows.
- NiMH/NiCd mode 1 never appeared.
- Charge/storage voltage and cut-off ranges for LiHV, LiIon and LiFe haven't been explored, so `create` only accepts their defaults.
