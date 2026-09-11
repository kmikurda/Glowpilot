#pragma once
// Glowpilot — color definitions per Ring State
// All colors are CRGB values for FastLED.

#include <FastLED.h>

// Ring states as received from the Wire Protocol
enum class RingState {
    BLOCKED,      // red-purple  #CC0055
    WORKING,      // amber       #FF8800
    IDLE,         // mint-green  #00CC66
    NO_SESSIONS,  // soft-blue pulse #0044FF
    BOOT,         // slow blue pulse (startup, before first connection)
    UNKNOWN       // unrecognised state string — keep previous
};

// Base target colors per state
inline CRGB colorForState(RingState state) {
    switch (state) {
        case RingState::BLOCKED:     return CRGB(0xCC, 0x00, 0x55);
        case RingState::WORKING:     return CRGB(0xFF, 0x88, 0x00);
        case RingState::IDLE:        return CRGB(0x00, 0xCC, 0x66);
        case RingState::NO_SESSIONS: return CRGB(0x00, 0x44, 0xFF);
        case RingState::BOOT:        return CRGB(0x00, 0x44, 0xFF);
        default:                     return CRGB::Black;
    }
}

// States that use continuous pulsing instead of a solid color
inline bool isPulsingState(RingState state) {
    return state == RingState::NO_SESSIONS || state == RingState::BOOT;
}
