# OBS URLSource Overlay Endpoint

## Overview

The `/stream/obs-urlsource/overlay` endpoint provides real-time printer overlay data in JSON format that is compatible with the [OBS URLSource plugin](https://github.com/royshil/obs-urlsource/tree/0.4.0).

Note: PrintStreamer formats many overlay fields server-side so the OBS side can treat values as presentation-ready strings. Numeric values (temperatures, speed, flow, filament) are rounded and returned as strings to simplify templates in OBS 

## Endpoint

- **URL**: `http://<printstreamer-host>:<port>/stream/obs-urlsource/overlay`
- **Method**: GET
- **Authentication**: Not required
- **Content-Type**: `application/json`

## Response Format

The endpoint returns a JSON object with the following properties:

```json
{
  "nozzle": "200",
  "nozzleTarget": "210",
  "bed": "60.0",
  "bedTarget": "65",
  "state": "printing",
  "progress": "45",
  "layer": "25",
  "layerMax": "100",
  "time": "2025-12-05T14:30:00Z",
  "filename": "benchy.gcode",
  "speed": "50",
  "speedFactor": "100",
  "flow": "100.00",
  "filament": "25.500",
  "filamentType": "PLA",
  "filamentBrand": "Prusament",
  "filamentColor": "Black",
  "filamentName": "Prusament PLA Black",
  "filamentUsedMm": "1234",
  "filamentTotalMm": "5000",
  "slicer": "PrusaSlicer",
  "eta": "2:30 PM",
  "audioName": ""
}
```

## Data Fields

| Field | Type | Description |
|-------|------|-------------|
| `nozzle` | string | Current nozzle temperature as a pre-rounded string (°C) or empty string when unavailable |
| `nozzleTarget` | string | Target nozzle temperature as a pre-rounded string (°C) or empty string when unavailable |
| `bed` | string | Current bed temperature as a pre-rounded string (°C) or empty string when unavailable |
| `bedTarget` | string | Target bed temperature as a pre-rounded string (°C) or empty string when unavailable |
| `state` | string | Printer state (e.g., "printing", "paused", "idle") (empty string when unknown) |
| `progress` | string | Print progress as a pre-rounded percentage string (0-100) or empty string when unavailable |
| `layer` | string | Current layer number as string (empty when not available) |
| `layerMax` | string | Total number of layers as string (empty when not available) |
| `time` | string | Current time in ISO 8601 format (UTC) |
| `filename` | string | Name of the currently printing file (empty when not available) |
| `speed` | string | Current print speed as a pre-rounded string (mm/s) or empty string when unavailable |
| `speedFactor` | string | Speed factor as a pre-rounded percentage string (e.g. "100") or empty string when unavailable |
| `flow` | string | Flow/extrude factor as a pre-rounded string (e.g. "7.16") — returns "0.00" when value is missing |
| `filament` | string | Total filament used in meters as a pre-rounded string (e.g. "9.367") or empty string when unavailable |
| `filamentType` | string | Type of filament (e.g., "PLA", "ABS") (empty when not available) |
| `filamentBrand` | string | Filament brand name (empty when not available) |
| `filamentColor` | string | Filament color (empty when not available) |
| `filamentName` | string | Full filament name (empty when not available) |
| `filamentUsedMm` | string | Filament used in millimeters as a string (empty when not available) |
| `filamentTotalMm` | string | Total filament needed in millimeters as a string (empty when not available) |
| `slicer` | string | Slicer software used (empty when not available) |
| `eta` | string | Estimated time of arrival or display string (e.g. "2:30 PM") (empty when not available) |
| `audioName` | string | Currently playing audio name (empty when no audio is playing) |

## Using with OBS URLSource Plugin

### Step 1: Install OBS URLSource Plugin

Download and install the plugin from [https://github.com/royshil/obs-urlsource/releases](https://github.com/royshil/obs-urlsource/releases)

### Step 2: Add URL/API Source to OBS Scene

1. In OBS, add a new source to your scene: **Source → Add → URL/API Source**
2. Set the URL to your PrintStreamer instance:
   ```
   http://<printstreamer-host>:<port>/stream/obs-urlsource/overlay
   ```

### Step 3: Configure Output Parsing

The plugin can parse the JSON response and extract individual fields using JSONPointer or JSONPath syntax.

#### Example: Display Current Nozzle Temperature

- **Output Parsing Type**: JSON (JSONPointer)
- **Output**: `/nozzle`
- **Output Formatting**: Optional - apply regex post-processing as needed

#### Example: Display Printer State

- **Output Parsing Type**: JSON (JSONPointer)
- **Output**: `/state`

#### Example: Display Print Progress

- **Output Parsing Type**: JSON (JSONPointer)
- **Output**: `/progress`

### Step 4: Add Output Styling and Formatting

Use the plugin's built-in styling options to:
- Set font, size, and color
- Apply regex post-processing to format values (e.g., add units)
- Create dynamic templates using Inja syntax

#### Example: Format Temperature with Unit

- **Output**: `/nozzle`
- **Regex (post-processing)**: `^(.*)$` → `Nozzle: $1°C`

#### Example: Combine Multiple Values

Use the plugin's dynamic templating with multiple extractions:

```
Nozzle: {{output1}}°C / {{output2}}°C | Progress: {{output3}}%
```

Where you configure three separate JSONPointer outputs:
1. `/nozzle` (output1)
2. `/nozzleTarget` (output2)
3. `/progress` (output3)

### Step 5: Configure Update Timer

Set an appropriate update interval in the plugin settings (e.g., 1000ms for 1-second updates).

## Example HTML Template in OBS URLSource

For more advanced formatting, you can use the plugin's HTML rendering capabilities:

```html
<div style="font-family: Arial; color: white; background-color: rgba(0,0,0,0.7); padding: 10px; border-radius: 5px;">
  <div><b>Printer Status</b></div>
  <div>State: {{output1}}</div>
  <div>Progress: {{output2}}%</div>
  <div>Nozzle: {{output3}}°C / {{output4}}°C</div>
  <div>Bed: {{output5}}°C / {{output6}}°C</div>
</div>
```

## Example JSON Extraction with Inja Templates

The plugin supports the Inja templating engine for advanced output templating:

```
Nozzle: {{body.nozzle | int}}°C
```

This will extract and format the nozzle temperature from the entire JSON response body.

## Update Frequency

The endpoint queries Moonraker for current printer state. Configure your preferred update frequency in the OBS URLSource plugin settings. PrintStreamer itself has a configurable refresh interval (`Overlay:RefreshMs`) which controls how often the overlay snapshot is refreshed.

```json
{
  "Overlay": {
    "RefreshMs": 1000
  }
}
```

## Error Handling

If an error occurs while fetching data, the endpoint will return:

```json
{
  "error": "error description"
}
```

With HTTP status code 500.

## Performance Considerations

- The endpoint makes requests to Moonraker on each call
- Default refresh interval is 1000ms, but can be adjusted
- The plugin will make requests at the frequency specified in its update timer settings
- Recommended update interval: 1000ms (1 second) for balance between freshness and server load

## Requirements

- PrintStreamer running and properly configured with Moonraker connection
- OBS URLSource plugin v0.4.0 or later installed
- Network connectivity between OBS and PrintStreamer instances
