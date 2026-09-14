#pragma once
// Glowpilot — color definitions per Ring State
// All colors are CRGB values for FastLED.

#include <FastLED.h>

// Ring states as received from the Wire Protocol
enum class RingState {
    BLOCKED,      // red         #CC0000
    WORKING,      // amber       #FF8800
    IDLE,         // green       #00CC00
    NO_SESSIONS,  // blue pulse  #0044FF
    BOOT,         // blue pulse  #0044FF (startup, before first connection)
    UNKNOWN       // unrecognised state string — keep previous
};

// Base target colors per state
inline CRGB colorForState(RingState state) {
    switch (state) {
        case RingState::BLOCKED:     return CRGB(0xCC, 0x00, 0x00); // red
        case RingState::WORKING:     return CRGB(0xFF, 0x88, 0x00); // amber
        case RingState::IDLE:        return CRGB(0x00, 0xCC, 0x00); // green
        case RingState::NO_SESSIONS: return CRGB(0x00, 0x44, 0xFF); // blue
        case RingState::BOOT:        return CRGB(0x00, 0x44, 0xFF); // blue
        default:                     return CRGB::Black;
    }
}

