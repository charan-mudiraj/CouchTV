/*
  CouchLink - Wi-Fi for the CouchIR receiver box, so any phone (including ones without an IR blaster) can switch
  the TV PC and the TV on and off, and see whether they're on, even while the PC is asleep or off.

  Board:   an ESP32 dev board (the 30-pin "DOIT ESP32 DevKit"). Arduino IDE: Tools > Board > "ESP32 Dev Module".
  Wiring:  to the CouchIR Leonardo, whose relays do the actual pressing:
             ESP32 TX2 (GPIO17)  ->  Leonardo pin 0 (RX)
             ESP32 RX2 (GPIO16)  <-  Leonardo pin 1 (TX), through a divider: 2.2 kOhm from pin 1, and 4.4 kOhm
                                     (two 2.2 kOhm) to GND, so its 5 V signal arrives as 3.3 V
             ESP32 VIN and GND   ->  the Leonardo's 5 V and GND, with a 470 uF capacitor across them for Wi-Fi's
                                     current bursts
  The PC's USB keeps powering it all while the PC is asleep or off (with ErP disabled in the BIOS).

  Wi-Fi: with no Wi-Fi saved (or with the BOOT button held while it starts), it opens its own network,
  "CouchTV-Setup". Join it with a phone, and a page opens (or go to http://192.168.4.1) to pick your Wi-Fi and type
  its password, which is kept on the ESP32 only. If it can't join for 3 minutes (the router's down), it opens the
  setup network as well, while it keeps trying. On your Wi-Fi it's "couchtv-board": http://couchtv-board.local (or
  its address) shows the PC and TV and has a Power button.

  Phones talk to it by UDP on port 47710, one line per packet, each answered to the sender:
      COUCHBOARD?    ->  COUCHBOARD 1 couchtv-board        (broadcast, to find it)
      POWER <id>     ->  OK <id>                            the Power button; a repeat of the same id is ignored,
                                                            so the phone can resend until it hears OK
      STATE          ->  STATE PC ON|OFF TV ON|OFF ...      as the Leonardo last said (it's asked every 2 s);
                         STATE ?                            if the Leonardo hasn't answered lately
  Power goes to the Leonardo as "POWER BUTTON", which treats it exactly like Power by infrared: it wakes or starts
  the PC (and the TV) if the PC is asleep or off, and otherwise passes it to CouchTV, which puts the PC to sleep.
*/

#include <WiFi.h>
#include <WiFiUdp.h>
#include <WebServer.h>
#include <DNSServer.h>
#include <ESPmDNS.h>
#include <Preferences.h>

const char VERSION[] = "1";
const char HOST_NAME[] = "couchtv-board";
const char SETUP_NAME[] = "CouchTV-Setup";
const uint16_t UDP_PORT = 47710;
const int LEONARDO_RX = 16, LEONARDO_TX = 17;   // Serial2
const int BOOT_BUTTON = 0;
const unsigned long JOIN_GIVE_UP_MS = 180000;   // can't join for this long: open the setup network too
const unsigned long ASK_EVERY_MS = 2000;
const unsigned long STATE_FRESH_MS = 6000;

Preferences prefs;
WebServer web(80);
DNSServer dns;
WiFiUDP udp;

String ssid, password;
bool setupOpen = false;
bool setupBecauseLost = false;   // opened because it couldn't join: closes again once it does
unsigned long joinStarted = 0;
String fromLeonardo;
String lastState = "STATE ?";
unsigned long lastStateAt = 0;
unsigned long lastAsk = 0;
uint32_t lastPowerId = 0;

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

void handleUdp() {
  int size = udp.parsePacket();
  if (size <= 0) return;
  char buffer[80];
  int n = udp.read(buffer, sizeof(buffer) - 1);
  if (n <= 0) return;
  buffer[n] = 0;
  String message(buffer);
  message.trim();
  String reply;
  if (message == "COUCHBOARD?") {
    reply = String("COUCHBOARD 1 ") + HOST_NAME;
  } else if (message.startsWith("POWER")) {
    uint32_t id = strtoul(message.c_str() + 5, NULL, 10);
    if (id == 0 || id != lastPowerId) {
      lastPowerId = id;
      power();
    }
    reply = String("OK ") + id;
  } else if (message == "STATE") {
    reply = state();
  }
  if (reply.length() == 0) return;
  udp.beginPacket(udp.remoteIP(), udp.remotePort());
  udp.print(reply);
  udp.endPacket();
}

// ---------------------------------------------------------------- web pages

const char STYLE[] PROGMEM = "<meta name=viewport content='width=device-width,initial-scale=1'>"
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

void pageHome() {
  String s = state();
  String page = String(FPSTR(STYLE)) + "<title>CouchTV board</title><h1>CouchTV board</h1>";
  if (s == "STATE ?") page += "<p class=state>The receiver isn't answering. Check the two wires to the Leonardo.</p>";
  else {
    page += "<p class=state>PC: " + String(s.indexOf("PC ON") >= 0 ? "on" : "off");
    page += " &middot; TV: " + String(s.indexOf("TV ON") >= 0 ? "on" : "off") + "</p>";
  }
  page += "<form method=post action=/power><button>Power</button></form>";
  page += "<p>Wi-Fi: " + escape(WiFi.SSID()) + " &middot; " + WiFi.localIP().toString() + " &middot; MAC " + WiFi.macAddress() + "</p>";
  page += "<p><a style='color:#3ddc84' href=/setup>Change Wi-Fi</a></p>";
  web.send(200, "text/html", page);
}

void pagePower() {
  power();
  web.sendHeader("Location", "/");
  web.send(303);
}

void pageSetup() {
  int found = WiFi.scanNetworks();
  String page = String(FPSTR(STYLE)) + "<title>CouchTV board setup</title><h1>Connect the CouchTV board to your Wi-Fi</h1>";
  page += "<form method=post action=/save><label>Wi-Fi</label><select name=ssid>";
  for (int i = 0; i < found; i++) {
    String name = WiFi.SSID(i);
    if (name.length() == 0) continue;
    page += "<option value=\"" + escape(name) + "\">" + escape(name) + "</option>";
  }
  page += "</select><label>Password</label><input name=password type=password autocomplete=off>";
  page += "<button style='background:#3ddc84;color:#0b0e14'>Save and connect</button></form>";
  page += "<p>It only works with 2.4 GHz Wi-Fi. The password stays on this board.</p>";
  web.send(200, "text/html", page);
}

void pageSave() {
  String newSsid = web.arg("ssid"), newPassword = web.arg("password");
  if (newSsid.length() == 0) {
    web.send(400, "text/html", String(FPSTR(STYLE)) + "<p>Pick a Wi-Fi network.</p>");
    return;
  }
  prefs.putString("ssid", newSsid);
  prefs.putString("password", newPassword);
  web.send(200, "text/html", String(FPSTR(STYLE)) + "<h1>Saved</h1><p>The board is joining " + escape(newSsid) +
    ". Reconnect this phone to your Wi-Fi. If the board can't join, CouchTV-Setup comes back so you can try again.</p>");
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
  setupBecauseLost = false;
  dns.stop();
  WiFi.softAPdisconnect(true);
  WiFi.mode(WIFI_STA);
}

void setup() {
  Serial.begin(115200);
  Serial2.begin(115200, SERIAL_8N1, LEONARDO_RX, LEONARDO_TX);
  pinMode(BOOT_BUTTON, INPUT_PULLUP);
  Serial.print("CouchLink ");
  Serial.print(VERSION);
  Serial.print(", MAC ");
  Serial.println(WiFi.macAddress());

  prefs.begin("couchlink", false);
  ssid = prefs.getString("ssid", "");
  password = prefs.getString("password", "");

  WiFi.persistent(false);
  WiFi.setHostname(HOST_NAME);
  WiFi.mode(WIFI_STA);
  bool askedForSetup = digitalRead(BOOT_BUTTON) == LOW;
  if (ssid.length() > 0) {
    WiFi.setAutoReconnect(true);
    WiFi.begin(ssid.c_str(), password.c_str());
    joinStarted = millis();
  }
  if (ssid.length() == 0 || askedForSetup) openSetup();

  web.on("/", HTTP_GET, []() { if (setupOpen && WiFi.status() != WL_CONNECTED) pageSetup(); else pageHome(); });
  web.on("/power", HTTP_POST, pagePower);
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

  bool joined = WiFi.status() == WL_CONNECTED;
  if (joined) {
    joinStarted = now;
    if (setupOpen && setupBecauseLost) closeSetup();   // back on the home Wi-Fi
  } else if (ssid.length() > 0 && !setupOpen && now - joinStarted > JOIN_GIVE_UP_MS) {
    openSetup();
    setupBecauseLost = true;
  }
  delay(2);
}
