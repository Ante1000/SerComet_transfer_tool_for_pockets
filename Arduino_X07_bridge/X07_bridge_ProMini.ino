// Canon X-07 <-> PC bridge on an Arduino Pro Mini (5 V / 16 MHz), after the idea of the gist "Canon X-07 <> PC using Arduino UNO"
// (written again, not copied). The PC side is the hardware UART (RX = pin 0, TX = pin 1 of the Pro Mini) of a USB-UART adapter
// (FTDI / CP2102 / CH340); the X-07 side is a software UART on pins 10 (RX) and 11 (TX) with INVERTED logic.
//
// RTS / CTS (pins 12 / 13) are switched off for now: USE_HANDSHAKE = false. Pin 13 (the LED of the Pro Mini) then shows the activity.
// 1200 baud on both sides (the gist uses 4800; change USB_BAUD and X07_BAUD together with the speed set on the X-07). Set the same speed in SerComet / the terminal:
// the bridge does not follow a speed change of the PC, so the SerComet port test passes only at USB_BAUD.
//
// TEST_MODE:  0 = the bridge,
//             1 = the PC side only: everything received from the PC is sent back (the SerComet port test, wires TXD-RXD are not needed),
//             2 = the X-07 side: a test line is sent to the X-07 pin every 0.5 s and everything received from the X-07 goes to the PC.
//
// IMPORTANT: a wire from pin 11 to pin 10 is NOT a loopback test: the AVR SoftwareSerial cannot receive while it transmits (it
// works with the interrupts off), so it never hears itself. Use TEST_MODE 1 for the PC side, TEST_MODE 2 with the X-07 (or
// with pin 11 on the RX of a second adapter) for the other side.
//
// DTR: most USB-UART adapters reset the Pro Mini every time a program opens the port (the bootloader then waits about 1-2 s and
// the first bytes are lost). After uploading the sketch disconnect the DTR wire (or cut the reset line / use a 100 nF capacitor
// only while uploading), otherwise a port test and the first transmission fail.
//
// The X-07 routine EE1F (EXEC &HEE1F, input of a program into the X-07) takes only about 30 bytes per second (the gist uses
// "pv -L 30"): for that set TX_BYTE_DELAY_MS to 33. The bytes wait in a 512 byte queue; a larger file than the queue needs
// a pause between the lines on the PC side (Line delay of SerComet), because there is no flow control to the PC.

#include <SoftwareSerial.h>

#define TEST_MODE 0

static const uint8_t  PIN_X07_RX = 10;           // from the X-07 (its output)
static const uint8_t  PIN_X07_TX = 11;           // to the X-07 (its input)
static const uint8_t  PIN_RTS    = 12;           // output, high = the bridge is ready (only when USE_HANDSHAKE)
static const uint8_t  PIN_CTS    = 13;           // input, high = the X-07 accepts data (only when USE_HANDSHAKE); else the LED

static const bool     USE_HANDSHAKE   = false;   // RTS / CTS: temporarily off
static const bool     INVERTED        = true;    // inverted logic on the X-07 side
static const long     USB_BAUD        = 1200;    // the PC side
static const long     X07_BAUD        = 1200;    // the X-07 side
static const uint16_t TX_BYTE_DELAY_MS = 0;      // 0 = full speed; 33 = about 30 bytes/s for EXEC &HEE1F
static const bool     WAIT_RX_IDLE    = true;    // do not start a byte towards the X-07 while the X-07 is sending one (it would be lost)

static const uint16_t QSIZE = 512;
static uint8_t  q[QSIZE];
static uint16_t qHead = 0, qTail = 0, qCount = 0;
static uint32_t lastTx = 0, ledOffAt = 0;

SoftwareSerial x07(PIN_X07_RX, PIN_X07_TX, INVERTED);

static void led()
{
  if (USE_HANDSHAKE) return;
  digitalWrite(PIN_CTS, HIGH);
  ledOffAt = millis() + 20;
}

static void ledPoll()
{
  if (!USE_HANDSHAKE && ledOffAt && (int32_t)(millis() - ledOffAt) >= 0) { digitalWrite(PIN_CTS, LOW); ledOffAt = 0; }
}

// the line of the X-07 is idle: LOW with the inverted logic, HIGH with the normal one
static bool x07LineIdle()
{
  return digitalRead(PIN_X07_RX) == (INVERTED ? LOW : HIGH);
}

void setup()
{
  Serial.begin(USB_BAUD);
  x07.begin(X07_BAUD);
  if (USE_HANDSHAKE)
  {
    pinMode(PIN_RTS, OUTPUT);
    digitalWrite(PIN_RTS, HIGH);
    pinMode(PIN_CTS, INPUT);
  }
  else
  {
    pinMode(PIN_CTS, OUTPUT);                    // the LED
    digitalWrite(PIN_CTS, LOW);
  }
}

void loop()
{
#if TEST_MODE == 1
  // the PC side: echo
  while (Serial.available() > 0) { Serial.write((uint8_t)Serial.read()); led(); }
  ledPoll();
#elif TEST_MODE == 2
  // the X-07 side: a test line every 0.5 s, the received bytes to the PC
  static uint32_t last = 0;
  if ((uint32_t)(millis() - last) >= 500) { last = millis(); x07.print(F("X07 TEST 0123456789\r\n")); led(); }
  while (x07.available() > 0) { Serial.write((uint8_t)x07.read()); led(); }
  ledPoll();
#else
  // PC -> queue
  while (Serial.available() > 0 && qCount < QSIZE)
  {
    q[qHead] = (uint8_t)Serial.read();
    qHead = (qHead + 1) % QSIZE;
    qCount++;
  }

  // queue -> X-07 (one byte at a time, at most one per TX_BYTE_DELAY_MS)
  if (qCount > 0 && (!USE_HANDSHAKE || digitalRead(PIN_CTS) == HIGH) && (uint32_t)(millis() - lastTx) >= TX_BYTE_DELAY_MS
      && (!WAIT_RX_IDLE || (x07.available() == 0 && x07LineIdle())))
  {
    x07.write(q[qTail]);
    qTail = (qTail + 1) % QSIZE;
    qCount--;
    lastTx = millis();
    led();
  }

  // X-07 -> PC
  while (x07.available() > 0) { Serial.write((uint8_t)x07.read()); led(); }
  ledPoll();
#endif
}
