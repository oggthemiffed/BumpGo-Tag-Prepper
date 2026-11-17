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

In prep mode it writes only those two pages, and never touches the configuration pages (`0x29` AUTH0 / `0x2A` ACCESS), so it cannot accidentally lock a tag.

## Creating tags from settings

The BumpGo profile format has been decoded (see [docs/bumpgo-format.md](docs/bumpgo-format.md)), so tags can be written straight from settings, no charger needed:

```bash
dotnet run --project BumpGoPrep.Console -- create --type LiPo --cells 4 --mode charge --charge 5.0
dotnet run --project BumpGoPrep.Console -- create --type NiMH --cells 6 --mode cycle --charge 2.0 --discharge 1.0 --cycles 2 --order dc
dotnet run --project BumpGoPrep.Console -- create --type Pb --cells 6 --mode agm --charge 2.0
```

It prints the profile it built, then programs every tag you put on the reader (prep, profile, verify). Anything you leave out uses the charger's own default. Add `--save <file>` to write the profile to a file for `program` instead. Run with `--help` for every option.

Values are checked against what the charger has been seen to allow, so `create` won't write a 25 A charge current or a 4.35 V LiPo. Voltage changes are only accepted for LiPo so far, because only those ranges have been tested; other types use their defaults.

`decode <dump-file>` shows the profile held in a dump, and `dump` now prints it for every tag it reads.

## Programming tags without the charger

Prepping leaves the profile to the charger. The app can also copy a whole profile from the PC, so you can make a batch of identical tags (say, ten 2S LiPo tags) without bumping each one on the charger:

1. **Make a master tag on the charger.** Prep a blank tag, bump it, set the profile and choose *Save to BumpGo Tag*.
2. **Dump it:** `dotnet run --project BumpGoPrep.Console -- dump`, then put the master tag on the reader. Its memory is saved to `dumps/<UID>_<time>.txt`.
3. **Program the blanks from that dump:** `dotnet run --project BumpGoPrep.Console -- program dumps/<file>.txt`. Each tag gets the password and PACK, then the 36 user-memory pages (`0x04`–`0x27`) copied across and read back to verify.
4. **Bump a programmed tag on the charger** and check that it shows the parameters you saved on the master tag.

Copying has been confirmed on a D200neo+: profiles aren't tied to the tag's UID.

### `--with-protection`

The D200neo+ turns out not to password-protect the tags it saves to (AUTH0 stays `0xFF`), so this option isn't needed for it. It's kept for tags or chargers that do. When protection is set, it's done by setting AUTH0 (and possibly the PROT bit in ACCESS). The dump shows what it set, as `# AUTH0` / `# ACCESS` lines at the top. By default `program` leaves the copies unprotected. If the charger appears to care, add `--with-protection` to copy AUTH0 and ACCESS from the dump as well.

Protection uses the BumpGo password, which the app already knows, so it can always be undone. The ACCESS bits that *can't* be undone are never written: `CFGLCK` (locks the config pages permanently) and `AUTHLIM` (disables the tag after too many wrong passwords). If the dump has them set, they are cleared and a note is printed.

Tags the charger has already protected can be dumped, re-prepped and re-programmed: the app authenticates with the BumpGo password when it finds AUTH0 set or a page it can't read.

### Decoding the profile format

To see which bytes hold which setting, save profiles on the charger that differ in one setting only, dump each tag, and compare them:

```bash
dotnet run --project BumpGoPrep.Console -- diff dumps/lipo-2s.txt dumps/lipo-4s.txt dumps/lihv-4s.txt
```

```
page  [1]          [2]          [3]
 04   01 02 05 00  01 04 05 00  02 04 05 00   user
      ^^ ^^
```

Only differing pages are listed, with `^^` under the bytes that changed. Dump files are plain text and safe to edit by hand: lines are `PP: B0 B1 B2 B3`, and anything after the four bytes is ignored. Once the layout is known, a profile could be generated from settings directly instead of copied.

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

With no arguments it runs in prep mode. Place a blank tag on the reader. It preps automatically, then waits for the next one:

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

Each tag is processed once. Lift a tag off and place it back to run it again, which is also how you retry one that failed. `Ctrl+C` stops cleanly and prints a summary.

### Commands

| Command | What it does |
| ------- | ------------ |
| `prep` (default) | Writes the password and PACK so the charger accepts the tag as a new BumpGo tag |
| `dump [--out <folder>]` | Reads each tag and saves it to a text file. Writes nothing |
| `program <dump-file> [--with-protection]` | Preps each tag, copies the profile from the dump, and verifies it |
| `diff <file> <file> [...]` | Shows which bytes differ between dumps. No reader needed |
| `create --type <t> --cells <n> [...]` | Builds a profile from settings and programs it onto tags. `--save <file>` writes it to a file instead |
| `decode <file> [...]` | Shows the charge profile in dump files. No reader needed |

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

Password authentication (`PWD_AUTH`, `1B`) has no built-in reader command, so it is the one raw NTAG command, sent via the ACR122U's Direct Transmit as a PN532 `InCommunicateThru` (`FF 00 00 00 07 D4 42 1B <pwd>`). A tag that refuses a command (wrong password, protected page) drops to idle and ignores everything until it is re-selected, so after a refused read the app reconnects with a card reset before authenticating.

Worth knowing if you extend this: **every reply ends with a two-byte status word**, `90 00` meaning success. A successful write returns *only* that status word and no data. Mistaking those trailing bytes for tag data is an easy way to convince yourself that working writes are failing.

Clone tags whose UID doesn't begin with `04` (NXP's manufacturer code) have been seen to work fine. The app reads the capability container and warns if a tag reports NTAG215/216 memory size, but never blocks on it.

## Credits

The two-page procedure this automates was worked out and shared by the RC community — credit to `versvendet33` and others in that thread for establishing that writing the password is all a blank tag needs.

## License

[MIT](LICENSE) — do what you like with it, no warranty.

## Disclaimer

Not affiliated with, endorsed by, or supported by SkyRC. BumpGo and D200neo+ are trademarks of their respective owners. This writes to NFC tags you own, at your own risk.
