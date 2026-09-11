# Glowpilot

A physical USB-connected LED ring that gives a developer a glanceable, at-a-distance view of the collective state of their running AI coding agent sessions — without switching terminal windows.

## Language

### Core concepts

**Session**:
A single running instance of an AI coding agent tool (e.g. one Claude Code terminal). Identified by a unique session ID for its lifetime.
_Avoid_: process, agent, instance

**Session State**:
One of three mutually exclusive states a Session can be in: `blocked`, `working`, or `idle`.
_Avoid_: status, mode

**Blocked**:
A Session state meaning the agent has paused and is waiting for the developer to confirm or provide input before it can proceed. The only state that requires immediate developer attention.
_Avoid_: waiting, paused, pending

**Working**:
A Session state meaning the agent is actively executing — running tools, generating output, or processing.
_Avoid_: running, active, busy, thinking

**Idle**:
A Session state meaning the agent has finished its current task and is not doing anything. Includes the moment after a session completes and before it is closed.
_Avoid_: finished, done, stopped, complete

**Closed**:
A terminal event (not a state) signalling that a Session has ended and its slot should be released immediately, without waiting for eviction.
_Avoid_: terminated, exited, dead

**Ring State**:
The single collective visual state currently displayed on the Device. Always reflects the highest-priority active Session state: `blocked` overrides `working`, which overrides `idle`. When no Sessions are active, the Ring State is `no-sessions`.
_Avoid_: display state, LED state, current color

**No-Sessions**:
The Ring State when there are no tracked Sessions. Displayed as a slow blue pulse to indicate the Service is running and the Device is connected, but no agents are active.
_Avoid_: standby, offline, empty

### Components

**Device**:
The physical USB hardware — a microcontroller driving a 7-LED RGB ring behind a diffuser. Treated as a dumb renderer: it receives Ring State commands and renders them; it holds no session knowledge.
_Avoid_: hardware, gadget, ring (when referring to the full unit)

**LED Ring**:
The 7-LED WS2812B addressable ring inside the Device. All LEDs always display the same color, blending through the diffuser into a single light source.
_Avoid_: LEDs, lights, pixels

**Firmware**:
The software running on the Device's microcontroller. Responsible only for decoding the Wire Protocol and driving the LED Ring.

**Service**:
The long-running background process on the developer's machine. Aggregates Session events from Hook Adapters, maintains the Session Map, applies Ring State priority logic, and drives the Device over USB.
_Avoid_: daemon, server, backend, agent

**Hook Adapter**:
A thin executable invoked by a tool's native hook mechanism. Translates a tool-native event into a normalized Session Event and forwards it to the Service. One adapter per supported tool.
_Avoid_: plugin, integration, connector, hook script

**Session Map**:
The Service's in-memory record of all currently tracked Sessions, keyed by session ID. Each entry holds the tool name, current state, and last-seen timestamp. The authoritative source for computing Ring State.
_Avoid_: session store, state map, registry

### Events and protocol

**Session Event**:
The normalized payload a Hook Adapter sends to the Service: session ID, tool name, new state, and an optional closed flag.
_Avoid_: hook event, message, update

**Wire Protocol**:
The newline-delimited JSON format used between the Service and the Device over USB serial. Carries the current Ring State (and tool identity for future use).
_Avoid_: serial protocol, device protocol, USB protocol

**Handshake**:
The exchange that occurs when the Service first connects to the Device. The Service declares its protocol version; the Device replies with its firmware version and a compatibility signal.

**Eviction**:
The act of removing a Session from the Session Map when its last-seen timestamp exceeds the stale timeout. After eviction, the Service recomputes Ring State from the remaining Sessions.
_Avoid_: expiry, timeout, cleanup, pruning

### People and tools

**Tool**:
An AI coding agent application that integrates with Glowpilot by registering a Hook Adapter. Initial tools: Claude Code, GitHub Copilot CLI.
_Avoid_: agent (overloaded), client, app

**Developer**:
The person using Glowpilot — a software developer running one or more AI coding agent sessions on their local machine.
_Avoid_: user (allowed but less precise in this domain)
