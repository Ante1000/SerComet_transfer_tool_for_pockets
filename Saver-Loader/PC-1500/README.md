# Saver/Loader for the Sharp PC-1500(A), 4800 bps

LOAD and SAVE of BASIC programs through the software UART (8N1, fixed 4800 bps; TX = PC7 pin 10, RX = PB2 pin 27 or PB0 pin 9, TTL levels),
for SerComet (target PC-1500, Software Serial). The routines work directly in the program memory; the bit routines are those of
[UART7.3](https://github.com/Ante1000/Sharp_PC-1500_Software_UART). The file is the bytes of the BASIC program (tokens) and one checksum byte.

| File | Contents |
|---|---|
| `Basic/sl1500.txt` | the BASIC installer (39 lines; asks for the RX port, shows INSTALLING..., POKEs 312 bytes with a checksum) |
| `Basic/sl1500_7c00.txt` | the installer for the **PC-1500A**: the code goes to the machine language RAM `&7C10` / `&7D00` (see below; no `NEW &n` needed at all) |
| `Basic/sl1500_ce161.txt` | the installer for the CE-161 (code at the top of the module, `&3E00` / `&3F00`) |
| `wav/sl1500.wav` | the installer as a tape recording for `CLOAD` (CE-150; name `SL1500`, 6 s leader tone, about 2 minutes) |
| `asm/uart1500_sl.asm` | the source (LH5801) of LOAD and SAVE |
| `tools/lh5801asm.py`, `tools/build_sl1500.py` | a small LH5801 assembler and the installer builder |
| `tools/verify_sl1500.py` | simulator test of SAVE, LOAD and the installer (about 90 s, Python 3 only) |

## Use

1. PRO mode: `NEW0`, `NEW &4300` (RAM start + `&300`; other RAM: `PEEK &7863` is the high byte of the RAM start; with the RAM from `&0000`: `NEW &300`), `CLOAD` the tape (or type `sl1500.txt`), `RUN`;
   ENTER for RX on PB2 (`0` for PB0); it shows INSTALLING... and then `SL1500 READY 4800 PB2` (or `PB0`, the chosen port) and the two calls.in hex (`LOAD CALL &0100`, `SAVE CALL &0200`) and, for this machine, the command to give after `NEW0` (`AFTER NEW0: NEW &4300`, `NEW &300` with the RAM from `&0000`; it is RAM start + `&300`). Then `NEW`: the routines stay.
   The WAV `wav/sl1500.wav` is of the earlier text of the installer (without these messages): make it again from `Basic/sl1500.txt` with SerComet when needed.
2. **Save** (PC-1500 to SerComet): `CALL &4200` (RUN mode). `X=0 : CALL &4200,X` gives X = 1 when done.
3. **Load** (SerComet to the PC-1500): `NEW`, `CALL &4100`, then Send in SerComet within 30 s. `X=0 : CALL &4100,X`: 1 = loaded, 2 = checksum error,
   3 = nothing received, 4 = does not fit; after 2 or 4 the memory is left empty.

Addresses are for a RAM that starts at `&4000`: **LOAD at RAM start + `&100` (`&4100`), SAVE at RAM start + `&200` (`&4200`)**, BASIC from RAM start + `&300`; with the RAM from `&0000`: `CALL 256` (LOAD) and `CALL 512` (SAVE). The installer prints them for any configuration.
Tested in a simulator (cycle counts of the [pc1500emu](https://github.com/pchambre/pc1500emu) opcode table); **not yet tried on a real PC-1500**.

## PC-1500A: the code in the machine language RAM (`&7C01`-`&7FFF`), no `NEW &n` needed

The PC-1500A has 1 KB of RAM for machine language programs at `&7C00`-`&7FFF` (the PC-1500 without A has only a mirror of `&7800`-`&7BFF` there).
Measured on a real PC-1500A (bare machine and with a CE-161): it is a separate RAM, not a mirror (`&7FFF` is not `&7BFF`); `&7C00` itself cannot be
written (it always reads 13), `&7C01` upwards can; the check sum of `&7C01`-`&7FFF` did not change after errors, a program with GOSUB, RESERVE keys,
CSAVE / CLOAD, LPRINT (CE-150), `NEW0`, `NEW &n`, switching off and on, nor after fitting and removing a working CE-161. The BASIC variables grow downwards
from the top of the RAM (`&7899/9A`, `&5800` after `NEW0`), so the top of the program RAM is not a safe place; this area is outside of the BASIC memory.

| | |
|---|---|
| LOAD | `CALL &7C10` (`NEW` first) |
| SAVE | `CALL &7D00` |
| code | 312 bytes, `&7C10`-`&7CCE` and `&7D00`-`&7D78` |
| installer | `Basic/sl1500_7c00.txt` (built with `python3 tools/build_sl1500.py 7c00`) |

Install with any `NEW0`, enter / `CLOAD` the installer, `RUN`. It refuses (`NO ML RAM AT &7C00`) on a machine without this RAM (it writes `&7FFF` and checks that `&7BFF` does not change)
and reads back every POKEd byte (`ML RAM WRITE ERROR`). The routines stay after `NEW`, `NEW0`, `NEW &n` and after switching the machine off (they are lost with `ALL RESET`
or when the batteries are out): the same two calls in every PC-1500A configuration (bare, CE-155, CE-161, 32 KB). The BASIC memory is not reduced.
A **faulty memory module** changed this area once during the tests: a module that gives rubbish with `CLOAD` is not a good test partner.
Tested in the simulator only (the SL code is the same as for the other layouts); the CALL of a test routine at `&7C00` worked on a real PC-1500A.

## CE-161 version: the SL in the memory module (survives `NEW0`)

For a PC-1500(A) with the 16 KB RAM module **CE-161** (RAM from `&0000`, `PEEK &7863` = 0). Measured on a real CE-161: `NEW0`, `NEW 256` and
`NEW &n` write only **one byte** (the end marker `FFH` at the start of the BASIC program, `&00C5` / `&0100` / `&n`) and change the pointers; they erase
nothing. `MEM` after `NEW0` is 22330 (the BASIC memory is one block from the program start to `&57FF`). The code is therefore placed at the **top of the
module**, where the BASIC does not come unless the program is longer than about 15.5 KB:

| | |
|---|---|
| LOAD | `CALL &3E00` (`NEW` first) |
| SAVE | `CALL &3F00` |
| code | 316 bytes, `&3E00`-`&3F78` |
| installer | `Basic/sl1500_ce161.txt` (built with `python3 tools/build_sl1500.py ce161`) |

1. Switch of the CE-161: **write enabled** (protection off). `NEW0`, enter / `CLOAD` the installer, `RUN`. It stops with `WRITE PROTECT ON?` when the POKEs do not stay.
2. The routines stay after `NEW0`, `NEW 256` (the initial setting of the CE-161 manual), `NEW &300` and after switching the machine off.
3. **Keep `MEM` above about 6700**: below it the BASIC program reaches `&3E00` and overwrites the code (nothing protects it with the switch off). `LOAD` itself
   cannot overwrite the code: when the received program would reach `&3E00` it stops with `X = 4` and leaves the memory empty (this check is new in this version;
   tested in the simulator, `tools/verify_sl1500.py`).
4. A faulty `POKE` into `&3E00`-`&3F78`, or a program that is too long, destroys the code: install it again (keep the installer on tape).
   Alternatively switch the protection on (then the BASIC must start in the internal RAM: `NEW0`, `NEW &4000`; 6 KB program memory).

Not yet tried on a real PC-1500: the installer and the guard were tested only in the simulator; the CALL of a test routine at `&3E00` (returns `X = 1`) worked on a real CE-161.

## Archive

`archive/` keeps the earlier versions: `sl1500_v1.txt` / `sl1500_v1.wav` (installer with LOAD at RAM start + `&C5`, SAVE at + `&184`) and `sl1500_ml_pb2.*`, `sl1500_ml_pb0.*`
(the 312 bytes of LOAD + SAVE as a machine-language file, with tape recordings, for loading with `CALL &BCE8` / `&BD3C`; **not working yet**, the header handling was not finished).
