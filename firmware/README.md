# Glowpilot Firmware

Firmware for the Glowpilot device — Seeed XIAO ESP32-C3 driving a 7-LED WS2812B ring through USB CDC serial.

## Requirements

- [PlatformIO CLI](https://docs.platformio.org/en/latest/core/installation/index.html) or [PlatformIO IDE](https://platformio.org/install/ide?install=vscode) (VS Code extension)
- USB cable (USB-C)
- The device must be in normal or bootloader mode

## Flashing

```bash
# From the firmware/ directory:
pio run --target upload
```

PlatformIO downloads the `espressif32` platform and libraries on first run.

To specify a port explicitly:

```bash
pio run --target upload --upload-port COM3       # Windows
pio run --target upload --upload-port /dev/ttyUSB0  # Linux
```

## Building (without uploading)

```bash
pio run
```

A successful build prints `[SUCCESS]` with binary sizes.

## Monitoring serial output

```bash
pio device monitor
```

Baud rate is set to 115200 in `platformio.ini`.

## Testing standalone (boot animation)

1. Flash the firmware.
2. Disconnect and reconnect USB (or press RESET).
3. The ring should immediately start a slow blue pulse — this is the **boot animation**, indicating the MCU is running but no Service connection has been established yet.
4. No computer-side software is required for this test.

## Testing the Wire Protocol manually

Open any serial terminal at 115200 baud and send newline-terminated JSON:

```
{"state":"idle","tool":""}
{"state":"working","tool":"claude"}
{"state":"blocked","tool":"claude"}
{"state":"no-sessions","tool":""}
```

Observe the LED ring change color accordingly.

### Version handshake

Send:
```
{"v":1,"name":"glowpilot-service"}
```

The firmware replies:
```
{"v":1,"fw":"1.0.0","ok":true}
```

### Malformed frame handling

Any invalid JSON or unknown state string is silently dropped:
```
not json at all
{"garbage":true}
{"state":"unknown-value"}
```

The ring keeps its last known state — it never freezes or crashes.

## Hardware wiring

| Signal | XIAO GPIO | Notes |
|--------|-----------|-------|
| WS2812B data | GPIO7 | Can be changed in `leds.h` (`DATA_PIN`) |
| VCC | 3.3 V or 5 V | Check your ring's datasheet |
| GND | GND | |

## USB identification

The device enumerates with:

| Field | Value |
|-------|-------|
| VID | `0x2886` (Seeed Studio) |
| PID | `0x0045` |

These are set via `build_flags` in `platformio.ini` and are used by the Background Service to locate the correct COM port.
