# Saver/Loader for the Sharp PC-1600 (V3.2, 1200 to 19200 bps)

LOAD (`CALL &C100`) and SAVE (`CALL &C200`) of BASIC programs through the software UART (8N1; TX = PC7, RX = PB2), for SerComet (target PC-1600, Software Serial).
The file is the bytes of the BASIC program (tokens) and one checksum byte. A BASIC installer puts the Z-80 code in the machine-language area and
patches the delays for the chosen speed (ENTER = 9600).

| File | Contents |
|---|---|
| `Basic/sl.txt` | the BASIC installer V3.2 (49 lines) |
| `Basic/sl_v2.1.txt`, `asm/uart1600_sl_v2.1.asm` | the previous version V2.1 (fixed 19200 bps), kept as a working fallback |
| `asm/uart1600_sl.asm` | the source of SAVE and LOAD (position independent, no buffer; assembler `z80asm`) |
| `tools/build_sl.py` | builds `Basic/sl.txt` from the source |
| `tools/verify_sl.py` | emulator test (`pip install z80`), with `tools/verify_listing.py` |
| `Basic/testlong_*.bas`, `longtest.bas`, `tools/make_testprog.py` | long test programs |
| `Basic/tokentest*.txt/.img` | test files of the tokenizer |

Use: install with `RUN`, `NEW` (the routines stay). Save: `CALL &C200` (direct command). Load: `NEW`, `CALL &C100`, then Send in SerComet within 30 s.
With a variable (`X=0 : CALL &C100,X`): 1 = loaded, 2 = checksum error, 3 = nothing received, 4 = does not fit.
More details (memory layout of the BASIC, pointers, timing) in the comments of `asm/uart1600_sl.asm` and in the
[UART1600 repository](https://github.com/Ante1000/sharp_pc-1600_software_uart).
