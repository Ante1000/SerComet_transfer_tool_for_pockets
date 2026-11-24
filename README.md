![SerComet: a Sharp pocket computer talking to a PC](img/imag1.jpg)

# <img src="img/sercomet_icon.png" alt="" width="40" align="top"> SerComet

**File transfer tool for PC and Sharp pocket computers.** A small Windows program (version 1.6) that sends and receives BASIC programs
between a PC and a pocket computer through a serial port (a USB-UART / USB-RS232 adapter), with a few helpers around it: the
Saver/Loader programs for the Sharp PC-1500 and PC-1600, a built-in BASIC tokenizer / detokenizer, a terminal and a log.

Free for personal, non-commercial and educational use ([PolyForm Noncommercial License 1.0.0](LICENSE.md)).

If SerComet saves you time, you can [buy the author a coffee](https://www.buymeacoffee.com/Alex_F4VTS) (the button is also in the title bar of the program).

## What it does

- Send and receive BASIC programs, with a built-in tokenizer / detokenizer for the PC-1500 and PC-1600, **Auto token** and **Skip REM** (comment lines are left out when a program is sent).
- **Files** list: drag files in from Explorer (copied; the question about an existing file tells whether the new file is newer, older or the same) and drag them out to the desktop; point at a name for the size and dates; right mouse button: **View, Edit, Delete, Rename, Check Basic, Renumber, Remove REM** (the last two for `.bas` / `.txt`).
- **TEST** (button next to the port status, active while the port is open; the port is closed for the test and opened again afterwards): a port test. Connect RXD with TXD (and RTS with CTS or DTR with DSR if that flow control is selected); 100 bytes are sent and read back at every speed from 300 to 115200 bps; every speed is reported OK / FAIL and the result is PORT TEST OK / FAIL.
- **Renumber**: renumbers a BASIC text file (step and first line number, default 10 and 10) including GOTO, GOSUB, THEN, ELSE, RESTORE, RUN and ON ... GOTO / GOSUB; the log shows the old and the new range; if the last line would be above the limit of the machine, a smaller step (5, 2 or 1) is proposed. The file itself is changed.
- **Remove REM**: writes `NAME_NOREM.bas` without the REM lines and the comments at the ends of lines. Two options: *Keep first REM* (e.g. the program title; default on) and *Remove also target REM lines and renumber accordingly program skips* (default off: a REM that a GOTO / GOSUB / THEN points to is removed too and the jumps go to the next line that stays; a REM at the very end stays). The log tells how many lines were removed and which jumps were moved. Jumps with a computed line number (`GOTO A*10`) cannot be followed.
- **Check Basic**: a basic check of a listing for a chosen machine (line numbers, repeated numbers, GOTO / GOSUB targets, characters, line length, quotation marks and parentheses, keywords, FOR / NEXT, the program size against the free memory of the PC-1500A / PC-1600 ...). The keyword lists of the PC-1500 and PC-1600 are exact, those of the other machines are approximate (a warning only).
- **Sharp (other) with a model box** (1.6): the radio button *Sharp (other)* has a box beside it (active when the button is on): PC-1350, PC-1360, PC-E500, PC-E500S, PC-1450..90, other. The model sets the folder (`PC1350`, `PC1360`, `PCE500`, `PCE500S`, `PC1450`, `SHARP_other`; the old `PC_15-pin` folder is gone), the notes in the Info box and the WAV converter: PC-1350 / PC-1360 (tokenized tape recordings), PC-E500 / PC-E500S (ASCII tape file, CSAVE recordings are read back); for PC-1450..90 and *other* tokenization and tape format are not supported (the usual UART transfer of BASIC programs works).
- **Locked while sending** (1.5): while a file is being sent the port, the target computer, the transmission mode (with Baud, Delay, EOL and Flow) and the check boxes cannot be changed; the controls keep their look, they just ignore the mouse, the keys and the wheel until the transmission is finished or aborted. The log says so once when you try.
- **Line delay by baud rate** (1.5): when you choose a baud rate in a text mode the **Delay** box is set to a value that fits it: 20 ms at 300 to 2400 bps, 50 ms at 4800, 100 ms at 9600, 125 ms at 19200, 140 ms at 38400 and 57600, 145 ms at 115200 (twice these values for the PC-1500 and the NEC / TRS-80). The Sharp needs about 150 ms to tokenize and store a line and its receive buffer takes up the characters that arrive meanwhile: delay = 150 - 480000 / baud (at least 20 ms). These are starting values, tune them by testing; a delay saved in settings.ini is kept when the program starts.
- Warnings in the log when the baud rate looks wrong or the listing is too long for the memory; right mouse button in the log: Clear log, Copy log.

## Computers

| Target | How |
|---|---|
| Sharp PC-1500 | Software Serial through the Saver/Loader (4800 baud, TX = PC7, RX = PB2), or RS-232 text through the CE-158(X) |
| Sharp PC-1600 | Software Serial through the Saver/Loader (1200 to 19200 baud), or its RS-232 port |
| Other Sharp | pocket computers with the 15-pin connector (PC-1350, PC-1360, PC-E500 ...), TTL-UART text transfer |
| Casio FX-850P / FX-870P | TTL-UART, 300 to 4800 baud |
| TRS-80 Model 100 / NEC PC-8201 | RS-232, 300 to 19200 baud |
| UART Other | any computer with a UART interface, TTL or RS-232 |

## What is in this repository

| Folder | Contents |
|---|---|
| [`SerComet/`](SerComet) | the Windows program: C# WinForms source (Visual Studio 2019, .NET Framework 4.7.2): `Form1.cs`, `Form1.Designer.cs`, `Program.cs`, `SerComet.ico` |
| [`Saver-Loader/PC-1500/`](Saver-Loader/PC-1500) | **Saver/Loader for the PC-1500(A)**: LOAD and SAVE of BASIC programs through the software UART, 4800 bps: LH5801 source, BASIC installer, tape recording (WAV), simulator test |
| [`Saver-Loader/PC-1600/`](Saver-Loader/PC-1600) | **Saver/Loader for the PC-1600**: Z-80 source, BASIC installer (1200 to 19200 bps), emulator test, test programs |
| [`img/`](img) | pictures |

The Saver/Loaders are small machine-language routines that a short BASIC installer puts in the RAM of the pocket computer; after that
`CALL` sends the program in memory to SerComet and `CALL` loads one from it. They are based on the software UART of
[UART7.3 for the PC-1500](https://github.com/Ante1000/Sharp_PC-1500_Software_UART) and
[UART1600 for the PC-1600](https://github.com/Ante1000/sharp_pc-1600_software_uart).

## Building the program

Create a Windows Forms project in Visual Studio (.NET Framework 4.7.2), add the files of [`SerComet/`](SerComet) and build. There is nothing else to install.
The settings are kept in `settings.ini` (next to the program, or in `%AppData%\SerComet`).

## Author

Alex Matczewski F4VTS. Bug reports, suggestions, questions, contact: alex.f4vts@gmail.com.
YouTube: [youtube.com/@anteamber](https://www.youtube.com/@anteamber)
