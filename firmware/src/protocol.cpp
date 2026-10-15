// Glowpilot — Wire Protocol decoder
//
// Wire Protocol (Service → Firmware): newline-delimited JSON
//   {"state":"blocked","tool":"claude"}\n
//   {"state":"working","tool":"claude"}\n
//   {"state":"idle","tool":"claude"}\n
//   {"state":"no-sessions","tool":""}\n
//
// Version handshake (Service → Firmware):
//   {"v":1,"name":"glowpilot-service"}\n
// Firmware response:
//   {"v":1,"fw":"1.0.0","ok":true}\n
//
// Malformed or unrecognised frames are silently dropped.

#include "protocol.h"
#include <Arduino.h>
#include <ArduinoJson.h>

static char   rxBuf[PROTOCOL_BUF_SIZE];
static size_t rxLen = 0;

void protocolInit() {
    // USB CDC serial is initialised in main.cpp
}

// Parse a complete line (NUL-terminated, without the trailing '\n').
// Returns the decoded RingState or UNKNOWN if the frame should be ignored.
static RingState parseLine(const char* line) {
    // Use a fixed-size JSON document on the stack.
    // 256 bytes is enough for both message types.
    JsonDocument doc;

    DeserializationError err = deserializeJson(doc, line);
    if (err) {
        // Malformed JSON — drop silently
        return RingState::UNKNOWN;
    }

    // --- Version handshake ---
    if (doc["v"].is<int>() && doc["name"].is<const char*>()) {
        int v = doc["v"];
        const char* name = doc["name"];
        if (v == 1 && strcmp(name, "glowpilot-service") == 0) {
            // Respond: {"v":1,"fw":"1.0.0","ok":true}
            Serial.print(F("{\"v\":1,\"fw\":\""));
            Serial.print(FW_VERSION);
            Serial.print(F("\",\"ok\":true}\n"));
            // Service is now connected — exit boot animation by transitioning
            // to NO_SESSIONS (connected but no sessions reported yet).
            return RingState::NO_SESSIONS;
        }
        return RingState::UNKNOWN;
    }

    // --- State frame ---
    if (!doc["state"].is<const char*>()) {
        return RingState::UNKNOWN; // unrecognised frame shape
    }

    const char* state = doc["state"];
    // "tool" field is present for future use — ignored in v1

    if (strcmp(state, "blocked") == 0)        return RingState::BLOCKED;
    if (strcmp(state, "working") == 0)        return RingState::WORKING;
    if (strcmp(state, "working-stale") == 0)  return RingState::WORKING_STALE;
    if (strcmp(state, "idle") == 0)           return RingState::IDLE;
    if (strcmp(state, "no-sessions") == 0)    return RingState::NO_SESSIONS;

    return RingState::UNKNOWN; // unrecognised state value
}

RingState protocolUpdate() {
    // Drain available bytes from USB CDC serial
    while (Serial.available()) {
        char c = (char)Serial.read();

        if (c == '\n' || c == '\r') {
            // End of line — process buffer if non-empty
            if (rxLen > 0) {
                rxBuf[rxLen] = '\0';
                RingState result = parseLine(rxBuf);
                rxLen = 0;
                if (result != RingState::UNKNOWN) {
                    return result;
                }
            }
            continue;
        }

        // Accumulate character; if buffer is full, discard the whole line
        if (rxLen < PROTOCOL_BUF_SIZE - 1) {
            rxBuf[rxLen++] = c;
        } else {
            // Overflow — drop and reset
            rxLen = 0;
        }
    }

    return RingState::UNKNOWN; // no complete frame this iteration
}
