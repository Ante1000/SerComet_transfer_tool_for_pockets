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
