# ESP8266 USB <-> TTL-UART bridge for the Sharp PC-E500

A transparent USB-UART converter on a Wemos D1 mini (ESP8266MOD): the USB cable goes to the PC (the CH340 of the board is
connected to the hardware UART0), and the PC-E500 is connected to a software UART on D5 / D6. Use it with SerComet
(*Sharp other -> PC-E500*, TTL-UART) or any terminal program.

## Wiring

| Wemos D1 mini | PC-E500 (15-pin connector, 0 / +5 V TTL) |
|---|---|
| D5 (GPIO14, RX) | TX of the PC-E500 (SD) **through a divider**: 1 k from the PC-E500 TX to D5, 2 k from D5 to GND |
| D6 (GPIO12, TX) | RX of the PC-E500 (RD), direct (a 3.3 V high level is above the TTL threshold of 5 V logic; check it with your unit) |
| GND | GND of the PC-E500 |

* The ESP8266 pins are **not 5 V tolerant**: never connect the 5 V output of the PC-E500 directly to D5.
* Power the board only from the USB cable. Do not connect any supply pin of the PC-E500.
* Look up the pins SD / RD / GND in the manual of your PC-E500 (the numbers are not repeated here because I have not verified them).
* Do not press the FLASH button (D3) while the board resets: it would start the ESP8266 boot loader.
* After a reset the ESP8266 boot loader prints a few characters at 74880 bps on UART0; the terminal shows garbage once. Open the port after the board has started.

## Baud rate

Both sides always use the same rate, 8N1: 1200, 2400, 4800, **9600** (default), 19200. A short press of the FLASH button (D3) selects the next
rate; the LED blinks 1..5 times (4 = 9600) and the choice is stored in flash. Set the same rate in SerComet / the terminal. The PC-E500 limit is 9600 bps.

On the PC-E500: `OPEN "9600,N,8,1,A,L,&H1A,N,N" : CLOSE`, then `LOAD` / `SAVE` (COM is the default device).

## Build

Arduino IDE with the ESP8266 core, board *LOLIN(WEMOS) D1 mini*, upload `ESP8266_UART_bridge.ino`. No extra library is needed
(SoftwareSerial and EEPROM come with the core).

The firmware was written without a test on hardware or a compilation: please try it with a loopback first (D5 connected to D6, characters typed in a terminal must come back).
