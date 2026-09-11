// Glowpilot — LED ring renderer
// All 7 LEDs always show the same color.
// Fade: linear interpolation over FADE_MS when state changes.
// Pulse: sinusoidal brightness modulation for BOOT / NO_SESSIONS states.

#include "leds.h"
#include <math.h>

static CRGB  leds[LED_COUNT];

// Fade tracking
static CRGB     fadeFrom   = CRGB::Black;
static CRGB     fadeTo     = CRGB::Black;
static uint32_t fadeStart  = 0;
static bool     inFade     = false;

// Current resolved color (post-fade, pre-pulse)
static CRGB     currentColor = CRGB::Black;

// Current state (for pulse detection)
static RingState currentState = RingState::BOOT;

// ---------------------------------------------------------------------------
// Helpers

// Linear interpolate two CRGB values, blend in [0,255] (0=a, 255=b)
static CRGB lerpColor(CRGB a, CRGB b, uint8_t blend) {
    return CRGB(
        a.r + (((int16_t)b.r - a.r) * blend) / 255,
        a.g + (((int16_t)b.g - a.g) * blend) / 255,
        a.b + (((int16_t)b.b - a.b) * blend) / 255
    );
}

// Apply sinusoidal pulse: brightness oscillates between ~10% and 100%
static CRGB applyPulse(CRGB base) {
    float phase = (float)(millis() % PULSE_PERIOD) / (float)PULSE_PERIOD; // 0..1
    float sineVal = sinf(phase * 2.0f * (float)M_PI);  // -1..1
    float brightness = 0.55f + 0.45f * sineVal;         // 0.1..1.0
    return CRGB(
        (uint8_t)(base.r * brightness),
        (uint8_t)(base.g * brightness),
        (uint8_t)(base.b * brightness)
    );
}

// ---------------------------------------------------------------------------

void ledsInit() {
    FastLED.addLeds<WS2812B, DATA_PIN, GRB>(leds, LED_COUNT);
    FastLED.setBrightness(255); // full brightness so output matches spec-mandated color values
    FastLED.clear(true);

    // Immediately begin boot animation
    currentState = RingState::BOOT;
    currentColor = CRGB::Black;
    fadeFrom     = CRGB::Black;
    fadeTo       = colorForState(RingState::BOOT);
    fadeStart    = millis();
    inFade       = true;
}

void ledsSetState(RingState newState) {
    if (newState == RingState::UNKNOWN) return; // keep current

    if (newState == currentState && !inFade) return; // no change

    currentState = newState;

    // Snapshot current displayed color as fade origin
    if (inFade) {
        uint32_t elapsed = millis() - fadeStart;
        uint8_t blend = (elapsed >= FADE_MS) ? 255
                                             : (uint8_t)((elapsed * 255UL) / FADE_MS);
        fadeFrom = lerpColor(fadeFrom, fadeTo, blend);
    } else {
        fadeFrom = currentColor;
    }

    fadeTo    = colorForState(newState);
    fadeStart = millis();
    inFade    = true;
}

void ledsUpdate() {
    CRGB displayColor;

    if (inFade) {
        uint32_t elapsed = millis() - fadeStart;
        if (elapsed >= FADE_MS) {
            inFade       = false;
            currentColor = fadeTo;
            displayColor = fadeTo;
        } else {
            uint8_t blend = (uint8_t)((elapsed * 255UL) / FADE_MS);
            displayColor  = lerpColor(fadeFrom, fadeTo, blend);
            currentColor  = displayColor;
        }
    } else {
        displayColor = currentColor;
    }

    // Apply pulse overlay for pulsing states
    if (isPulsingState(currentState)) {
        displayColor = applyPulse(displayColor);
    }

    // Write the same color to every LED
    for (uint8_t i = 0; i < LED_COUNT; i++) {
        leds[i] = displayColor;
    }
    FastLED.show();
}
