// Glowpilot — LED ring renderer
//
// Animations per ring state:
//   WORKING     — spinning dot with 2-LED trailing tail (amber, clockwise)
//   BLOCKED     — fast sinusoidal pulse, all LEDs (red — signals user action needed)
//   IDLE        — solid, all LEDs (green — done, waiting for input)
//   NO_SESSIONS — slow sinusoidal pulse, all LEDs (blue)
//   BOOT        — slow sinusoidal pulse, all LEDs (blue, before first connection)
//
// State changes trigger a per-LED cross-fade from the previous output snapshot.

#include "leds.h"
#include <math.h>

static CRGB      leds[LED_COUNT];
static CRGB      fadePrev[LED_COUNT];
static uint32_t  fadeStart   = 0;
static bool      inFade      = false;
static RingState currentState = RingState::BOOT;

// ---------------------------------------------------------------------------
// Helper

static CRGB lerpColor(CRGB a, CRGB b, uint8_t blend) {
    return CRGB(
        a.r + (((int16_t)b.r - a.r) * blend) / 255,
        a.g + (((int16_t)b.g - a.g) * blend) / 255,
        a.b + (((int16_t)b.b - a.b) * blend) / 255
    );
}

// ---------------------------------------------------------------------------
// Target computation — fills out[LED_COUNT] with the desired colors for the
// current state at the current moment in time (millis()).

static void computeTarget(CRGB* out) {
    CRGB base = colorForState(currentState);

    switch (currentState) {

        case RingState::WORKING: {
            // One dot travels clockwise; two LEDs behind it form a fading tail.
            // spinPos is the fractional LED position of the dot head (0 .. LED_COUNT).
            float spinPos = (float)(millis() % SPIN_PERIOD_MS)
                            / (float)SPIN_PERIOD_MS
                            * (float)LED_COUNT;
            for (uint8_t i = 0; i < LED_COUNT; i++) {
                // Angular distance *behind* the head (0 = head, increases clockwise).
                float dist = fmodf((float)LED_COUNT + spinPos - (float)i, (float)LED_COUNT);
                if (dist < 1.0f) {
                    out[i] = base;                                      // head: full
                } else if (dist < 3.0f) {
                    float t = 1.0f - (dist - 1.0f) / 2.0f;            // tail: linear fade
                    out[i] = CRGB((uint8_t)(base.r * t),
                                  (uint8_t)(base.g * t),
                                  (uint8_t)(base.b * t));
                } else {
                    out[i] = CRGB::Black;                               // dark
                }
            }
            break;
        }

        case RingState::BLOCKED: {
            // Fast sinusoidal pulse — all LEDs identical brightness.
            float phase      = (float)(millis() % BLINK_PERIOD_MS) / (float)BLINK_PERIOD_MS;
            float brightness = 0.1f + 0.9f * (0.5f + 0.5f * sinf(phase * 2.0f * (float)M_PI));
            CRGB  c = CRGB((uint8_t)(base.r * brightness),
                           (uint8_t)(base.g * brightness),
                           (uint8_t)(base.b * brightness));
            for (uint8_t i = 0; i < LED_COUNT; i++) out[i] = c;
            break;
        }

        case RingState::IDLE: {
            // Solid — no animation. All LEDs at full color.
            for (uint8_t i = 0; i < LED_COUNT; i++) out[i] = base;
            break;
        }

        case RingState::NO_SESSIONS:
        case RingState::BOOT:
        default: {
            // Slow sinusoidal pulse — soft ambient presence, not urgent.
            float phase      = (float)(millis() % PULSE_PERIOD_MS) / (float)PULSE_PERIOD_MS;
            float brightness = 0.1f + 0.9f * (0.5f + 0.5f * sinf(phase * 2.0f * (float)M_PI));
            CRGB  c = CRGB((uint8_t)(base.r * brightness),
                           (uint8_t)(base.g * brightness),
                           (uint8_t)(base.b * brightness));
            for (uint8_t i = 0; i < LED_COUNT; i++) out[i] = c;
            break;
        }
    }
}

// ---------------------------------------------------------------------------

void ledsInit() {
    FastLED.addLeds<WS2812B, DATA_PIN, GRB>(leds, LED_COUNT);
    FastLED.setBrightness(255);
    FastLED.clear(true);

    currentState = RingState::BOOT;
    for (uint8_t i = 0; i < LED_COUNT; i++) {
        fadePrev[i] = CRGB::Black;
        leds[i]     = CRGB::Black;
    }
    fadeStart = millis();
    inFade    = true;
}

void ledsSetState(RingState newState) {
    if (newState == RingState::UNKNOWN) return;
    if (newState == currentState && !inFade) return;

    // Snapshot the currently displayed output as the fade origin.
    for (uint8_t i = 0; i < LED_COUNT; i++) fadePrev[i] = leds[i];

    currentState = newState;
    fadeStart    = millis();
    inFade       = true;
}

void ledsUpdate() {
    CRGB target[LED_COUNT];
    computeTarget(target);

    if (inFade) {
        uint32_t elapsed = millis() - fadeStart;
        if (elapsed >= FADE_MS) {
            inFade = false;
            for (uint8_t i = 0; i < LED_COUNT; i++) leds[i] = target[i];
        } else {
            uint8_t blend = (uint8_t)((elapsed * 255UL) / FADE_MS);
            for (uint8_t i = 0; i < LED_COUNT; i++) {
                leds[i] = lerpColor(fadePrev[i], target[i], blend);
            }
        }
    } else {
        for (uint8_t i = 0; i < LED_COUNT; i++) leds[i] = target[i];
    }

    FastLED.show();
}
