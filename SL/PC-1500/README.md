# Saver/Loader for the Sharp PC-1500(A), 4800 bps

LOAD and SAVE of BASIC programs through the software UART (8N1, fixed 4800 bps; TX = PC7 pin 10, RX = PB2 pin 27 or PB0 pin 9, TTL levels),
for SerComet (target PC-1500, Software Serial). The routines work directly in the program memory; the bit routines are those of
[UART7.3](https://github.com/Ante1000/Sharp_PC-1500_Software_UART). The file is the bytes of the BASIC program (tokens) and one checksum byte.

| File | Contents |
|---|---|
| `Basic/sl1500.txt` | the BASIC installer (34 lines; asks for the RX port, POKEs 312 bytes with a checksum) |
| `wav/sl1500.wav` | the installer as a tape recording for `CLOAD` (CE-150) |
| `asm/uart1500_sl.asm` | the source (LH5801) of LOAD and SAVE |
| `tools/lh5801asm.py`, `tools/build_sl1500.py` | a small LH5801 assembler and the installer builder |
| `tools/verify_sl1500.py` | simulator test of SAVE, LOAD and the installer (about 90 s, Python 3 only) |

## Use

1. PRO mode: `NEW0`, `NEW &4200` (RAM start + `&200`; other RAM: `PEEK &7863` is the high byte of the RAM start), `CLOAD` the tape (or type `sl1500.txt`), `RUN`;
   ENTER for RX on PB2 (`0` for PB0). Then `NEW`: the routines stay.
2. **Save** (PC-1500 to SerComet): `CALL &4184` (RUN mode). `X=0 : CALL &4184,X` gives X = 1 when done.
3. **Load** (SerComet to the PC-1500): `NEW`, `CALL &40C5`, then Send in SerComet within 30 s. `X=0 : CALL &40C5,X`: 1 = loaded, 2 = checksum error,
   3 = nothing received, 4 = does not fit; after 2 or 4 the memory is left empty.

Addresses are for a RAM that starts at `&4000`: LOAD at RAM start + `&C5`, SAVE at RAM start + `&184`; the installer prints them for other configurations.
Tested in a simulator (cycle counts of the [pc1500emu](https://github.com/pchambre/pc1500emu) opcode table); **not yet tried on a real PC-1500**.

## Loading the Saver/Loader from tape without the BASIC installer (machine code, keyboard only)

`ml/sl1500_pb2.bin` (RX = PB2) and `ml/sl1500_pb0.bin` (RX = PB0) are the 312 bytes of LOAD + SAVE as they are after the installer;
`wav/sl1500_ml_pb2.wav` and `wav/sl1500_ml_pb0.wav` are tape recordings of them (a machine-language file, about 27 s). They are loaded with the
cassette routines of the CE-150, from the keyboard of a freshly reset PC-1500 (no CLOAD):

1. PRO mode: `NEW0`, then `NEW` followed by RAM start + `&200` (`&4200`; with a RAM that starts at `&0000`: `NEW &200`; `PEEK &7863` is the high byte of the RAM start).
2. Several commands separated by `:` are not accepted from the keyboard of the PC-1500, so the calls are typed as a small **BASIC program** (PRO mode, every line followed by ENTER):
```
10 A=PEEK &7863*256+&C5
20 POKE &7879,&80
30 POKE &7B69,0
40 CALL &BCE8
50 CALL &BD3C,A
60 CALL &BBF5
```
   Then RUN mode, `RUN` + ENTER and at once start the WAV (CE-150 audio input).
3. Line 40 (`CALL &BCE8`) finds the header (up to a minute), line 50 (`CALL &BD3C,A`) stores the data from the address `A` (RAM start + `&C5`). When the tape has ended (the
   noise stops, about 27 s after the start) press **BREAK** once. If the program goes on, line 60 (`CALL &BBF5`) puts the ports and interrupts back; if the BASIC stops with `BREAK IN 50`,
   type `CALL &BBF5` + ENTER yourself. The 3 bytes after the code (`&1FD..&1FF` from the RAM start) are overwritten by the end of the tape; they are not used. Then PRO mode and `NEW` (the program is deleted, the routines stay).
4. Check (type it as a program, too, or look at a few bytes): `PRINT PEEK 197, PEEK 198, PEEK 388` gives `253 190 253` with the RAM from `&0000` (add `&4000` to the three addresses for the RAM from `&4000`). The sum of the 312 bytes is **34369** (PB2) or **34360** (PB0). Then LOAD is at RAM start + `&C5`, SAVE at RAM start + `&184`, exactly as after the installer.

The routines of the CE-150 (`BCE8` header, `BD3C` data block with a checksum every 80 bytes, `BBF5` end) are those of `CLOAD M`; the tape format was checked by decoding
the recordings back (identical bytes). Not yet tried on a real PC-1500.
