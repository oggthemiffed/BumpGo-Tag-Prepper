# BumpGo Tag Prepper

Prepares blank NTAG213 tags so a **SkyRC D200neo+** charger accepts them as empty BumpGo battery tags — using a cheap USB NFC reader instead of a phone, a Proxmark3 or a Flipper Zero.

Once a tag is prepped, you set your charging profile on the charger and bump the tag. The charger writes the full profile itself, so this works for any chemistry and cell count (LiPo, LiHV, 2S, 4S, 6S…). Each NTAG213 has a unique hardware UID, so the charger tracks every battery independently.

## What it actually does

A blank NTAG213 needs just two pages written before the charger will recognise it:

| Page | Contents | Meaning |
| ---- | -------- | ------- |
| 43 (`0x2B`) | `05 07 09 03` | The password the charger expects |
| 44 (`0x2C`) | `00 00 00 00` | A zeroed PACK (password acknowledge) |

That's it — no dumping or cloning of an original tag required.

It writes only those two pages, and deliberately never touches the configuration pages (`0x29` AUTH0 / `0x2A` ACCESS), so it cannot accidentally lock a tag.

## Requirements

- **.NET 10 SDK**
- An **ACS ACR122U** USB NFC reader (or another PC/SC reader supporting NfcA / ISO 14443-3A)
- Blank **NTAG213** tags
- Developed and tested on Windows 11. The PC/SC stack is cross-platform, so it should work on Linux/macOS via `pcsc-lite`, but that is untested.

## Getting started

```bash
dotnet build
dotnet run --project BumpGoPrep.Console
```

Place a blank tag on the reader. It preps automatically, then waits for the next one:

```
BumpGo Tag Prepper
Prepares blank NTAG213 tags for the SkyRC D200neo+.
build 1.0.0.0 (built 2026-08-03 20:22:58)

Reader: ACS ACR122 0
Ready - place a blank NTAG213 on the reader. Ctrl+C to quit.

[1] 04-11-22-33-44-55-66
    Password (page 43)  OK
    PACK (page 44)      OK
    prepped - 1 done. Remove the tag and place the next one.
```

Each tag is prepped once. Lift a tag off and place it back to run it again, which is also how you retry one that failed. `Ctrl+C` stops cleanly and prints a summary.

## Project layout

| Project | Purpose |
| ------- | ------- |
| `BumpGoPrep.Core` | All the NFC and NTAG213 logic, exposed as events. No UI dependencies. |
| `BumpGoPrep.Console` | Console front end — subscribes to the events and prints them. |
| `BumpGoPrep.Tests` | xUnit tests. No hardware required. |

`Core` is deliberately UI-agnostic so a WinForms (or any other) front end can reference it and subscribe to the same events without duplicating logic. Events are raised on a background thread, so a GUI consumer must marshal to the UI thread before touching controls.

## Tests

```bash
dotnet test
```

The suite runs without a reader attached: a fake tag stands in for the hardware, so the prepping behaviour is exercised end to end.

## Implementation notes

Writes go through the reader's built-in **Update Binary** command:

```
FF D6 00 <page> 04 <4 bytes>
```

The reader handles the NfcA framing, the CRC and the tag's ACK internally. Raw NTAG `WRITE` (`A2`) commands work too, but must be wrapped in a PN532 Direct Transmit frame (`FF 00 00 00 <Lc> D4 40 01 …`) and unwrapped again on the way back — the built-in command avoids all of that.

Worth knowing if you extend this: **every reply ends with a two-byte status word**, `90 00` meaning success. A successful write returns *only* that status word and no data. Mistaking those trailing bytes for tag data is an easy way to convince yourself that working writes are failing.

Clone tags whose UID doesn't begin with `04` (NXP's manufacturer code) have been seen to work fine. The app reads the capability container and warns if a tag reports NTAG215/216 memory size, but never blocks on it.

## Credits

The two-page procedure this automates was worked out and shared by the RC community — credit to `versvendet33` and others in that thread for establishing that writing the password is all a blank tag needs.

## Disclaimer

Not affiliated with, endorsed by, or supported by SkyRC. BumpGo and D200neo+ are trademarks of their respective owners. This writes to NFC tags you own, at your own risk.
