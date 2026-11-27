# PC-1600 Saver/Loader for large BASIC programs (more than about 12 KB): analysis and concept

Status: a **concept with experiments**, no code yet. Sources: the PC-1600 Technical Reference (hardware, memory, BASIC interpreter chapters, as supplied) and this repository.
Every statement is marked: **[manual]** = written in the Technical Reference, **[repo]** = from our own code/notes, **[hyp]** = hypothesis that must be tested on the machine.

## 1. What the manual says about the memory

**Z-80 map and banks [manual, hardware chapter 7.2, I/O port 31H]**

| Z-80 window | bank 0 | bank 1 | bank 2 | bank 3 | 4..7 |
|---|---|---|---|---|---|
| 0000-3FFF | PC-1600 ROM | - | | | |
| 4000-7FFF | PC-1600 ROM | **slot 2 module, location C** | - | PC-1600 ROM (CS24) | CE-1600P ROMs |
| 8000-BFFF | **slot 1 (A)** | **slot 1 (B)** | **slot 2 (C)** | **slot 2 (D)** | bank 6 = PC-1600 ROM (CS123) |
| C000-FFFF | **main RAM (RAM3, 16 KB)** | - | | | |

* A module of 16 KB or less in slot 1 is at location A, in slot 2 at C; 32 KB: A+B or C+D; **48 KB or more (CE-1600M, 64 KB) only in slot 2**: the 32 KB C+D part, the rest through bank switching [manual 3.13.1].
* The window 8000-BFFF is switched by the program all the time (BASIC reads its own text through it). Whoever runs code from this window and switches it, loses the code.
* The slot 2 module is also visible at **4000-7FFF (bank 1)**: a second window on the same memory.

**BASIC program memory [manual 3.12.2, 4.2.4]**

* The program is not one block: it is stored in a **chain of logical banks**: the extension memory banks listed in `ADTBL+1..5` (F1D6H-F1DAH, bits 4-5 = physical bank, e.g. `01H` = bank 0, `32H` = bank 3), in the order given by `SOMTb` (F02AH), and **then the main memory**; "the program is loaded into bank 0, bank 2, bank 3 and the main memory in that order" (example 1).
* A line is `[line number 2 B][length 1 B][tokens][CR]`, the program ends with `FFH` (example: the program starts at C0C5H in the main memory) [manual 4.2.4].
* The ROM tracks a **logical bank number** next to every line address: `SRADRb` (F1C3H), `NXTADR` (02EEH: next line, address + logical bank), `TADCNV` (02D4H: logical -> physical bank), `LINSRH` (028CH) [manual].
* IOCS memory routines in the fixed ROM: `MEMORYCHK` 0180H, `BANKSET` 0190H (set the bank of a page), `BANKREAD` 0193H, `SLOT1MAP` 0196H, `SLOT2MAP` 0199H, `BANKJUMP` 019CH, `BANKCALL` 019FH, `BANKCALL2` (RST 20H + bank + address) [manual 3.12.3].
* A RAM module can be a **system software module**: header `55H 00H <entry L> <entry H> 00H 00H 00H 82H` at location A or C, followed by the machine code; "automatically run at power-on" [manual 3.13.2-3].

## 2. Why the current loader (V4.0) stops at about 12 KB [repo + hyp]

* [repo] V4.0 installs its code **inside the BASIC memory** (behind the installer text, 3072 bytes), moves the program start behind it, and stores the file **flat** from `FE3CH` up to `EEFFH`, "contiguous through C000H, also from a module". The logical bank of the end is set equal to that of the start.
* The free main memory is C0C5H-EEFFH = **12 KB**. The code takes 3.6 KB of it. That is the limit you see.
* [hyp] With an extension module the chain is: bank(s) first, then the main memory. A flat store crosses the end of a bank (BFFFH) into **C000H** (the main memory) instead of continuing in the **next logical bank** (8000H of another physical bank). The bytes land in the wrong place, they can overwrite the code installed there, and the pointers (`FE3FH`, `F868H`) do not describe a real chain.
* [hyp] The end scan of SAVE (it follows line lengths from `FE3CH`) has the same flat assumption.

## 3. Requirements for a correct big-program loader

1. **Code outside the program memory** (no loss of capacity, no self-overwrite).
2. **Code executing from fixed memory** while the 8000-BFFF window is being switched: that means **main RAM (C000-FFFF)** (or the ROM at 0000-3FFF, which we cannot change). The code cannot sit in the window it switches.
3. The store must follow the **chain of logical banks** in the order of `ADTBL`, line by line (a line must not be cut at the end of a bank, to be checked).
4. The pointers (start, end with the **logical bank byte**, `F865H`-`F868H`, `FE3CH`-`FE40H`, `FE2AH`-`FE2CH`) must describe the result exactly as `NXTADR` expects it.
5. The loader must use the IOCS routines (`TADCNV`, `BANKSET`) instead of own port 31H writes, as far as their calling conventions are known.

## 4. Options compared

| # | Idea | Capacity | Survives NEW / NEW0 | Risk | Verdict |
|---|---|---|---|---|---|
| A | V4.0: code in the BASIC memory | -3.6 KB, flat store | no (installer again) | wrong for banks | rejected |
| B | V3.2: code at &C100 | blocks the program there | no | the program crosses &C100 | rejected |
| C | code in the work area F000-FFFF | no loss | partly | the area is used by BASIC (stack FA38-FAFF, registers FA00-FA37, pointers F8xx) | too small and unknown, rejected |
| D | **transient, self-relocating loader**: copied to the **top of the free main RAM** (about EA00-EEFF) at the call, runs there, may switch banks freely | -0.6 KB (only while loading) | the code is gone afterwards, so it must be reinstalled from somewhere | medium | **good base (phase 1)** |
| E | **resident image in a battery-backed RAM module** (system software module, header 55H ...), the stub copies it into main RAM as in D | 0 | **yes** (the module is not touched by NEW/NEW0) | medium | **best (phase 2), your idea, with the relocation trick** |
| F | run the code from the second window 4000-7FFF (bank 1 = slot 2, location C) | 0 | yes | **only if the module is in slot 2**; a 64 KB module (the extension, CE-1600M) must be in slot 2, so slot 2 is taken by the extension memory | not general |
| G | stage the file in a RAM module (linear store, no BASIC chain), then let the ROM `LOAD "S2:..."` copy it (the ROM knows the banks) | 0 | yes | the format of the file module is **not in the supplied pages** | attractive, needs the BASIC manual (file module) |
| H | feed the lines through the BASIC's own entry (keyboard buffer / line insert): the ROM does the banks | 0 | n/a | routine and addresses unknown | research |
| I | use the ROM's RS-232 (`LOAD "COM1:"`) with a TTL <-> RS-232 level adapter | 0 | n/a | needs hardware, no software at all | **fallback that already works for any size** [repo: SerComet "RS-232 serial" mode] |

### Why E (module + relocation) and not "run in the module"
* The module is at 8000-BFFF only (bank 0/1 for slot 1, 2/3 for slot 2). The loader must switch exactly this window to reach the extension banks. So the code must **leave** the module: a 16-byte `LDIR` stub copies the image (about 600 bytes: LOAD 213 B + SAVE about 330 B + the patched delays) to the top of the free main RAM and jumps there.
* After NEW or NEW0 the user memory is empty and free, so the top of it is the natural scratch place. The loader checks its store pointer against the code start (error 4 "does not fit"); the cost is only the 0.6 KB at the very end of the memory.
* The relocated code is position independent already [repo: "needs no buffer, no CALLs, no absolute jumps"]; only the bank handling and the pointer logic are new.
* The module keeps the code across **NEW0, CLEAR, power off** (RAM modules are battery backed), and no installer from tape is needed after the first installation.

### How to call it from BASIC (the open point)
The window 8000-BFFF shows an arbitrary bank when BASIC runs `CALL`. A 5-byte trampoline in fixed RAM does the switch, e.g. `RST 20H, bank, addr L, addr H, RET` (BANKCALL2) [calling convention to be verified]. Where to keep it:
1. in a REM line or string of a one-line BASIC program (typed again after NEW0; `NEW` of the program deletes it);
2. in the machine-language reserve of the main RAM (C000-C0C4: 197 bytes below the BASIC start; **to be checked that NEW0 leaves it**);
3. **the power-on entry of the system software module** installs it (the manual: the module is run at power-on) and reinstalls it whenever it was lost, so that `CALL &xxxx` always works. This is the cleanest and would give the "always available" loader.

## 5. Concept (recommended)

**Phase 0: facts (one evening on the PC-1600 with the extension)**
* E1: dump `F016H-F022H`, `F02AH`, `F1D6H-F1DAH` (extension configuration) and the pointers `F865H-F868H`, `FE2AH-FE2CH`, `FE3CH-FE45H`, `F8A6H-F8ABH`, `F1C3H`, with the extension installed and with the main memory only.
* E2: load a large program by the **ROM** (RS-232 or CAS) and dump the memory with SAVE of V4.0 around the end of the first bank: do the lines continue at C000H or at 8000H of the next bank; what stands at the end of a bank (`FFH`?).
* E3: do NEW and NEW0 change C000H-C0C4H and the top of the main memory? Where does the BASIC place variables and the stack relative to the user memory end?
* E4: does `XPEEK` / `XPOKE` exist (the intermediate code table lists XPEEK# / XPEEK$)? They would allow an installer written in plain BASIC to fill any bank.

**Phase 1: relocating loader (works without any module)**
1. The installer (BASIC, as today) puts the code **at the top of the free main memory** instead of the start; it does not move the program start; the lost memory is 0.6 KB.
2. LOAD walks the chain `ADTBL` (logical banks via `TADCNV`/`BANKSET`), stores line by line, sets the pointers and the logical bank of the end, restores the window bank. SAVE walks the same chain with `NXTADR`.
3. Test with the extension (64 KB) and with 6 KB, 15 KB, 40 KB test programs (`testlong_*.bas` exist).

**Phase 2: resident module**
1. The BASIC installer writes the **system software module image** (header + code + checksum) into the RAM module (slot 1 recommended: CE-161 or a 32 KB part) and verifies it.
2. The module entry (power-on) installs the trampoline; `CALL &xxxx` or a short call reaches the stub, which relocates the loader and runs it.
3. On the first start after a loss of the trampoline, a one-line BASIC re-creates it.

**Phase 3 (research, if G or H are possible)**: staging in the module and the ROM `LOAD`, or line injection, would remove the bank logic from our code completely.

## 6. What I need to continue
* The dumps of E1-E3 (a photo or a typed list is enough), and the configuration (which module in which slot, `MEM` with and without it).
* The pages of the BASIC manual about `NEW`, `NEW0`..., `INIT "S1:"`, the module types (system software / file / program module), `XPEEK`/`XPOKE`, `LOAD "S1:"`.
* The IOCS description of `BANKSET`, `TADCNV`, `BANKCALL2` parameters (registers) if it exists in the part of the manual not supplied yet.
