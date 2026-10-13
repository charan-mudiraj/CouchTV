/*
  CouchLink - Wi-Fi for the CouchIR receiver box, so any phone paired with the TV (including ones without an IR
  blaster) can switch the TV PC and the TV on and off, and see whether they're on, even while the PC is asleep or off.

  Board:   an ESP32 dev board (the 30-pin "DOIT ESP32 DevKit"). Arduino IDE: Tools > Board > "ESP32 Dev Module".
  Wiring:  to the CouchIR Leonardo, whose relays do the actual pressing:
             ESP32 TX2 (GPIO17)  ->  Leonardo pin 0 (RX)
             ESP32 RX2 (GPIO16)  <-  Leonardo pin 1 (TX), through a divider: 2.2 kOhm from pin 1, and 4.4 kOhm
                                     (two 2.2 kOhm) to GND, so its 5 V signal arrives as 3.3 V
             ESP32 VIN and GND   ->  the Leonardo's 5 V and GND, with a 470 uF capacitor across them for Wi-Fi's
                                     current bursts
  The PC's USB keeps powering it all while the PC is asleep or off (with ErP disabled in the BIOS).

  Wi-Fi: with no Wi-Fi saved, or after its BOOT button is held for 3 seconds, it opens its own network,
  "CouchTV-Setup" (its blue light blinks meanwhile). Join it with a phone, and a page opens (or go to
  http://192.168.4.1) to pick your Wi-Fi and type its password, which is kept on the ESP32 only. With a Wi-Fi saved,
  the setup network closes again after 10 minutes. Nothing else opens it: someone on the Wi-Fi can't change it.

  Security (version 2): it only does what phones paired with the TV ask. CouchTV gives it a key over USB, through
  the Leonardo ("KEY <64 hex digits>"; kept here), and gives the same key to paired phones. Requests carry a tag:
  the first 16 bytes of HMAC-SHA256 with that key, over the request and the board's current challenge (a random
  number it changes every minute and after each Power, so a request can't be replayed). Answers are tagged too, and
  bring the next challenge. Phones talk to it by UDP on port 47710, one line per packet, each answered to the sender:
      COUCHBOARD?               ->  COUCHBOARD 2 couchtv-board          (broadcast, to find it)
      NONCE                     ->  NONCE <challenge>                   (or NOKEY before it has a key)
      STATE <c> <tag>           ->  STATE PC ON|OFF TV ON|OFF ... <next challenge> <tag>
                                    tag("STATE <c>"); the answer's tag("R <c> <answer> <next challenge>")
                                    STATE ? if the Leonardo hasn't answered lately
      POWER <id> <c> <tag>      ->  OK <id> <next challenge> <tag>       tag("POWER <id> <c>"): the Power button.
                                    The same id again within 10 s (a resend, its OK lost) is answered, not pressed.
  A request it can't check is answered "NONCE <challenge>", so a phone whose challenge went stale asks again.
  Power goes to the Leonardo as "POWER BUTTON", which treats it exactly like Power by infrared: it wakes or starts
  the PC (and the TV) if the PC is asleep or off, and otherwise passes it to CouchTV, which puts the PC to sleep.
*/

#include <WiFi.h>
#include <WiFiUdp.h>
#include <WebServer.h>
#include <DNSServer.h>
#include <ESPmDNS.h>
#include <Preferences.h>
#include <esp_system.h>
#include <mbedtls/md.h>

const char VERSION[] = "2";
const char HOST_NAME[] = "couchtv-board";
const char SETUP_NAME[] = "CouchTV-Setup";
const char HTML[] = "text/html; charset=utf-8";   // Wi-Fi names can have emoji in them
const uint16_t UDP_PORT = 47710;
const int LEONARDO_RX = 16, LEONARDO_TX = 17;   // Serial2
const int BOOT_BUTTON = 0;
const int LED = 2;                              // the blue light: blinks while the setup network is open
const unsigned long HOLD_FOR_SETUP_MS = 3000;
const unsigned long SETUP_OPEN_MS = 600000;     // with a Wi-Fi saved, the setup network closes after 10 minutes
const unsigned long ASK_EVERY_MS = 2000;
const unsigned long STATE_FRESH_MS = 6000;
const unsigned long CHALLENGE_MS = 60000;
const unsigned long REPEAT_MS = 10000;

Preferences prefs;
WebServer web(80);
DNSServer dns;
WiFiUDP udp;

String ssid, password;
bool setupOpen = false;
unsigned long setupOpenedAt = 0;
unsigned long bootDownSince = 0;
String fromLeonardo;
String lastState = "STATE ?";
unsigned long lastStateAt = 0;
unsigned long lastAsk = 0;
uint8_t key[32];
bool hasKey = false;
String challenge, previous;   // 32 hex digits each; previous is still good for a moment after a change
unsigned long challengeAt = 0;
uint32_t lastPowerId = 0;
unsigned long lastPowerAt = 0;

// ---------------------------------------------------------------- the key and its tags

void newChallenge() {
  uint8_t bytes[16];
  esp_fill_random(bytes, sizeof(bytes));
  char text[33];
  for (int i = 0; i < 16; i++) sprintf(text + 2 * i, "%02x", bytes[i]);
  previous = challenge;
  challenge = text;
  challengeAt = millis();
}

// The first 16 bytes of HMAC-SHA256 with the key, as 32 hex digits.
String tag(const String &message) {
  uint8_t mac[32];
  mbedtls_md_hmac(mbedtls_md_info_from_type(MBEDTLS_MD_SHA256), key, sizeof(key), (const uint8_t *)message.c_str(), message.length(), mac);
  char text[33];
  for (int i = 0; i < 16; i++) sprintf(text + 2 * i, "%02x", mac[i]);
  return String(text);
}

// Equal, taking the same time wherever they differ.
bool same(const String &a, const String &b) {
  if (a.length() != b.length()) return false;
  uint8_t diff = 0;
  for (unsigned int i = 0; i < a.length(); i++) diff |= a[i] ^ b[i];
  return diff == 0;
}

bool currentChallenge(const String &c) {
  return c.length() == 32 && (c == challenge || (previous.length() == 32 && c == previous));
}

// An answer to a request made with challenge c: tagged, with the next challenge.
String signedAnswer(const String &c, const String &body) {
  return body + " " + challenge + " " + tag("R " + c + " " + body + " " + challenge);
}

int hexValue(char c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'a' && c <= 'f') return c - 'a' + 10;
  if (c >= 'A' && c <= 'F') return c - 'A' + 10;
  return -1;
}

// "KEY <64 hex digits>" from CouchTV, through the Leonardo.
void takeKey(const String &text) {
  if (text.length() != 64) return;
  uint8_t fresh[32];
  for (int i = 0; i < 32; i++) {
    int hi = hexValue(text[2 * i]), lo = hexValue(text[2 * i + 1]);
    if (hi < 0 || lo < 0) return;
    fresh[i] = hi * 16 + lo;
  }
  if (hasKey && memcmp(fresh, key, sizeof(key)) == 0) return;   // the same as before: nothing to write
  memcpy(key, fresh, sizeof(key));
  hasKey = true;
  prefs.putBytes("key", key, sizeof(key));
  newChallenge();
  previous = "";
  Serial.println("Key from CouchTV saved");
}

// ---------------------------------------------------------------- the Leonardo

void tellLeonardo(const char *text) {
  Serial2.print(text);
  Serial2.print('\n');
}

void readLeonardo() {
  while (Serial2.available() > 0) {
    char c = Serial2.read();
    if (c == '\r') continue;
    if (c != '\n') {
      if (fromLeonardo.length() < 160) fromLeonardo += c;
      continue;
    }
    if (fromLeonardo.startsWith("STATE ")) {
      lastState = fromLeonardo;
      lastStateAt = millis();
    } else if (fromLeonardo.startsWith("KEY ")) {
      takeKey(fromLeonardo.substring(4));
    }
    fromLeonardo = "";
  }
}

String state() {
  return millis() - lastStateAt < STATE_FRESH_MS && lastStateAt != 0 ? lastState : String("STATE ?");
}

void power() {
  tellLeonardo("POWER BUTTON");
  Serial.println("Power");
}

// ---------------------------------------------------------------- phones (UDP)

// Splits a request into up to 4 words.
int words(const String &message, String *out) {
  int count = 0, start = 0;
  while (count < 4 && start <= (int)message.length()) {
    int space = message.indexOf(' ', start);
    if (space < 0) space = message.length();
    out[count++] = message.substring(start, space);
    start = space + 1;
  }
  return start <= (int)message.length() ? 5 : count;   // 5: more than 4 words
}

String answer(const String &message) {
  if (message == "COUCHBOARD?") return String("COUCHBOARD 2 ") + HOST_NAME;
  String w[4];
  int n = words(message, w);
  if (w[0] != "NONCE" && w[0] != "STATE" && w[0] != "POWER") return "";
  if (!hasKey) return "NOKEY";
  if (w[0] == "STATE" && n == 3 && currentChallenge(w[1]) && same(w[2], tag("STATE " + w[1]))) return signedAnswer(w[1], state());
  if (w[0] == "POWER" && n == 4 && currentChallenge(w[2]) && same(w[3], tag("POWER " + w[1] + " " + w[2]))) {
    uint32_t id = strtoul(w[1].c_str(), NULL, 10);
    if (id == lastPowerId && millis() - lastPowerAt < REPEAT_MS) return signedAnswer(w[2], "OK " + w[1]);   // a resend
    if (w[2] == challenge) {
      lastPowerId = id;
      lastPowerAt = millis();
      power();
      newChallenge();   // so this request can't be used again
      return signedAnswer(w[2], "OK " + w[1]);
    }
  }
  return "NONCE " + challenge;   // couldn't check it (or its challenge had changed): here's the one to use
}

void handleUdp() {
  int size = udp.parsePacket();
  if (size <= 0) return;
  char buffer[200];
  int n = udp.read(buffer, sizeof(buffer) - 1);
  if (n <= 0) return;
  buffer[n] = 0;
  String message(buffer);
  message.trim();
  String reply = answer(message);
  if (reply.length() == 0) return;
  udp.beginPacket(udp.remoteIP(), udp.remotePort());
  udp.print(reply);
  udp.endPacket();
}

// ---------------------------------------------------------------- web pages

const char STYLE[] PROGMEM = "<meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>"
  "<style>body{font-family:sans-serif;background:#0b0e14;color:#f4f6fa;margin:0;padding:24px;max-width:480px}"
  "h1{font-size:22px}p,label{color:#a3acbc}input,select,button{font-size:18px;width:100%;box-sizing:border-box;"
  "margin:6px 0 14px;padding:12px;border-radius:12px;border:1px solid #3a4458;background:#1c222d;color:#f4f6fa}"
  "button{background:#e5484d;border:0;font-weight:bold}.state{font-size:20px;color:#f4f6fa}</style>";

String escape(const String &text) {
  String out;
  for (unsigned int i = 0; i < text.length(); i++) {
    char c = text[i];
    if (c == '<') out += "&lt;";
    else if (c == '>') out += "&gt;";
    else if (c == '&') out += "&amp;";
    else if (c == '"') out += "&quot;";
    else if (c == '\'') out += "&#39;";
    else out += c;
  }
  return out;
}

// On the home Wi-Fi: nothing anyone could use, and no Power button (that's for paired phones, in the app).
void pageHome() {
  String page = String(FPSTR(STYLE)) + "<title>CouchTV board</title><h1>CouchTV board</h1>";
  page += "<p class=state>Use the CouchTV Remote app on a phone paired with the TV.</p>";
  page += "<p>Key from the TV: " + String(hasKey ? "yes" : "not yet (it comes when CouchTV connects to the receiver)") + "</p>";
  page += "<p>Version " + String(VERSION) + " &middot; Wi-Fi: " + escape(WiFi.SSID()) + " &middot; " + WiFi.localIP().toString() + "</p>";
  page += "<p>To change its Wi-Fi, hold the board's BOOT button for 3 seconds: its blue light blinks, and the CouchTV-Setup network opens for 10 minutes.</p>";
  web.send(200, HTML, page);
}

void pageSetup() {
  if (!setupOpen) {
    pageHome();
    return;
  }
  int found = WiFi.scanNetworks();
  String page = String(FPSTR(STYLE)) + "<title>CouchTV board setup</title><h1>Connect the CouchTV board to your Wi-Fi</h1>";
  page += "<form method=post action=/save accept-charset=utf-8><label>Wi-Fi</label><select name=ssid>";
  for (int i = 0; i < found; i++) {
    String name = WiFi.SSID(i);
    if (name.length() == 0) continue;
    page += "<option value=\"" + escape(name) + "\">" + escape(name) + "</option>";
  }
  page += "</select><label>Password</label><input name=password type=password autocomplete=off>";
  page += "<button style='background:#3ddc84;color:#0b0e14'>Save and connect</button></form>";
  page += "<p>It only works with 2.4 GHz Wi-Fi. The password stays on this board.</p>";
  web.send(200, HTML, page);
}

void pageSave() {
  if (!setupOpen) {
    web.send(403, "text/plain", "Hold the board's BOOT button for 3 seconds first");
    return;
  }
  String newSsid = web.arg("ssid"), newPassword = web.arg("password");
  if (newSsid.length() == 0) {
    web.send(400, HTML, String(FPSTR(STYLE)) + "<p>Pick a Wi-Fi network.</p>");
    return;
  }
  prefs.putString("ssid", newSsid);
  prefs.putString("password", newPassword);
  web.send(200, HTML, String(FPSTR(STYLE)) + "<h1>Saved</h1><p>The board is joining " + escape(newSsid) +
    ". Reconnect this phone to your Wi-Fi. If the board can't join, hold its BOOT button for 3 seconds to try again.</p>");
  delay(1500);
  ESP.restart();
}

// Phones joining the setup network look for a page; any address goes to the setup page.
void pageAny() {
  if (setupOpen) {
    web.sendHeader("Location", String("http://") + WiFi.softAPIP().toString() + "/setup");
    web.send(302);
  } else {
    web.send(404, "text/plain", "Not here");
  }
}

// ---------------------------------------------------------------- Wi-Fi

void openSetup() {
  setupOpenedAt = millis();
  if (setupOpen) return;
  setupOpen = true;
  WiFi.mode(ssid.length() > 0 ? WIFI_AP_STA : WIFI_AP);   // keep trying the saved Wi-Fi meanwhile
  WiFi.softAP(SETUP_NAME);
  dns.start(53, "*", WiFi.softAPIP());
  Serial.print("Setup network open: ");
  Serial.println(WiFi.softAPIP());
}

void closeSetup() {
  if (!setupOpen) return;
  setupOpen = false;
  dns.stop();
  WiFi.softAPdisconnect(true);
  WiFi.mode(WIFI_STA);
  digitalWrite(LED, LOW);
  Serial.println("Setup network closed");
}

void setup() {
  Serial.begin(115200);
  Serial2.begin(115200, SERIAL_8N1, LEONARDO_RX, LEONARDO_TX);
  pinMode(BOOT_BUTTON, INPUT_PULLUP);
  pinMode(LED, OUTPUT);
  digitalWrite(LED, LOW);
  Serial.print("CouchLink ");
  Serial.print(VERSION);
  Serial.print(", MAC ");
  Serial.println(WiFi.macAddress());

  prefs.begin("couchlink", false);
  ssid = prefs.getString("ssid", "");
  password = prefs.getString("password", "");
  hasKey = prefs.getBytes("key", key, sizeof(key)) == sizeof(key);
  newChallenge();
  previous = "";

  WiFi.persistent(false);
  WiFi.setHostname(HOST_NAME);
  WiFi.mode(WIFI_STA);
  if (ssid.length() > 0) {
    WiFi.setAutoReconnect(true);
    WiFi.begin(ssid.c_str(), password.c_str());
  } else {
    openSetup();
  }

  web.on("/", HTTP_GET, []() { if (setupOpen && WiFi.status() != WL_CONNECTED) pageSetup(); else pageHome(); });
  web.on("/setup", HTTP_GET, pageSetup);
  web.on("/save", HTTP_POST, pageSave);
  web.onNotFound(pageAny);
  web.begin();
  udp.begin(UDP_PORT);
  MDNS.begin(HOST_NAME);
}

void loop() {
  unsigned long now = millis();
  if (setupOpen) dns.processNextRequest();
  web.handleClient();
  handleUdp();
  readLeonardo();
  if (now - lastAsk >= ASK_EVERY_MS) {
    lastAsk = now;
    tellLeonardo("STATE");
  }
  if (now - challengeAt >= CHALLENGE_MS) newChallenge();

  // Holding BOOT for 3 seconds opens the setup network: only someone at the box can change its Wi-Fi.
  if (digitalRead(BOOT_BUTTON) == LOW) {
    if (bootDownSince == 0) bootDownSince = now == 0 ? 1 : now;
    else if (now - bootDownSince >= HOLD_FOR_SETUP_MS) openSetup();
  } else {
    bootDownSince = 0;
  }
  if (setupOpen) {
    digitalWrite(LED, (now / 500) % 2 ? HIGH : LOW);
    if (ssid.length() > 0 && now - setupOpenedAt > SETUP_OPEN_MS) closeSetup();
  }
  delay(2);
}
