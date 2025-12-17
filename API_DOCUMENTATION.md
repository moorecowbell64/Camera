# Jennov P87HM85-30X-EAS Camera API Documentation

## Overview
- **Brand**: Jennov
- **Model**: P87HM85-30X-EAS
- **Camera IP**: 192.168.50.224
- **API Port**: 12351 (HTTP)
- **Protocol**: JSON-RPC over HTTP
- **Base Firmware**: Dahua-based

## Authentication

### Login
```json
{
  "method": "global.login",
  "params": {
    "userName": "admin",
    "password": "",
    "clientType": "Web3.0"
  },
  "id": 10000
}
```

Response includes a `session` ID that must be included in subsequent requests.

### Logout
```json
{
  "method": "global.logout",
  "params": null,
  "session": <session_id>,
  "id": 10001
}
```

## PTZ Control

### Start PTZ Control
```json
{
  "method": "ptz.start",
  "session": <session_id>,
  "params": {
    "channel": 0,
    ...
  },
  "id": <request_id>
}
```

### Stop PTZ Control
```json
{
  "method": "ptz.stop",
  "session": <session_id>,
  "params": {
    "channel": 0,
    ...
  },
  "id": <request_id>
}
```

### Continuous Movement (Pan/Tilt)
```json
{
  "method": "ptz.moveContinuously",
  "session": <session_id>,
  "params": {
    "speed": [x_speed, y_speed],
    "timeout": <timeout_ms>
  },
  "id": <request_id>
}
```
- `speed`: Array [horizontal, vertical]
- Positive values = right/up, Negative values = left/down
- Speed range typically: -8 to 8

### Move to Screen Coordinates
```json
{
  "method": "ptz.moveDirectly",
  "session": <session_id>,
  "params": {
    "screen": [x_coord, y_coord]
  },
  "id": <request_id>
}
```
- Coordinates appear to be percentages (0-100)

### Stop Movement
```json
{
  "method": "ptz.stopMove",
  "session": <session_id>,
  "params": null,
  "id": <request_id>
}
```

### Get PTZ Status
```json
{
  "method": "ptz.getStatus",
  "session": <session_id>,
  "params": null,
  "id": <request_id>
}
```

## Preset Management

### Get All Presets
```json
{
  "method": "ptz.getPresets",
  "session": <session_id>,
  "params": null,
  "id": <request_id>
}
```

### Set Preset
```json
{
  "method": "ptz.setPreset",
  "session": <session_id>,
  "params": {
    "index": <preset_number>,
    "name": "<preset_name>"
  },
  "id": <request_id>
}
```

### Go to Preset
```json
{
  "method": "ptz.gotoPreset",
  "session": <session_id>,
  "params": {
    "index": <preset_number>,
    "speed": <speed>
  },
  "id": <request_id>
}
```

### Remove Preset
```json
{
  "method": "ptz.removePreset",
  "session": <session_id>,
  "params": {
    "index": <preset_number>
  },
  "id": <request_id>
}
```

## Tour Management

### Get Tours
```json
{
  "method": "ptz.getTours",
  "session": <session_id>,
  "params": null,
  "id": <request_id>
}
```

### Set Tour
```json
{
  "method": "ptz.setTour",
  "session": <session_id>,
  "params": {
    "index": <tour_number>,
    "name": "<tour_name>"
  },
  "id": <request_id>
}
```

### Add Tour Point
```json
{
  "method": "ptz.addTourPoint",
  "session": <session_id>,
  "params": {
    "index": <tour_number>,
    "point": <point_number>,
    "presetIndex": <preset_number>,
    "duration": <duration_seconds>,
    "speed": <speed>
  },
  "id": <request_id>
}
```

### Start/Stop Tour
```json
{
  "method": "ptz.startTour",
  "session": <session_id>,
  "params": {
    "index": <tour_number>
  },
  "id": <request_id>
}

{
  "method": "ptz.stopTour",
  "session": <session_id>,
  "params": {
    "index": <tour_number>
  },
  "id": <request_id>
}
```

## Recording

### Start Recording
```json
{
  "method": "recordManager.start",
  "params": null,
  "session": <session_id>,
  "id": <request_id>
}
```

### Stop Recording
```json
{
  "method": "recordManager.stop",
  "params": null,
  "session": <session_id>,
  "id": <request_id>
}
```

## Snapshot

### Start Snapshot
```json
{
  "method": "snapManager.start",
  "params": null,
  "session": <session_id>,
  "id": <request_id>
}
```

### Stop Snapshot
```json
{
  "method": "snapManager.stop",
  "params": null,
  "session": <session_id>,
  "id": <request_id>
}
```

## Configuration

### Get Config
```json
{
  "method": "configManager.getConfig",
  "params": {
    "name": "<config_name>"
  },
  "session": <session_id>,
  "id": <request_id>
}
```

### Set Config
```json
{
  "method": "configManager.setConfig",
  "params": {
    "name": "<config_name>",
    "table": <config_object>,
    "options": ""
  },
  "session": <session_id>,
  "id": <request_id>
}
```

## Video Streaming

The camera uses HTML5/WebSocket based streaming:
- After login, request `StreamAccess` via SOAP
- Response includes `h5Port` for the HTML5 player
- Video is streamed via WebSocket on the specified port

## Request/Response Format

All requests should be sent as HTTP POST to:
```
http://<camera_ip>:12351/RPC2
```

Headers:
```
Content-Type: application/json
```

Response format:
```json
{
  "result": <result_data>,
  "session": <session_id>,
  "id": <request_id>
}
```

Error response:
```json
{
  "error": {
    "code": <error_code>,
    "message": "<error_message>"
  },
  "id": <request_id>
}
```

## Notes

- Request IDs can be incremented sequentially
- Session ID must be included in all requests after login
- Speed values typically range from -8 to 8
- Preset indices typically start from 1
- Channel is usually 0 for single-camera systems
