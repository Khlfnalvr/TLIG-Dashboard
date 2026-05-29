/*
 * ESP32 BMS-to-PC Serial Bridge
 * ==============================
 *
 * Reference firmware for the ESP32 acting as BMS master:
 *   1. Reads cell voltages / temperatures / current / SOC from the BMS
 *      slaves on the internal CAN bus (TWAI on ESP32).
 *   2. Aggregates one full snapshot per sampling period.
 *   3. Emits the snapshot as a single NDJSON line over the USB-CDC /
 *      UART0 serial port.
 *
 * The Windows TLIGDashboard app expects NDJSON in this shape, one object per
 * line, terminated with '\n':
 *
 *   {"v":53.12,"i":-2.5,"soc":78,"st":"discharging",
 *    "cells":[3.682,3.681,3.680,3.682,3.681,3.682,3.683,3.680,
 *             3.681,3.682,3.682,3.681,3.680,3.682,3.683,3.681,
 *             3.682,3.682,3.681,3.680],
 *    "temps":[28,29,30,29,28,28,29,30,29,28],
 *    "bal":[0,5,12]}
 *
 * Fields:
 *   v      pack voltage,  volts (double)
 *   i      pack current,  amps (signed; + = charging, - = discharging)
 *   soc    state of charge, percent (0..100)
 *   st     status enum: "idle"|"charging"|"discharging"|"fault"|"balancing"
 *   cells  array of 20 cell voltages, volts
 *   temps  array of 10 NTC temperatures, °C (int or double)
 *   bal    array of 0-indexed cells currently balancing (may be empty)
 *
 * Wiring (typical):
 *   ESP32  ─┬─  USB to PC                         (serial bridge)
 *           └─  GPIO5  TX→CTX  TJA1050 transceiver → CAN_H/CAN_L → BMS slaves
 *                GPIO4  RX←CRX
 *
 * Required Arduino libraries:
 *   • ArduinoJson  (Benoit Blanchon)        — JSON serialisation
 *   • TWAI driver is built into the ESP32 Arduino core (no extra install)
 *
 * Default baud: 115200 — change TLIGDashboard's UI dropdown if you use another.
 *
 * The CAN-side reading is highly specific to your BMS slaves. The skeleton
 * below uses simulated values; replace `pollBmsSlaves()` with your actual
 * TWAI receive + protocol decode for the slave boards you have.
 */

#include <ArduinoJson.h>
#include "driver/twai.h"   // ESP-IDF TWAI (CAN) driver

// ── User-tunable ───────────────────────────────────────────────────────────
static const int      SERIAL_BAUD       = 115200;
static const uint32_t SAMPLE_PERIOD_MS  = 1000;     // 1 Hz snapshot rate
static const gpio_num_t CAN_TX_PIN      = GPIO_NUM_5;
static const gpio_num_t CAN_RX_PIN      = GPIO_NUM_4;
static const uint32_t   CAN_BITRATE_HZ  = 500000;   // BMS slaves' bus speed

// ── BMS snapshot in RAM ────────────────────────────────────────────────────
struct BmsSnapshot {
    float    pack_voltage = 0;
    float    current      = 0;
    float    soc          = 0;
    const char* status    = "idle";
    float    cells[20]    = {0};
    int8_t   temps[10]    = {0};
    uint32_t balancing    = 0;   // bit i = cell (i+0) balancing
};

BmsSnapshot bms;

// ── Setup ──────────────────────────────────────────────────────────────────
void setup() {
    Serial.begin(SERIAL_BAUD);
    while (!Serial && millis() < 2000) { /* wait for USB-CDC */ }

    // Bring up TWAI (CAN) at 500 kbit/s in NORMAL mode
    twai_general_config_t g = TWAI_GENERAL_CONFIG_DEFAULT(CAN_TX_PIN, CAN_RX_PIN, TWAI_MODE_NORMAL);
    twai_timing_config_t  t = TWAI_TIMING_CONFIG_500KBITS();
    twai_filter_config_t  f = TWAI_FILTER_CONFIG_ACCEPT_ALL();
    if (twai_driver_install(&g, &t, &f) == ESP_OK) {
        twai_start();
    } else {
        // CAN driver failed — we still emit JSON so the PC sees something.
        // (In production, also set status = "fault" until CAN is healthy.)
    }
}

// ── Main loop ──────────────────────────────────────────────────────────────
void loop() {
    static uint32_t last_emit = 0;

    pollBmsSlaves();                  // drain TWAI RX into `bms`

    if (millis() - last_emit >= SAMPLE_PERIOD_MS) {
        last_emit = millis();
        emitSnapshot(bms);
    }
}

// ── CAN-side polling (replace with your slaves' protocol) ─────────────────
void pollBmsSlaves() {
    twai_message_t msg;
    // Drain all pending frames; bail when the queue is empty.
    while (twai_receive(&msg, 0) == ESP_OK) {
        // TODO: decode your slaves' frames into the `bms` snapshot fields.
        // The example below is a placeholder that does nothing.
        (void)msg;
    }

    // ── Demo data (remove once real decoding is in place) ──
    // Lets you smoke-test the PC app without real slaves attached.
    static uint32_t demo_tick = 0;
    if (millis() - demo_tick > 1000) {
        demo_tick = millis();
        bms.pack_voltage = 53.0f + 0.1f * sinf(millis() * 0.001f);
        bms.current      = -2.5f;
        bms.soc          = 78.0f;
        bms.status       = "discharging";
        for (int i = 0; i < 20; i++) bms.cells[i] = 3.680f + 0.003f * ((i + (millis()/500)) % 5);
        for (int i = 0; i < 10; i++) bms.temps[i] = 28 + (i % 3);
        bms.balancing = (1u << 0) | (1u << 5) | (1u << 12);
    }
}

// ── JSON serialisation ─────────────────────────────────────────────────────
void emitSnapshot(const BmsSnapshot& s) {
    // 1024 bytes comfortably fits the full snapshot (~300 B in practice).
    StaticJsonDocument<1024> doc;

    doc["v"]   = round2(s.pack_voltage);
    doc["i"]   = round2(s.current);
    doc["soc"] = round1(s.soc);
    doc["st"]  = s.status;

    JsonArray cells = doc.createNestedArray("cells");
    for (int i = 0; i < 20; i++) cells.add(round3(s.cells[i]));

    JsonArray temps = doc.createNestedArray("temps");
    for (int i = 0; i < 10; i++) temps.add(s.temps[i]);

    JsonArray bal = doc.createNestedArray("bal");
    for (int i = 0; i < 20; i++) if (s.balancing & (1u << i)) bal.add(i);

    serializeJson(doc, Serial);
    Serial.write('\n');
}

// Rounding helpers — keeps the JSON compact and human-readable
static inline float round1(float x) { return roundf(x * 10.f)    / 10.f; }
static inline float round2(float x) { return roundf(x * 100.f)   / 100.f; }
static inline float round3(float x) { return roundf(x * 1000.f)  / 1000.f; }
