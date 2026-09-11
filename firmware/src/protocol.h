#pragma once
// Glowpilot — Wire Protocol decoder + version handshake
// Reads newline-delimited JSON from USB CDC serial.
// Malformed frames are silently dropped.

#include "colors.h"

// Maximum line buffer length (bytes, including '\n')
static constexpr size_t PROTOCOL_BUF_SIZE = 256;

// Firmware version string sent in handshake response
static constexpr char FW_VERSION[] = "1.0.0";

// Initialise: nothing to do currently, kept for symmetry
void protocolInit();

// Call each loop iteration. Returns the decoded state if a complete,
// valid frame arrived; returns RingState::UNKNOWN otherwise so the
// caller can skip rendering.
RingState protocolUpdate();
