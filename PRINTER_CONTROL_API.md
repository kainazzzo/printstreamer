# Printer Control API Documentation

## Overview

The Printer Control API provides RESTful endpoints for managing printer operations, including temperature control, G-code command sending, and G-code rendering data retrieval. The API is fully integrated with the PopOut Control component and works seamlessly with the `PrinterConsoleService`.

## Base URL

All endpoints are accessible at: `/api/printer`

## Authentication

Currently, no authentication is required. Future versions may add API key authentication.

## Endpoints

### Configuration

#### Get Printer Configuration
```
GET /api/printer/config
```

Returns printer configuration including temperature limits and rate limiting settings.

**Response:**
```json
{
    "toolMaxTemp": 350,
    "bedMaxTemp": 120,
    "rateLimit": {
        "commandsPerMinute": 0
    }
}
```

**Status Codes:**
- `200` - Success
- `500` - Server error

---

### Temperature Control

#### Set Tool Temperature
```
POST /api/printer/temperature/tool?temperature=200&toolIndex=0
```

Sets the tool/nozzle temperature.

**Query Parameters:**
- `temperature` (int, required): Target temperature in °C (0-350)
- `toolIndex` (int, optional): Tool index for multi-tool printers (default: 0)

**Response:**
```json
{
    "success": true,
    "message": "Tool temp set to 200°C",
    "temperature": 200
}
```

**Status Codes:**
- `200` - Success
- `400` - Invalid temperature range
- `500` - Server error

---

#### Set Bed Temperature
```
POST /api/printer/temperature/bed?temperature=60
```

Sets the bed temperature.

**Query Parameters:**
- `temperature` (int, required): Target temperature in °C (0-120)

**Response:**
```json
{
    "success": true,
    "message": "Bed temp set to 60°C",
    "temperature": 60
}
```

**Status Codes:**
- `200` - Success
- `400` - Invalid temperature range
- `500` - Server error

---

#### Set Multiple Temperatures
```
POST /api/printer/temperature/set?toolTemp=200&bedTemp=60&toolIndex=0
```

Sets both tool and bed temperatures in a single request.

**Query Parameters:**
- `toolTemp` (int, optional): Target tool temperature in °C
- `bedTemp` (int, optional): Target bed temperature in °C
- `toolIndex` (int, optional): Tool index (default: 0)

At least one temperature must be specified.

**Response:**
```json
{
    "success": true,
    "message": "Temperature set",
    "toolTemp": 200,
    "bedTemp": 60
}
```

**Status Codes:**
- `200` - Success
- `400` - Invalid parameters or range
- `500` - Server error

---

#### Apply Material Preset
```
POST /api/printer/temperature/preset?preset=pla
```

Applies predefined material temperature presets.

**Query Parameters:**
- `preset` (string, required): Preset name - one of:
  - `pla` - 200°C tool, 60°C bed
  - `petg` - 240°C tool, 70°C bed
  - `abs` - 250°C tool, 100°C bed
  - `tpu` - 220°C tool, 60°C bed
  - `nylon` - 250°C tool, 85°C bed
  - `cooldown` - 0°C tool, 0°C bed

**Response:**
```json
{
    "success": true,
    "message": "Preset applied",
    "preset": "pla",
    "toolTemp": 200,
    "bedTemp": 60
}
```

**Status Codes:**
- `200` - Success
- `400` - Invalid preset
- `500` - Server error

---

### G-Code Commands

#### Send G-Code Command
```
POST /api/printer/gcode/send?command=G28&confirmed=false
```

Sends a raw G-code command to the printer.

**Query Parameters:**
- `command` (string, required, URL-encoded): G-code command to send
- `confirmed` (bool, optional): Whether confirmation has been given (default: false)

**Response (Success):**
```json
{
    "success": true,
    "message": "Sent",
    "command": "G28",
    "timestamp": "2025-11-06T12:34:56Z"
}
```

**Response (Requires Confirmation):**
```json
{
    "success": false,
    "confirmationRequired": true,
    "message": "This command requires confirmation",
    "command": "M104 S300"
}
```

**Status Codes:**
- `200` - Success or confirmation required
- `400` - Invalid command
- `500` - Server error

---

### Console Data

#### Get Recent Console Lines
```
GET /api/printer/console/lines?maxLines=100
```

Retrieves recent console output from the printer.

**Query Parameters:**
- `maxLines` (int, optional): Maximum number of lines to return (default: 100)

**Response:**
```json
{
    "lines": [
        {
            "Timestamp": "2025-11-06T12:34:56Z",
            "Text": "Sent: G28",
            "Level": "info",
            "FromLocal": true
        },
        {
            "Timestamp": "2025-11-06T12:34:57Z",
            "Text": "ok",
            "Level": "info",
            "FromLocal": false
        }
    ]
}
```

**Status Codes:**
- `200` - Success
- `500` - Server error

---

### G-Code Rendering

#### Get G-Code History
```
GET /api/printer/gcode/history?maxLines=1000
```

Retrieves sent G-code commands suitable for rendering in the mini preview.

**Query Parameters:**
- `maxLines` (int, optional): Maximum lines to scan (default: 1000)

**Response:**
```json
{
    "commands": [
        "G28",
        "G0 X10 Y10 Z0.2",
        "G1 X20 Y20 Z0.2 F1200 E5"
    ],
    "count": 3,
    "timestamp": "2025-11-06T12:34:56Z"
}
```

**Status Codes:**
- `200` - Success
- `500` - Server error

---

#### Get G-Code Bounds
```
GET /api/printer/gcode/bounds?maxLines=1000
```

Retrieves bounding box information for the current G-code history for optimizing the renderer view.

**Query Parameters:**
- `maxLines` (int, optional): Maximum lines to scan (default: 1000)

**Response (With Data):**
```json
{
    "hasData": true,
    "bounds": {
        "minX": 0,
        "maxX": 100,
        "minY": 0,
        "maxY": 100
    },
    "dimensions": {
        "width": 100,
        "height": 100
    }
}
```

**Response (No Data):**
```json
{
    "hasData": false,
    "bounds": null,
    "dimensions": null
}
```

**Status Codes:**
- `200` - Success
- `500` - Server error

---

### Status

#### Get Printer Connection Status
```
GET /api/printer/status
```

Checks the printer connection status and gets the latest message.

**Response:**
```json
{
    "connected": true,
    "lastMessage": "Print complete",
    "timestamp": "2025-11-06T12:34:56Z"
}
```

**Status Codes:**
- `200` - Success
- `500` - Server error

---

## Client Service

### PrinterControlApiService

A Blazor service for communicating with the API:

```csharp
@inject PrinterControlApiService PrinterApi

// Get configuration
var config = await PrinterApi.GetConfigAsync();

// Set temperatures
var result = await PrinterApi.SetToolTemperatureAsync(200);
var result = await PrinterApi.SetBedTemperatureAsync(60);
var result = await PrinterApi.SetTemperaturesAsync(200, 60);

// Apply presets
var result = await PrinterApi.ApplyPresetAsync("pla");

// Send G-code
var result = await PrinterApi.SendGcodeAsync("G28");

// Get data
var lines = await PrinterApi.GetConsoleLinesAsync(100);
var history = await PrinterApi.GetGcodeHistoryAsync(1000);
var bounds = await PrinterApi.GetGcodeBoundsAsync(1000);
var status = await PrinterApi.GetStatusAsync();
```

---

## Error Handling

All endpoints return appropriate HTTP status codes:

- `200` - Request successful
- `400` - Bad request (invalid parameters)
- `500` - Server error

Error responses follow this format:
```json
{
    "success": false,
    "error": "Error description"
}
```

---

## Rate Limiting

The API respects rate limiting configured in `appsettings.json`:
```json
{
    "Stream": {
        "Console": {
            "RateLimit": {
                "CommandsPerMinute": 60
            }
        }
    }
}
```

When rate limited, the response will include:
```json
{
    "success": false,
    "error": "Rate limited: try again in 0.5s"
}
```

---

## Configuration

The API behavior is controlled by these configuration settings:

```json
{
    "Stream": {
        "Console": {
            "Enabled": true,
            "AllowSend": true,
            "ToolMaxTemp": 350,
            "BedMaxTemp": 120,
            "DisallowedCommands": ["M999"],
            "RequireConfirmation": ["M104 S400"],
            "RateLimit": {
                "CommandsPerMinute": 60
            }
        }
    },
    "Moonraker": {
        "BaseUrl": "http://192.168.1.2:7125/",
        "ApiKey": "",
        "AuthHeader": "X-Api-Key"
    }
}
```

---

## Integration with PopOut Component

The API is automatically used by the `PrinterControlPopout` component which is included on all pages. The component handles:

- Loading printer config on initialization
- Sending temperature commands via the API
- Sending G-code commands via the API
- Retrieving G-code history for the mini renderer
- Displaying contextual status messages

No additional configuration is required beyond registering the service in `Program.cs`.

---

## Examples

### Set PLA Temperature
```bash
curl -X POST "http://localhost:8080/api/printer/temperature/preset?preset=pla"
```

### Send Home Command
```bash
curl -X POST "http://localhost:8080/api/printer/gcode/send?command=G28"
```

### Get Recent Console
```bash
curl -X GET "http://localhost:8080/api/printer/console/lines?maxLines=50"
```

### Set Tool and Bed Temps
```bash
curl -X POST "http://localhost:8080/api/printer/temperature/set?toolTemp=220&bedTemp=70"
```

