// Canon X-07 <-> PC bridge on an Arduino Pro Mini (5 V / 16 MHz), after the idea of the gist "Canon X-07 <> PC using Arduino UNO"
// (written again, not copied). The PC side is the hardware UART (RX = pin 0, TX = pin 1 of the Pro Mini) of a USB-UART adapter
// (FTDI / CP2102 / CH340; the DTR, DSR, RTS lines of the adapter are not used); the X-07 side is a software UART on pins
// 10 (RX) and 11 (TX) with INVERTED logic.
//
// This version does not use the library SoftwareSerial (it cannot receive while it transmits, so a wire between pin 11 and pin 10
// is never heard). The UART of the X-07 side is bit-banged here: while a byte is transmitted, pin 10 is sampled in the middle of
// every bit, so a wire from pin 11 to pin 10 works as a loopback (see LOOPBACK below).
//
// RTS / CTS (pins 12 / 13) are switched off: USE_HANDSHAKE = false. Pin 13 is then the LED of the Pro Mini.
// 1200 baud on both sides, 8N1. Set the same speed in SerComet / the terminal; the bridge does not follow a speed change
// of the PC, so the SerComet port test can pass only at USB_BAUD (the other speeds of the list FAIL by design).
//
// LED (pin 13 of the Pro Mini): lights up when a byte begins to arrive from the X-07 on pin 10 (the start bit) and goes out 80 ms
// after the last byte. If it flickers with nothing connected, pin 10 is floating: put 10 k from pin 10 to GND (the idle level of
// the inverted line is LOW, the gist has the same resistor). Three short blinks after every start = the sketch has (re)started.
//
// LOOPBACK: after every start the sketch sends 0x55 and 0xAA on pin 11 and listens on pin 10 meanwhile: when both come back, the wire
// between pin 11 and pin 10 is there and the sketch works as a loopback (everything from the PC goes out on pin 11 and returns to
// the PC): the SerComet port test at USB_BAUD passes then. One long blink = the loopback was found. Without the wire (the X-07
// connected) the sketch is the bridge. Nothing has to be changed in the sketch to switch between the two.
//
// TEST_MODE:  0 = the bridge (and the loopback, see above),
//             1 = the PC side only: everything received from the PC is sent back,
//             2 = the X-07 side: a test line is sent to pin 11 every 0.5 s and everything received on pin 10 goes to the PC.
//
// The X-07 routine EE1F (EXEC &HEE1F, input of a program into the X-07) takes only about 30 bytes per second (the gist uses
// "pv -L 30"): faster bytes give "I/O error" on the X-07, so TX_BYTE_DELAY_MS is 33. The PC sends faster (120 bytes/s at 1200
// baud), so the bytes wait in a 512 byte queue and the sketch stops the PC with XOFF / XON. Set the flow control XON/XOFF in
// SerComet (Flow) or in the terminal program. The X-07 wants CR as the end of a line (not CR+LF): Line end = CR in SerComet.
// On the X-07 the same speed has to be set in INIT#5,"COM:",1200,... as in the sketch.

#define TEST_MODE 0

#include <Arduino.h>

static const uint8_t  PIN_X07_RX = 10;           // from the X-07 (its output)
static const uint8_t  PIN_X07_TX = 11;           // to the X-07 (its input)
static const uint8_t  PIN_RTS    = 12;           // output, high = the bridge is ready (only when USE_HANDSHAKE)
static const uint8_t  PIN_CTS    = 13;           // input, high = the X-07 accepts data (only when USE_HANDSHAKE); else the LED

static const bool     USE_HANDSHAKE   = false;   // RTS / CTS: off
static const bool     INVERTED        = true;    // inverted logic on the X-07 side
static const long     USB_BAUD        = 1200;    // the PC side
static const long     X07_BAUD        = 1200;    // the X-07 side
static const uint16_t TX_BYTE_DELAY_MS = 33;     // the time from the start of one byte to the start of the next one towards the X-07:
                                                 // 33 = about 30 bytes/s (EXEC &HEE1F cannot take more: faster data give "I/O error" on the X-07);
                                                 // 0 = full speed
static const bool     XONXOFF_TO_PC   = true;    // the queue is slower than the PC: XOFF (13H) / XON (11H) are sent to the PC (SerComet: Flow
                                                 // XON/XOFF, Tera Term: Flow control xon/xoff) so that no byte is lost

static const uint32_t BIT_US = 1000000UL / X07_BAUD;

static const uint16_t QSIZE = 512;
static uint8_t  q[QSIZE];
static uint16_t qHead = 0, qTail = 0, qCount = 0;
static uint32_t lastTx = 0, ledOffAt = 0;
static bool loopbackWire = false;
static bool xoffSent = false;               // set at the start: pin 11 is wired to pin 10

static inline void waitUntil(uint32_t t) { while ((int32_t)(micros() - t) < 0) { } }

// logical bit (1 = idle / stop, 0 = start) <-> level of the pin
static inline uint8_t pinLevel(uint8_t bit) { return INVERTED ? !bit : bit; }
static inline uint8_t bitOfPin()            { uint8_t v = digitalRead(PIN_X07_RX); return INVERTED ? !v : v; }

// the LED: on, and off again 80 ms after the last call of ledOff()
static void ledOn()  { if (!USE_HANDSHAKE) { digitalWrite(PIN_CTS, HIGH); ledOffAt = 0; } }
static void ledOff() { if (!USE_HANDSHAKE) ledOffAt = millis() + 80; }

static void ledPoll()
{
  if (!USE_HANDSHAKE && ledOffAt && (int32_t)(millis() - ledOffAt) >= 0) { digitalWrite(PIN_CTS, LOW); ledOffAt = 0; }
}

// transmits a byte on pin 11 (start bit, 8 data bits LSB first, stop bit); pin 10 is sampled in the middle of every data bit,
// so the returned byte is what came back on pin 10 (the loopback wire); with a real X-07 it is of no use
static uint8_t txByte(uint8_t b)
{
  uint8_t heard = 0;
  uint32_t t0 = micros();
  digitalWrite(PIN_X07_TX, pinLevel(0));                      // start bit
  for (uint8_t i = 0; i < 8; i++)
  {
    waitUntil(t0 + (uint32_t)(i + 1) * BIT_US);
    digitalWrite(PIN_X07_TX, pinLevel((b >> i) & 1));
    waitUntil(t0 + (uint32_t)(i + 1) * BIT_US + BIT_US / 2);
    if (bitOfPin()) heard |= (uint8_t)(1 << i);
  }
  waitUntil(t0 + 9UL * BIT_US);
  digitalWrite(PIN_X07_TX, pinLevel(1));                      // stop bit (and idle)
  waitUntil(t0 + 10UL * BIT_US);
  return heard;
}

// the start bit has just been seen on pin 10: receives the byte
static uint8_t rxByte()
{
  uint8_t b = 0;
  uint32_t t0 = micros();                                      // the beginning of the start bit (to within the polling time)
  for (uint8_t i = 0; i < 8; i++)
  {
    waitUntil(t0 + BIT_US + BIT_US / 2 + (uint32_t)i * BIT_US);   // the middle of the data bit i
    if (bitOfPin()) b |= (uint8_t)(1 << i);
  }
  waitUntil(t0 + 9UL * BIT_US + BIT_US / 2);                   // the middle of the stop bit
  return b;
}

static inline bool startBitSeen() { return bitOfPin() == 0; }

void setup()
{
  Serial.begin(USB_BAUD);
  pinMode(PIN_X07_RX, INPUT);
  pinMode(PIN_X07_TX, OUTPUT);
  digitalWrite(PIN_X07_TX, pinLevel(1));                      // idle
  if (USE_HANDSHAKE)
  {
    pinMode(PIN_RTS, OUTPUT);
    digitalWrite(PIN_RTS, HIGH);
    pinMode(PIN_CTS, INPUT);
  }
  else
  {
    pinMode(PIN_CTS, OUTPUT);                                  // the LED
    digitalWrite(PIN_CTS, LOW);
    // three blinks after every start: if they appear each time a program opens the COM port, the board is reset by DTR
    // (the sketch then starts 1 - 2 s after the port was opened and the first bytes sent in that time are lost)
    for (uint8_t i = 0; i < 3; i++) { digitalWrite(PIN_CTS, HIGH); delay(100); digitalWrite(PIN_CTS, LOW); delay(150); }
  }
  // is pin 11 wired to pin 10? (the pins and the inversion are tested at the same time)
  delay(50);
  loopbackWire = (txByte(0x55) == 0x55) && (txByte(0xAA) == 0xAA);
  if (loopbackWire && !USE_HANDSHAKE) { digitalWrite(PIN_CTS, HIGH); delay(700); digitalWrite(PIN_CTS, LOW); }
}

void loop()
{
#if TEST_MODE == 1
  while (Serial.available() > 0) Serial.write((uint8_t)Serial.read());
#elif TEST_MODE == 2
  static uint32_t last = 0;
  if ((uint32_t)(millis() - last) >= 500)
  {
    last = millis();
    const char *s = "X07 TEST 0123456789\r\n";
    for (; *s; s++) txByte((uint8_t)*s);
  }
  if (startBitSeen()) { ledOn(); Serial.write(rxByte()); ledOff(); }
#else
  // PC -> queue
  while (Serial.available() > 0 && qCount < QSIZE)
  {
    q[qHead] = (uint8_t)Serial.read();
    qHead = (qHead + 1) % QSIZE;
    qCount++;
  }

  // the flow control towards the PC
  if (XONXOFF_TO_PC)
  {
    if (!xoffSent && qCount > QSIZE * 3 / 4) { Serial.write((uint8_t)0x13); xoffSent = true; }
    else if (xoffSent && qCount < QSIZE / 4) { Serial.write((uint8_t)0x11); xoffSent = false; }
  }

  // X-07 -> PC (a byte that the X-07 is sending now has the priority)
  if (!loopbackWire && startBitSeen()) { ledOn(); Serial.write(rxByte()); ledOff(); }

  // queue -> X-07 (one byte at a time, at most one per TX_BYTE_DELAY_MS)
  else if (qCount > 0 && (!USE_HANDSHAKE || digitalRead(PIN_CTS) == HIGH) && (uint32_t)(millis() - lastTx) >= TX_BYTE_DELAY_MS)
  {
    lastTx = millis();                                          // the start of this byte (the spacing is measured from start to start)
    uint8_t heard = txByte(q[qTail]);
    qTail = (qTail + 1) % QSIZE;
    qCount--;
    if (loopbackWire) { ledOn(); Serial.write(heard); ledOff(); }   // the loopback: what was heard on pin 10 goes back to the PC
  }
#endif
  ledPoll();
}
