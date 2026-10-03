/*
  CouchIR - a universal infrared receiver for CouchTV.

  Board:    Arduino Pro Micro / Leonardo / Micro (ATmega32U4, 5 V, 16 MHz).
            In the Arduino IDE choose Tools > Board > "Arduino Leonardo".
  Sensor:   Vishay TSOP38238 (38 kHz) with its output on pin 2. See ../../README.md for wiring.
  Library:  "IRremote" by Armin Joachimsmeyer, version 4.x (Tools > Manage Libraries).

  The receiver decodes the buttons of almost any infrared remote (NEC, Samsung, Sony, LG, RC5, RC6, Panasonic,
  JVC, Denon, Sharp, Apple, plus a universal decoder for most others) and reports each one to the PC:

      IR,<code>,N      a new press, e.g.   IR,NEC 00CE 0001,N
      IR,<code>,R      the button is still held (repeat)

  The code text is stable for each button, so CouchTV can map it to an action. CouchTV decides what every button
  does. The receiver only remembers which buttons are "power" buttons, so it can wake the PC from sleep, which
  CouchTV can't do itself while the PC is asleep.

  Commands from the PC, one per line:
      ?              ->  COUCHIR <version> <number of wake buttons>
      WAKE CLEAR     ->  forget the wake buttons (in memory)
      WAKE <hash>    ->  add a wake button: FNV-1a hash of its upper-cased code text, 8 hex digits
      WAKE SAVE      ->  store the list in EEPROM (only changed bytes are written)
      WAKE LIST      ->  print the list
*/

#define EXCLUDE_EXOTIC_PROTOCOLS   // Bose, Lego, Whynter, FAST: not used by TV remotes; saves flash
#include <IRremote.hpp>
#include <Keyboard.h>              // a keyboard interface is what lets Windows arm the receiver to wake the PC
#include <EEPROM.h>

const uint8_t IR_PIN = 2;
const uint8_t LED_PIN = 17;        // the board's RX LED (lit when LOW): blinks for every button it decodes
const char VERSION[] = "1";

const uint8_t MAX_WAKE = 16;
const uint8_t EEPROM_MAGIC = 0xC7;
// The CouchTV phone remote's power button always wakes the PC, even before CouchTV has sent its list.
const char PHONE_POWER[] = "NEC 00CE 0020";

uint32_t wakeHashes[MAX_WAKE];
uint8_t wakeCount = 0;
char command[40];
uint8_t commandLength = 0;
unsigned long ledOffAt = 0;

uint32_t hashCode(const char *text) {
  uint32_t hash = 2166136261UL;                         // FNV-1a, same as CouchTV
  for (; *text; text++) {
    hash ^= (uint8_t)toupper(*text);
    hash *= 16777619UL;
  }
  return hash;
}

// Builds the stable text for a button, e.g. "NEC 00CE 0001", or "PulseDistance 1A2B3C4D/32" for remotes the
// universal decoder handles (those have no address/command, so the raw bits identify the button).
void describe(const IRData &ir, char *out, size_t size) {
  char name[20];
  strncpy_P(name, (const char *)getProtocolString(ir.protocol), sizeof(name) - 1);
  name[sizeof(name) - 1] = 0;
  if (ir.protocol == PULSE_DISTANCE || ir.protocol == PULSE_WIDTH)
    snprintf(out, size, "%s %08lX/%u", name, (unsigned long)ir.decodedRawData, (unsigned)ir.numberOfBits);
  else
    snprintf(out, size, "%s %04X %04X", name, (unsigned)ir.address, (unsigned)ir.command);
}

bool isWakeButton(const char *code) {
  if (strcmp(code, PHONE_POWER) == 0) return true;
  uint32_t hash = hashCode(code);
  for (uint8_t i = 0; i < wakeCount; i++)
    if (wakeHashes[i] == hash) return true;
  return false;
}

void loadWakeList() {
  wakeCount = 0;
  if (EEPROM.read(0) != EEPROM_MAGIC) return;
  uint8_t count = EEPROM.read(1);
  if (count > MAX_WAKE) return;
  for (uint8_t i = 0; i < count; i++) EEPROM.get(2 + i * 4, wakeHashes[i]);
  wakeCount = count;
}

void saveWakeList() {
  EEPROM.update(0, EEPROM_MAGIC);
  EEPROM.update(1, wakeCount);
  for (uint8_t i = 0; i < wakeCount; i++) EEPROM.put(2 + i * 4, wakeHashes[i]);
}

void blink() {
  digitalWrite(LED_PIN, LOW);
  ledOffAt = millis() + 40;
}

void handleSignal(const IRData &ir) {
  if (ir.protocol == UNKNOWN) return;                                        // noise, or not a remote signal
  if (ir.flags & (IRDATA_FLAGS_PARITY_FAILED | IRDATA_FLAGS_WAS_OVERFLOW)) return;   // garbled frame
  char code[40];
  describe(ir, code, sizeof(code));
  bool repeat = ir.flags & (IRDATA_FLAGS_IS_REPEAT | IRDATA_FLAGS_IS_AUTO_REPEAT);
  blink();

  if (USBDevice.isSuspended()) {                                             // the PC is asleep
    if (!repeat && isWakeButton(code)) USBDevice.wakeupHost();
    return;
  }
  Serial.print(F("IR,"));
  Serial.print(code);
  Serial.println(repeat ? F(",R") : F(",N"));
}

void runCommand(const char *text) {
  if (strcmp(text, "?") == 0) {
    Serial.print(F("COUCHIR "));
    Serial.print(VERSION);
    Serial.print(' ');
    Serial.println(wakeCount);
  } else if (strcmp(text, "WAKE CLEAR") == 0) {
    wakeCount = 0;
    Serial.println(F("OK"));
  } else if (strcmp(text, "WAKE SAVE") == 0) {
    saveWakeList();
    Serial.println(F("OK"));
  } else if (strcmp(text, "WAKE LIST") == 0) {
    for (uint8_t i = 0; i < wakeCount; i++) Serial.println(wakeHashes[i], HEX);
    Serial.println(F("OK"));
  } else if (strncmp(text, "WAKE ", 5) == 0) {
    if (wakeCount < MAX_WAKE) wakeHashes[wakeCount++] = strtoul(text + 5, NULL, 16);
    Serial.println(F("OK"));
  } else if (text[0] != 0) {
    Serial.println(F("ERR"));
  }
}

void readCommands() {
  while (Serial.available() > 0) {
    char c = Serial.read();
    if (c == '\r') continue;
    if (c != '\n') {
      if (commandLength < sizeof(command) - 1) command[commandLength++] = c;
      continue;
    }
    command[commandLength] = 0;
    commandLength = 0;
    runCommand(command);
  }
}

void setup() {
  pinMode(LED_PIN, OUTPUT);
  digitalWrite(LED_PIN, HIGH);
  Serial.begin(115200);
  Keyboard.begin();
  loadWakeList();
  IrReceiver.begin(IR_PIN, DISABLE_LED_FEEDBACK);
}

void loop() {
  if (IrReceiver.decode()) {
    handleSignal(IrReceiver.decodedIRData);
    IrReceiver.resume();
  }
  readCommands();
  if (ledOffAt != 0 && (long)(millis() - ledOffAt) >= 0) {
    digitalWrite(LED_PIN, HIGH);
    ledOffAt = 0;
  }
}
