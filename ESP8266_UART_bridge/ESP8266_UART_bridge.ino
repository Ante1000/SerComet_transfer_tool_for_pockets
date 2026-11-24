// ESP8266 (Wemos D1 mini / ESP8266MOD) as a USB <-> TTL-UART converter for the Sharp PC-E500 (and other pocket computers
// with a 15-pin TTL-UART SIO).
//
//   PC (USB, CH340 of the board) <-> UART0 (Serial, GPIO1/GPIO3)  <->  ESP8266  <->  SoftwareSerial (D5 = RX, D6 = TX)  <-> PC-E500
//
// The bridge is fully transparent (no text is added to the data). Both sides always run at the same baud rate, 8N1.
// The baud rate is chosen with the FLASH button (D3 / GPIO0): every short press selects the next rate
// (1200, 2400, 4800, 9600, 19200); the LED blinks 1..5 times to show the choice (1 = 1200 ... 4 = 9600) and the
// choice is stored in flash. Set the same baud rate in SerComet (or in the terminal program) as in the bridge.
// Default after the first start: 9600 (the PC-E500: OPEN "9600,N,8,1,A,L,&H1A,N,N").
//
// Libraries: ESP8266 Arduino core (the board "LOLIN(WEMOS) D1 mini"); SoftwareSerial comes with the core.
// Wi-Fi is switched off, so that the interrupts of the radio do not disturb the software UART.
//
// NOTE: the pins of the ESP8266 are 3.3 V only. The output of the PC-E500 (0 / +5 V) must go to D5 through a voltage
// divider (see README.md).

#include <ESP8266WiFi.h>
#include <SoftwareSerial.h>
#include <EEPROM.h>

static const uint8_t  PIN_E500_RX = 14;      // D5  <- TX (SD) of the PC-E500 (through the divider!)
static const uint8_t  PIN_E500_TX = 12;      // D6  -> RX (RD) of the PC-E500
static const uint8_t  PIN_BUTTON  = 0;       // D3 (FLASH button), low = pressed. Do not hold it while the board resets.
static const uint8_t  PIN_LED     = 2;       // D4, the blue LED, low = on

static const uint32_t BAUDS[] = { 1200, 2400, 4800, 9600, 19200 };
static const uint8_t  BAUD_COUNT = sizeof(BAUDS) / sizeof(BAUDS[0]);
static const uint8_t  BAUD_DEFAULT = 3;      // 9600
static const uint8_t  EE_MAGIC = 0xA5;

SoftwareSerial pocket;                       // the TTL-UART of the pocket computer
uint8_t  baudIndex = BAUD_DEFAULT;
uint32_t ledOffAt = 0;                       // activity flash of the LED

static void ledOn(uint16_t ms)  { digitalWrite(PIN_LED, LOW); ledOffAt = millis() + ms; }
static void ledPoll()           { if (ledOffAt && (int32_t)(millis() - ledOffAt) >= 0) { digitalWrite(PIN_LED, HIGH); ledOffAt = 0; } }

static void blinkCount(uint8_t n)
{
  for (uint8_t i = 0; i < n; i++)
  {
    digitalWrite(PIN_LED, LOW);  delay(150);
    digitalWrite(PIN_LED, HIGH); delay(200);
  }
}

static void loadSettings()
{
  EEPROM.begin(4);
  if (EEPROM.read(0) == EE_MAGIC && EEPROM.read(1) < BAUD_COUNT) baudIndex = EEPROM.read(1);
}

static void saveSettings()
{
  EEPROM.write(0, EE_MAGIC);
  EEPROM.write(1, baudIndex);
  EEPROM.commit();
}

static void startPorts()
{
  Serial.end();
  pocket.end();
  Serial.begin(BAUDS[baudIndex], SERIAL_8N1);
  Serial.setRxBufferSize(1024);                                                   // PC -> bridge
  pocket.begin(BAUDS[baudIndex], SWSERIAL_8N1, PIN_E500_RX, PIN_E500_TX, false, 512);   // false = normal (not inverted) TTL
}

void setup()
{
  pinMode(PIN_LED, OUTPUT);    digitalWrite(PIN_LED, HIGH);
  pinMode(PIN_BUTTON, INPUT_PULLUP);

  WiFi.persistent(false);
  WiFi.mode(WIFI_OFF);
  WiFi.forceSleepBegin();
  delay(1);

  loadSettings();
  startPorts();
  blinkCount(baudIndex + 1);
}

// the button: a short press selects the next baud rate
static void buttonPoll()
{
  static bool     wasDown = false;
  static uint32_t downSince = 0;
  bool down = digitalRead(PIN_BUTTON) == LOW;
  if (down && !wasDown) downSince = millis();
  if (!down && wasDown && millis() - downSince > 40)        // released after a debounced press
  {
    baudIndex = (baudIndex + 1) % BAUD_COUNT;
    saveSettings();
    startPorts();
    blinkCount(baudIndex + 1);
  }
  wasDown = down;
}

void loop()
{
  // PC -> pocket computer
  int n = Serial.available();
  while (n-- > 0 && pocket.availableForWrite() > 0)
  {
    pocket.write((uint8_t)Serial.read());
    ledOn(15);
  }

  // pocket computer -> PC
  while (pocket.available() > 0 && Serial.availableForWrite() > 0)
  {
    Serial.write((uint8_t)pocket.read());
    ledOn(15);
  }

  buttonPoll();
  ledPoll();
  yield();
}
