#pragma once
// Glowpilot — LED ring renderer (7x WS2812B) + per-state animations

#include <FastLED.h>
#include "colors.h"

static constexpr uint8_t  LED_COUNT        = 7;
static constexpr uint8_t  DATA_PIN         = 7;    // GPIO7 on XIAO ESP32-C3
static constexpr uint32_t FADE_MS          = 400;  // cross-fade on state change
static constexpr uint32_t SPIN_PERIOD_MS   = 1000; // one full rotation (WORKING)
static constexpr uint32_t BLINK_PERIOD_MS  = 600;  // fast sine blink (BLOCKED)
static constexpr uint32_t STALE_PERIOD_MS  = 2000; // slow sine pulse (WORKING_STALE)
static constexpr uint32_t PULSE_PERIOD_MS  = 2000; // slow sine pulse (NO_SESSIONS/BOOT)

void ledsInit();
void ledsSetState(RingState newState);
void ledsUpdate(); // call every loop iteration
