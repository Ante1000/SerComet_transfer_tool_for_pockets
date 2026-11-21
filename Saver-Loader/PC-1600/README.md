# Saver/Loader for the Sharp PC-1600 (V4.0, 1200 to 19200 bps)

LOAD and SAVE (the addresses are shown by the installer; `CALL &xxxx`) of BASIC programs through the software UART (8N1; TX = PC7, RX = PB2), for SerComet (target PC-1600, Software Serial).
The file is the bytes of the BASIC program (tokens) and one checksum byte. A BASIC installer puts the Z-80 code in the machine-language area and
patches the delays for the chosen speed (ENTER = 9600).

| File | Contents |
|---|---|
| `Basic/sl_v4.0.txt` | the BASIC installer V4.0 (asks for the speed, shows INSTALLING, ends with the addresses `CALL&xxxx` for LOAD and SAVE) |
| `wav/sl1600_mode0_CE-1600P.wav` | the installer as a tape recording for the PC-1600 in MODE 0 (`LOAD "CAS:SL1600"`, 6 s leader tone, about 25 s) |
| `wav/sl1600_mode1_CE-150.wav` | the same program in the PC-1500 tape format (MODE 1, CE-150 / PC-1500 `CLOAD`, 6 s leader tone, about 2.5 min); **confirmed: it can be loaded in MODE 1 and run in MODE 0** (see below) |
| `Basic/sl_v3.2.txt`, `asm/uart1600_sl_v3.2.asm` | the previous version V3.2 (code fixed at `&C100` / `&C200`; programs up to about 10 KB when the BASIC memory starts in an extension module) |
| `Basic/sl_v2.1.txt`, `asm/uart1600_sl_v2.1.asm` | the old version V2.1 (fixed 19200 bps), kept as a working fallback |
| `asm/uart1600_sl.asm` | the source of SAVE and LOAD (position independent, no buffer; assembler `z80asm`) |
| `tools/build_sl.py` | builds `Basic/sl_v4.0.txt` from the source |
| `tools/verify_sl.py` | emulator test (`pip install z80`), with `tools/verify_listing.py` |
| `Basic/testlong_*.bas`, `longtest.bas`, `tools/make_testprog.py` | long test programs |
| `Basic/tokentest*.txt/.img` | test files of the tokenizer |

## Installing the loader from tape

The installer is a BASIC program; it puts the machine-language LOAD and SAVE into the RAM. It can be loaded in two ways:

- **With a CE-1600P or CE-152 (MODE 0, PC-1600 tape format):** play `wav/sl1600_mode0_CE-1600P.wav` and `LOAD "CAS:SL1600"`.
- **With only a CE-150 (MODE 1, PC-1500 tape format): confirmed to work.** Switch the PC-1600 to **MODE 1**, play `wav/sl1600_mode1_CE-150.wav` into the CE-150 and `CLOAD` the program,
  then **switch back to MODE 0** and `RUN` it. The program stays in the memory when the mode is changed, so the installer runs normally in MODE 0 (the machine code it installs is for the Z-80 of MODE 0).
  This way the Saver/Loader can be installed **without a CE-1600P**, with the CE-150 of the PC-1500 alone.

Then `RUN` asks for the speed (ENTER = 9600), shows INSTALLING and ends with the two calls.

Use: install with `RUN` (the installer ends and its lines disappear: the code stays in the memory). Save: `CALL &xxxx` (the SAVE address shown, direct command). Load: `NEW`, `CALL &xxxx` (the LOAD address shown), then Send in SerComet within 30 s.
**Where the code lives (V4.0):** it is put behind the installer text (start of the program + 3072 bytes) and the start of the BASIC program is moved behind it (both pointer notations, `FE3CH` and `F865H`). `NEW` keeps this start (tested on the machine), so a program of any size up to the end of the memory (`MEM`) can be loaded without overwriting the code. It costs about 3.6 KB of the BASIC memory (the installer text plus the two routines).
V3.2 had the code at `&C100` and could not load a program that goes through that area (more than about 10 KB from an extension module starting at `98C5H`).
With a variable (`X=0 : CALL &C100,X`): 1 = loaded, 2 = checksum error, 3 = nothing received, 4 = does not fit.
More details (memory layout of the BASIC, pointers, timing) in the comments of `asm/uart1600_sl.asm` and in the
[UART1600 repository](https://github.com/Ante1000/sharp_pc-1600_software_uart).
