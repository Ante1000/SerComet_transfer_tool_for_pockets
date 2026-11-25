// Canon X-07 <-> PC bridge on an Arduino Pro Mini (5 V / 16 MHz), after the idea of the gist "Canon X-07 <> PC using Arduino UNO"
// (written again, not copied). The PC side is the hardware UART (RX = pin 0, TX = pin 1 of the Pro Mini) of a USB-UART adapter
// (FTDI / CP2102 / CH340); the X-07 side is a software UART on pins 10 (RX) and 11 (TX) with INVERTED logic.
//
// RTS / CTS (pins 12 / 13) are switched off for now: USE_HANDSHAKE = false.
// 4800 baud on both sides (the speed of the X-07 serial port in the gist). Set the same speed in SerComet / the terminal.
//
// The X-07 routine EE1F (EXEC &HEE1F, input of a program into the X-07) takes only about 30 bytes per second (the gist uses
// "pv -L 30"): for that set TX_BYTE_DELAY_MS to 33. The bytes wait in a 512 byte queue; a larger file than the queue needs
// a pause between the lines on the PC side (Line delay of SerComet), because there is no flow control to the PC.

#include <SoftwareSerial.h>

static const uint8_t  PIN_X07_RX = 10;           // from the X-07 (its output)
static const uint8_t  PIN_X07_TX = 11;           // to the X-07 (its input)
static const uint8_t  PIN_RTS    = 12;           // output, high = the bridge is ready (only when USE_HANDSHAKE)
static const uint8_t  PIN_CTS    = 13;           // input, high = the X-07 accepts data (only when USE_HANDSHAKE)

static const bool     USE_HANDSHAKE   = false;   // RTS / CTS: temporarily off
static const bool     INVERTED        = true;    // inverted logic on the X-07 side
static const long     BAUD            = 4800;
static const uint16_t TX_BYTE_DELAY_MS = 0;      // 0 = full speed; 33 = about 30 bytes/s for EXEC &HEE1F

static const uint16_t QSIZE = 512;
static uint8_t  q[QSIZE];
static uint16_t qHead = 0, qTail = 0, qCount = 0;
static uint32_t lastTx = 0;

SoftwareSerial x07(PIN_X07_RX, PIN_X07_TX, INVERTED);

void setup()
{
  Serial.begin(BAUD);
  x07.begin(BAUD);
  if (USE_HANDSHAKE)
  {
    pinMode(PIN_RTS, OUTPUT);
    digitalWrite(PIN_RTS, HIGH);
    pinMode(PIN_CTS, INPUT);
  }
}

void loop()
{
  // PC -> queue
  while (Serial.available() > 0 && qCount < QSIZE)
  {
    q[qHead] = (uint8_t)Serial.read();
    qHead = (qHead + 1) % QSIZE;
    qCount++;
  }

  // queue -> X-07 (one byte at a time, at most one per TX_BYTE_DELAY_MS)
  if (qCount > 0 && (!USE_HANDSHAKE || digitalRead(PIN_CTS) == HIGH) && (uint32_t)(millis() - lastTx) >= TX_BYTE_DELAY_MS)
  {
    x07.write(q[qTail]);
    qTail = (qTail + 1) % QSIZE;
    qCount--;
    lastTx = millis();
  }

  // X-07 -> PC
  while (x07.available() > 0) Serial.write((uint8_t)x07.read());
}
