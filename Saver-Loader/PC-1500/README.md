# Saver/Loader for the Sharp PC-1500 and PC-1500A, 4800 bps

LOAD and SAVE of BASIC programs through the software UART (8N1, fixed 4800 bps; TX = PC7 pin 10, RX = PB2 pin 27 or PB0 pin 9, TTL levels),
for SerComet (target PC-1500, Software Serial). The routines work directly in the program memory; the bit routines are those of
[UART7.3](https://github.com/Ante1000/Sharp_PC-1500_Software_UART). The file is the bytes of the BASIC program (tokens) and one checksum byte.

| File | Contents |
|---|---|
| `Basic/sl1500_pc1500a.txt`, `wav/sl1500_pc1500a.wav` | installer for the **PC-1500A**: the code goes to the machine language RAM `&7C10` / `&7D00`; **no `NEW &nnnn` needed**, survives `NEW` and `NEW0`. Tape name `SL1500A` |
| `Basic/sl1500_pc1500.txt`, `wav/sl1500_pc1500.wav` | installer for the **PC-1500** with a RAM module (CE-151, CE-155, CE-159, CE-161, 32 KB...; the RAM is read from the machine, nothing to choose): the code goes to the program RAM, **`NEW &nnnn` after every `NEW0`**. Tape name `SL1500` |
| `asm/uart1500_sl.asm` | the source (LH5801) of LOAD and SAVE |
| `tools/lh5801asm.py`, `tools/build_sl1500.py` | a small LH5801 assembler and the installer builder (`python3 tools/build_sl1500.py pc1500` / `pc1500a`) |
| `tools/pc1500_bas2wav.py` | BASIC text to a tape recording (WAV) for `CLOAD`: `python3 tools/pc1500_bas2wav.py Basic/sl1500_pc1500a.txt wav/sl1500_pc1500a.wav SL1500A` (44.1 kHz, 8 bit, 8 s leader tone, about 2.5 minutes). The tokens CLS, DATA, READ, RESTORE and STEP were added to the tool; the tokenizer reproduces the program of the earlier `sl1500.wav` byte for byte |
| `tools/verify_sl1500.py` | simulator test of SAVE, LOAD and the installers (about 90 s, Python 3 only) |

Tested in a simulator (cycle counts of the [pc1500emu](https://github.com/pchambre/pc1500emu) opcode table); **not yet tried on a real PC-1500**
(the CALL of a small test routine in `&7C00` and in the CE-161 worked on a real PC-1500A).

## PC-1500A: `Basic/sl1500_pc1500a.txt`

The PC-1500A has 1 KB of RAM for machine language programs at `&7C00`-`&7FFF` (the PC-1500 without A has only a mirror of `&7800`-`&7BFF` there).
Measured on a real PC-1500A (bare machine and with a CE-161): it is a separate RAM, not a mirror (`&7FFF` is not `&7BFF`); `&7C00` itself cannot be
written (it always reads 13), `&7C01` upwards can; the check sum of `&7C01`-`&7FFF` did not change after errors, a program with GOSUB, RESERVE keys,
CSAVE / CLOAD, LPRINT (CE-150), `NEW0`, `NEW &n`, switching off and on, nor after fitting and removing a working CE-161. The BASIC variables grow downwards
from the top of the RAM (`&7899/9A`, `&5800` after `NEW0`), so the top of the program RAM is not a safe place; this area is outside of the BASIC memory.
A **faulty memory module** changed this area once during the tests.

1. `NEW0`, enter (or `CLOAD` the tape `SL1500A`) the installer, `RUN`. The RAM expansion does not matter (bare, CE-155, CE-161, 32 KB): the installer needs nothing of it.
2. ENTER for RX on PB2 (`0` for PB0). It checks that `&7FFF` is a RAM of its own (writes the complement and checks that `&7BFF` does not change; on a PC-1500 without A it stops with
   `NO ML RAM AT &7C00`), POKEs 312 bytes, reads back every byte (`ML RAM WRITE ERROR`) and checks the sum of the DATA. Then it shows `SL1500 READY 4800 PB2` (or PB0),
   `LOAD CALL &7C10 NEW 1ST`, `SAVE CALL &7D00` and `SURVIVES NEW AND NEW0`.
3. **Save** (PC-1500A to SerComet): `CALL &7D00` (RUN mode). `X=0 : CALL &7D00,X` gives X = 1 when done.
4. **Load** (SerComet to the PC-1500A): `NEW`, `CALL &7C10`, then Send in SerComet within 30 s. `X=0 : CALL &7C10,X`: 1 = loaded, 2 = checksum error,
   3 = nothing received, 4 = does not fit; after 2 or 4 the memory is left empty.

The code stays after `NEW`, `NEW0`, `NEW &n` and after switching the machine off (it is lost with `ALL RESET` or when the batteries are out; keep the tape).
The BASIC memory is not reduced.

## PC-1500 with a RAM module: `Basic/sl1500_pc1500.txt`

The PC-1500 needs a RAM module (CE-151 4 KB, CE-155 / CE-159 8 KB, CE-161 / CE-163 16 KB or 32 KB): the installer reads the start and the top of the RAM from the machine
(`PEEK &7863`, `PEEK &7864`) and stops with `NEED RAM MODULE` when the RAM is smaller than 4 KB. The code goes to the program RAM:
**LOAD at RAM start + `&100`, SAVE at RAM start + `&200`, the BASIC from RAM start + `&300`**. `NEW0` puts the start of the BASIC to RAM start + `&C5`, where it would
overwrite the code with the first lines, so **after every `NEW0` the command `NEW &nnnn` (nnnn = RAM start + `&300`) is needed** (and not the `NEW 256` of the CE-161 manual). `NEW0`,
`NEW 256` and `NEW &n` write only one byte (the end marker `FFH` at the start of the BASIC program) and change the pointers (measured on a CE-161); they erase nothing.

| RAM | RAM start | `NEW` after `NEW0` | LOAD | SAVE |
|---|---|---|---|---|
| PC-1500A without a module (6 KB) | `&4000` | `NEW &4300` | `CALL &4100` | `CALL &4200` |
| 16 KB CE-161 / 32 KB RAM from `&0000` | `&0000` | `NEW &300` | `CALL &0100` | `CALL &0200` |
| other modules | `PEEK &7863` * 256 | RAM start + `&300` | + `&100` | + `&200` |

The installer prints the values for your machine.

1. PRO mode: `NEW0`, `NEW &nnnn` from the table (if you do not know it, load the installer and `RUN` it: it stops with `NEW &nnnn IN PRO!`; give that `NEW &nnnn` and load the installer again).
2. Enter (or `CLOAD` the tape `SL1500`) the installer, `RUN`. ENTER for RX on PB2 (`0` for PB0). It shows INSTALLING..., then `SL1500 READY 4800 PB2` (or PB0), the two calls and `AFTER NEW0: NEW &nnnn`.
   Then `NEW`: the routines stay (a plain `NEW` keeps the start of the BASIC).
3. **Save**: `CALL` SAVE (RUN mode). `X=0 : CALL &4200,X` gives X = 1 when done.
4. **Load**: `NEW`, `CALL` LOAD, then Send in SerComet within 30 s. `X=0 : CALL &4100,X`: 1 = loaded, 2 = checksum error, 3 = nothing received, 4 = does not fit; after 2 or 4 the memory is left empty.

The BASIC variables are allocated from the top of the RAM downwards; the code is at the bottom, below the BASIC program, so neither the program nor the variables can reach it.
It is lost by `NEW0` followed by a program (without `NEW &nnnn`), by `NEW 256`, by a faulty `POKE` and when the batteries are out.

## Archive

`archive/` keeps earlier versions: `sl1500_v1.txt` / `sl1500_v1.wav` (installer with LOAD at RAM start + `&C5`, SAVE at + `&184`), `sl1500_ml_pb2.*`, `sl1500_ml_pb0.*`
(the 312 bytes of LOAD + SAVE as a machine-language file, with tape recordings, for loading with `CALL &BCE8` / `&BD3C`; **not working yet**, the header handling was not finished)
and `sl1500_ce161.txt`: an installer that put the code at the top of the 16 KB module (LOAD `&3E00`, SAVE `&3F00`, with a guard in LOAD that stops at `&3E00`; kept for the case
that the protection switch of the CE-161 is used). The measured facts about it: with the switch on the module cannot be written (then the BASIC must start in the internal RAM: `NEW0`, `NEW &4000`);
`MEM` after `NEW0` is 22330 with a CE-161.
