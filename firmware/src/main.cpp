// Glowpilot firmware — entry point
// Target: Seeed XIAO ESP32-C3
// Framework: Arduino via PlatformIO
//
// Behaviours:
//  1. Boot animation  — slow blue pulse while waiting for first Service frame
//  2. Serial decoder  — reads USB CDC serial, buffers to '\n', parses JSON
//  3. LED renderer    — all 7 LEDs always show the same color
//  4. Fade transitions — ~400 ms smooth cross-fade on state change
//  5. Malformed frame handling — drop + continue, never hang
//  6. Version handshake — respond to {"v":1,"name":"glowpilot-service"}

#include <Arduino.h>
#include "leds.h"
#include "protocol.h"

// Loop period in ms: caps the update rate to ~100 fps for smooth fades/pulses
static constexpr uint32_t LOOP_PERIOD_MS = 10;

void setup() {
    // Initialise USB CDC serial (115200 baud)
    Serial.begin(115200);
    // Give the USB host time to enumerate before sending anything
    delay(200);

    // Initialise LED ring — starts BOOT animation automatically
    ledsInit();
    protocolInit();
}

void loop() {
    // Decode any incoming serial bytes; get new state if a frame arrived
    RingState decoded = protocolUpdate();
    if (decoded != RingState::UNKNOWN) {
        ledsSetState(decoded);
    }

    // Update LED ring (fade progress, pulse, FastLED.show())
    ledsUpdate();

    // Yield to keep fade / pulse smooth (~100 fps cap)
    delay(LOOP_PERIOD_MS);
}
