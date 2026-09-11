#pragma once
// Glowpilot — LED ring renderer (7x WS2812B) + fade transitions

#include <FastLED.h>
#include "colors.h"

static constexpr uint8_t  LED_COUNT    = 7;
static constexpr uint8_t  DATA_PIN     = 7;   // GPIO7 on XIAO ESP32-C3
static constexpr uint32_t FADE_MS      = 400; // cross-fade duration
static constexpr uint32_t PULSE_PERIOD = 2000; // ms for one full sine cycle

void ledsInit();
void ledsSetState(RingState newState);
void ledsUpdate(); // call every loop iteration
