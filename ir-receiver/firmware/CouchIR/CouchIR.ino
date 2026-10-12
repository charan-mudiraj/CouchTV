/*
  CouchIR - a universal infrared receiver for CouchTV, which also switches the PC and the TV on and off.

  Board:    Arduino Pro Micro / Leonardo / Micro (ATmega32U4, 5 V, 16 MHz).
            In the Arduino IDE choose Tools > Board > "Arduino Leonardo".
  Sensor:   Vishay TSOP38238 (38 kHz) with its output on pin 2. See ../../README.md for wiring.
  Library:  "IRremote" by Armin Joachimsmeyer, version 4.x (Tools > Manage Libraries).

  The receiver decodes the buttons of almost any infrared remote (NEC, Samsung, Sony, LG, RC5, RC6, Panasonic,
  JVC, Denon, Sharp, Apple, plus a universal decoder for most others) and reports each one to the PC:

      IR,<code>,N      a new press, e.g.   IR,NEC 00CE 0001,N
      IR,<code>,R      the button is still held (repeat)

  The code text is stable for each button, so CouchTV can map it to an action. CouchTV decides what every button
  does. The receiver only remembers which buttons are "power" buttons, so it can switch the PC on, which CouchTV
  can't do while the PC is asleep or off.

  Power control (version 4), with a 2-channel relay module; without one, the receiver works as before:
    relay 1 (K1, IN1 <- pin 8) "presses" the TV's power button, relay 2 (K2, IN2 <- pin 9) the PC's. Each button's two
    wires also go to its relay's middle (COM) and normally-open (NO) screw terminals, so the buttons keep working.
    (On the common blue module, with the screw terminals at the top, NO is each relay's right-hand terminal.)
  - Power while the PC is asleep or off: it's woken over USB, or, if that can't work (shut down, or after a power
    cut), its power button is pressed; and the TV is switched on.
  - Power while the PC is on is left to CouchTV, which puts it to sleep.
  - The TV follows the PC: when the PC sleeps or shuts down, whatever did it (Power, the sleep timer, idle,
    Windows' menu), the TV is switched off; when the PC wakes or starts, the TV is switched on.
  - The TV's button is a toggle, so the receiver keeps track of whether the TV is on: from what it last did, or,
    with TV sensing (SENSE ON), from the TV's USB port, which only has 5 V while the TV is on. Wiring: the TV USB
    port's 5 V -> 2.2 kOhm -> pin 10, with 10 kOhm from pin 10 to GND, and the port's ground to GND.
  - The PC counts as on while it sends USB frames (one a millisecond). Asleep it stops; shut down, its USB port can
    look like anything (still "configured", never "suspended"), so the frames are what's watched.
  - CouchTV says "OFF SOON" just before it shuts the PC down, and "RESTART SOON" before a restart. Both are saved,
    because the PC's USB power can blink as it goes off and restart the receiver, which would otherwise forget the
    TV was on: after OFF SOON the TV is switched off once the PC is off; after RESTART SOON the TV is left alone
    until the PC is back (each is forgotten after 10 minutes, in case it never happened).
  - The PC's button is never held longer than 0.3 s (holding it 4 s would force the PC off), and a second Power
    press within 5 s is ignored, so a double tap can't undo itself.
  - A relay's coil takes a gulp of current as it clicks. On the PC's standby USB power (PC off) that can dip the
    voltage enough to restart the receiver mid-press. So each press is noted in EEPROM first: a receiver that starts
    up to find the note knows its last press cut its power, counts it (DIED in STATE), and doesn't redo whatever it
    was doing, instead of pressing again and again. The cure is steadier power for the relays: a 470 uF capacitor
    across the relay module's VCC and GND.
  Most relay modules switch on when their input goes LOW; "RELAY HIGH" is for ones that switch on when it's HIGH.

  Nothing is ever sent to the PC while it sleeps: the board's USB code would wake it up to deliver it.

  Commands from the PC, one per line:
      ?              ->  COUCHIR <version> <number of wake buttons>
      WAKE CLEAR     ->  forget the wake buttons (in memory)
      WAKE <hash>    ->  add a wake button: FNV-1a hash of its upper-cased code text, 8 hex digits
      WAKE SAVE      ->  store the list in EEPROM (only changed bytes are written)
      WAKE LIST      ->  print the list
      STATE          ->  STATE PC ON|OFF TV ON|OFF SENSE ON|OFF RELAY LOW|HIGH BOOTS <times started>
                         DIED <times a press cut its power>
      OFF SOON       ->  the PC is about to shut down: switch the TV off once it's off (saved)
      RESTART SOON   ->  the PC is about to restart: leave the TV alone until it's back (saved)
      PRESS PC|TV    ->  press that power button once, to test the wiring
      SENSE ON|OFF   ->  TV sensing on or off (saved)
      RELAY LOW|HIGH ->  the input level that switches the relays on (saved)
      CONTACT PC|TV  ->  finds the right screw terminal: wire the relay's middle terminal to GND and an outer one
                         to pin 10 (nothing else on them); it says whether that one closes on a press (NO, use it)
*/

#define EXCLUDE_EXOTIC_PROTOCOLS   // Bose, Lego, Whynter, FAST: not used by TV remotes; saves flash
#include <IRremote.hpp>
#include <Keyboard.h>              // a keyboard interface is what lets Windows arm the receiver to wake the PC
#include <EEPROM.h>

const uint8_t IR_PIN = 2;
const uint8_t LED_PIN = 17;        // the board's RX LED (lit when LOW): blinks for every button it decodes
const uint8_t TV_RELAY = 8;        // relay module IN1 (K1)
const uint8_t PC_RELAY = 9;        // relay module IN2 (K2)
const uint8_t TV_SENSE = 10;       // the TV's USB 5 V, through 2.2 kOhm, with 10 kOhm to GND
const char VERSION[] = "5.1";

const uint8_t MAX_WAKE = 16;
const uint8_t EEPROM_MAGIC = 0xC7;
const uint8_t SETTINGS_AT = 100, SETTINGS_MAGIC = 0x5E;   // after the wake list (bytes 0 to 65)
const uint8_t FLAG_SENSE = 1, FLAG_RELAY_HIGH = 2;
const uint8_t PLAN_AT = SETTINGS_AT + 2;                  // what CouchTV said the PC is about to do
const uint8_t PLAN_NONE = 0, PLAN_OFF = 0xA1, PLAN_RESTART = 0xA2;
const uint8_t BOOTS_AT = SETTINGS_AT + 3;                 // 2 bytes: times the receiver has started
const uint8_t PRESSING_AT = SETTINGS_AT + 5;              // 1 while a relay is pressing
const uint8_t DIED_AT = SETTINGS_AT + 6;                  // 2 bytes: times the receiver restarted mid-press
// The CouchTV phone remote's power button always wakes the PC, even before CouchTV has sent its list.
const char PHONE_POWER[] = "NEC 00CE 0020";

const unsigned long PC_PRESS_MS = 300;        // a short press: sleep or wake (4 s would force it off)
const unsigned long TV_PRESS_MS = 400;
const unsigned long AWAKE_STABLE_MS = 2000;   // how long the PC's USB must be up before it counts as on...
const unsigned long ASLEEP_STABLE_MS = 4000;  // ...or quiet before it counts as asleep or off
const unsigned long POWER_GAP_MS = 5000;      // a second Power press within this is ignored
const unsigned long WAKE_WAIT_MS = 8000;      // woken over USB but not up by then: press its button
const unsigned long TV_CHECK_MS = 6000;       // with sensing: pressed, but the TV hasn't changed by then: once more
const unsigned long FRAMES_GONE_MS = 50;      // no USB frame for this long: the PC isn't talking
const unsigned long PLAN_MAX_MS = 600000UL;   // OFF SOON / RESTART SOON, but the PC still on (or not back) by then: forget it

uint32_t wakeHashes[MAX_WAKE];
uint8_t wakeCount = 0;
uint8_t flags = 0;
char command[40];
uint8_t commandLength = 0;
unsigned long ledOffAt = 0;

int8_t pcOn = -1;                  // -1 not known yet (just started), 0 asleep or off, 1 on
bool rawAwake = false;             // the PC's USB right now...
unsigned long rawSince = 0;        // ...and since when
bool tvOn = false;                 // what the receiver believes, without sensing
int8_t tvWant = -1;                // with sensing: the state a press is meant to reach, until it's checked
unsigned long tvCheckAt = 0;
uint8_t tvTries = 0;
bool powerUsed = false;
unsigned long lastPower = 0;
unsigned long wakeDeadline = 0;    // 0: no wake-up pending
uint8_t plan = PLAN_NONE;          // OFF SOON or RESTART SOON, until it's done
unsigned long planSince = 0;
uint16_t lastFrame = 0;            // the last USB frame number seen...
unsigned long lastFrameAt = 0;     // ...and when it changed
uint16_t boots = 0;
uint16_t died = 0;

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

void loadSettings() {
  wakeCount = 0;
  if (EEPROM.read(0) == EEPROM_MAGIC) {
    uint8_t count = EEPROM.read(1);
    if (count <= MAX_WAKE) {
      for (uint8_t i = 0; i < count; i++) EEPROM.get(2 + i * 4, wakeHashes[i]);
      wakeCount = count;
    }
  }
  bool known = EEPROM.read(SETTINGS_AT) == SETTINGS_MAGIC;
  flags = known ? EEPROM.read(SETTINGS_AT + 1) : 0;
  plan = known ? EEPROM.read(PLAN_AT) : PLAN_NONE;
  if (plan != PLAN_OFF && plan != PLAN_RESTART) plan = PLAN_NONE;
  if (known) {
    EEPROM.get(BOOTS_AT, boots);
    EEPROM.get(DIED_AT, died);
    if (EEPROM.read(PRESSING_AT) == 1) {
      died++;                      // the last press cut the power: don't redo what it was for
      plan = PLAN_NONE;
    }
  }
  boots++;
  saveFlags();
  EEPROM.put(BOOTS_AT, boots);
  EEPROM.put(DIED_AT, died);
  EEPROM.update(PRESSING_AT, 0);
}

void saveWakeList() {
  EEPROM.update(0, EEPROM_MAGIC);
  EEPROM.update(1, wakeCount);
  for (uint8_t i = 0; i < wakeCount; i++) EEPROM.put(2 + i * 4, wakeHashes[i]);
}

void saveFlags() {
  EEPROM.update(SETTINGS_AT, SETTINGS_MAGIC);
  EEPROM.update(SETTINGS_AT + 1, flags);
  EEPROM.update(PLAN_AT, plan);
}

void setPlan(uint8_t value) {
  plan = value;
  planSince = millis();
  saveFlags();
}

void blink() {
  digitalWrite(LED_PIN, LOW);
  ledOffAt = millis() + 40;
}

// ---------------------------------------------------------------- power

// The PC sends a USB frame every millisecond while it's on; the frame number stops when it sleeps or shuts down.
bool framesArriving() {
  uint16_t frame = UDFNUML | ((uint16_t)UDFNUMH << 8);
  unsigned long now = millis();
  if (frame != lastFrame) {
    lastFrame = frame;
    lastFrameAt = now;
  }
  return now - lastFrameAt < FRAMES_GONE_MS;
}

bool pcAwake() { return USBDevice.configured() && !USBDevice.isSuspended() && framesArriving(); }

// Only while the PC is awake: sending anything while it sleeps would wake it.
void report(const __FlashStringHelper *text) {
  if (pcAwake()) Serial.println(text);
}

uint8_t relayOn() { return (flags & FLAG_RELAY_HIGH) ? HIGH : LOW; }
uint8_t relayOff() { return (flags & FLAG_RELAY_HIGH) ? LOW : HIGH; }

void press(uint8_t relay, unsigned long ms) {
  EEPROM.update(PRESSING_AT, 1);   // cleared after: still there at start-up means this press cut the power
  digitalWrite(relay, relayOn());
  delay(ms);
  digitalWrite(relay, relayOff());
  EEPROM.update(PRESSING_AT, 0);
}

bool sensing() { return flags & FLAG_SENSE; }
bool tvSensed() { return digitalRead(TV_SENSE) == HIGH; }
bool tvIsOn() { return sensing() ? tvSensed() : tvOn; }

void pressTv() {
  press(TV_RELAY, TV_PRESS_MS);
  tvTries++;
  if (sensing()) {
    tvCheckAt = millis() + TV_CHECK_MS;
  } else {
    tvOn = tvWant == 1;
    tvWant = -1;
  }
}

void setTv(bool on) {
  tvTries = 0;
  if (tvIsOn() == on) {
    tvWant = -1;
    return;
  }
  tvWant = on ? 1 : 0;
  pressTv();
  report(on ? F("POWER TV ON") : F("POWER TV OFF"));
}

// Power, while the PC is asleep or off.
void powerOn() {
  unsigned long now = millis();
  if (powerUsed && now - lastPower < POWER_GAP_MS) return;
  powerUsed = true;
  lastPower = now;
  if (USBDevice.wakeupHost()) {
    wakeDeadline = now + WAKE_WAIT_MS;     // asleep, with Windows allowing USB wake-up: check it worked
    if (wakeDeadline == 0) wakeDeadline = 1;
  } else {
    press(PC_RELAY, PC_PRESS_MS);          // shut down, after a power cut, or wake-up not allowed
  }
  setTv(true);
}

void watchPower() {
  unsigned long now = millis();
  bool awake = pcAwake();
  if (awake != rawAwake) {
    rawAwake = awake;
    rawSince = now;
  }
  if (now - rawSince >= (awake ? AWAKE_STABLE_MS : ASLEEP_STABLE_MS) && pcOn != (awake ? 1 : 0)) {
    bool justStarted = pcOn < 0;
    pcOn = awake ? 1 : 0;
    if (justStarted) {
      // Just started. After a power cut the TV is off like the PC, so assume it matches the PC; but if CouchTV
      // said the PC was about to go off or restart, the TV was on, and this start was the PC's USB power blinking.
      tvOn = awake || plan != PLAN_NONE;
      if (plan == PLAN_OFF && !awake) {
        setPlan(PLAN_NONE);
        setTv(false);
      }
      planSince = now;
    } else if (plan == PLAN_RESTART) {
      if (awake) setPlan(PLAN_NONE);       // back from the restart: the TV stayed as it was
    } else {
      if (!awake && plan == PLAN_OFF) setPlan(PLAN_NONE);
      setTv(awake);                        // the TV follows the PC
    }
    if (awake) wakeDeadline = 0;
  }
  if (plan != PLAN_NONE && now - planSince > PLAN_MAX_MS) setPlan(PLAN_NONE);   // it didn't happen after all

  if (wakeDeadline != 0) {
    if (awake) wakeDeadline = 0;
    else if ((long)(now - wakeDeadline) >= 0) {
      wakeDeadline = 0;
      press(PC_RELAY, PC_PRESS_MS);        // the USB wake-up didn't take
    }
  }

  if (tvWant >= 0 && sensing() && (long)(now - tvCheckAt) >= 0) {
    if (tvSensed() == (tvWant == 1)) tvWant = -1;
    else if (tvTries < 2) pressTv();       // a press can be missed; one more, then leave it
    else tvWant = -1;
  }
}

// CONTACT: is the terminal on pin 10 closed at rest, and when the relay is on?
void checkContact(uint8_t relay) {
  pinMode(TV_SENSE, INPUT_PULLUP);
  delay(5);
  bool closedAtRest = digitalRead(TV_SENSE) == LOW;
  digitalWrite(relay, relayOn());
  delay(80);
  bool closedOn = digitalRead(TV_SENSE) == LOW;
  digitalWrite(relay, relayOff());
  pinMode(TV_SENSE, INPUT);
  if (!closedAtRest && closedOn) Serial.println(F("CONTACT NO: closes on a press, use this terminal"));
  else if (closedAtRest && !closedOn) Serial.println(F("CONTACT NC: opens on a press, use the other outer terminal"));
  else if (closedAtRest) Serial.println(F("CONTACT always closed: check the wiring"));
  else Serial.println(F("CONTACT never closes: check the wiring, or try RELAY HIGH"));
}

// ---------------------------------------------------------------- infrared and commands

void handleSignal(const IRData &ir) {
  if (ir.protocol == UNKNOWN) return;                                        // noise, or not a remote signal
  if (ir.flags & (IRDATA_FLAGS_PARITY_FAILED | IRDATA_FLAGS_WAS_OVERFLOW)) return;   // garbled frame
  char code[40];
  describe(ir, code, sizeof(code));
  bool repeat = ir.flags & (IRDATA_FLAGS_IS_REPEAT | IRDATA_FLAGS_IS_AUTO_REPEAT);
  blink();

  if (!pcAwake()) {                                                          // the PC is asleep or off
    if (!repeat && isWakeButton(code)) powerOn();
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
  } else if (strcmp(text, "STATE") == 0) {
    Serial.print(F("STATE PC "));
    Serial.print(pcOn == 1 ? F("ON") : F("OFF"));
    Serial.print(F(" TV "));
    Serial.print(tvIsOn() ? F("ON") : F("OFF"));
    Serial.print(F(" SENSE "));
    Serial.print(sensing() ? F("ON") : F("OFF"));
    Serial.print(F(" RELAY "));
    Serial.print((flags & FLAG_RELAY_HIGH) ? F("HIGH") : F("LOW"));
    Serial.print(F(" BOOTS "));
    Serial.print(boots);
    Serial.print(F(" DIED "));
    Serial.println(died);
  } else if (strcmp(text, "OFF SOON") == 0) {
    setPlan(PLAN_OFF);
    Serial.println(F("OK"));
  } else if (strcmp(text, "RESTART SOON") == 0) {
    setPlan(PLAN_RESTART);
    Serial.println(F("OK"));
  } else if (strcmp(text, "PRESS PC") == 0 || strcmp(text, "PRESS TV") == 0) {
    bool pc = text[6] == 'P';
    press(pc ? PC_RELAY : TV_RELAY, pc ? PC_PRESS_MS : TV_PRESS_MS);
    if (!pc && !sensing()) tvOn = !tvOn;
    Serial.println(F("OK"));
  } else if (strcmp(text, "SENSE ON") == 0 || strcmp(text, "SENSE OFF") == 0) {
    flags = text[7] == 'N' ? (flags | FLAG_SENSE) : (flags & ~FLAG_SENSE);
    saveFlags();
    Serial.println(F("OK"));
  } else if (strcmp(text, "RELAY LOW") == 0 || strcmp(text, "RELAY HIGH") == 0) {
    flags = text[6] == 'H' ? (flags | FLAG_RELAY_HIGH) : (flags & ~FLAG_RELAY_HIGH);
    saveFlags();
    digitalWrite(PC_RELAY, relayOff());
    digitalWrite(TV_RELAY, relayOff());
    Serial.println(F("OK"));
  } else if (strcmp(text, "CONTACT PC") == 0 || strcmp(text, "CONTACT TV") == 0) {
    checkContact(text[8] == 'P' ? PC_RELAY : TV_RELAY);
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
  loadSettings();
  // Relays off before the pins become outputs, so starting up never "presses" a button.
  digitalWrite(PC_RELAY, relayOff());
  digitalWrite(TV_RELAY, relayOff());
  pinMode(PC_RELAY, OUTPUT);
  pinMode(TV_RELAY, OUTPUT);
  pinMode(TV_SENSE, INPUT);
  pinMode(LED_PIN, OUTPUT);
  digitalWrite(LED_PIN, HIGH);
  Serial.begin(115200);
  Keyboard.begin();
  IrReceiver.begin(IR_PIN, DISABLE_LED_FEEDBACK);
}

void loop() {
  if (IrReceiver.decode()) {
    handleSignal(IrReceiver.decodedIRData);
    IrReceiver.resume();
  }
  readCommands();
  watchPower();
  if (ledOffAt != 0 && (long)(millis() - ledOffAt) >= 0) {
    digitalWrite(LED_PIN, HIGH);
    ledOffAt = 0;
  }
}
