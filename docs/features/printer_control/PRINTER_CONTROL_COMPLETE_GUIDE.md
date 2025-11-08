# Printer Control Feature - Complete Integration Guide

## Feature Overview

The Printer Control Popout is a persistent, floating control panel available on all pages of PrintStreamer. It provides real-time printer control through both direct service access and a RESTful API.

## Architecture Layers

### Layer 1: UI Components (Blazor)
- **PrinterControlPopout.razor** - Main control panel (bottom-right)
- **MiniGcodeRenderer.razor** - G-code preview renderer
- Features: Temperature controls, G-code sending, preset buttons, status messages

### Layer 2: Client Services
- **PrinterControlApiService** - HTTP client for API communication
- **PrinterConsoleService** - Direct service for console data (existing)

### Layer 3: API Layer
- **PrinterControlController** - RESTful API endpoints at `/api/printer/*`
- 10 endpoints covering all printer operations

### Layer 4: Business Logic
- **PrinterConsoleService** - Core printer communication
- **MoonrakerClient** - Moonraker integration
- Configuration-based settings

## File Structure

```
Components/
├── Shared/
│   ├── PrinterControlPopout.razor          # Main popout component
│   └── MiniGcodeRenderer.razor             # G-code visualization
│
Controllers/
└── PrinterControlController.cs              # API endpoints (/api/printer/*)

Services/
├── PrinterControlApiService.cs              # HTTP client service
├── PrinterConsoleService.cs                 # Console service (existing)
└── [other services...]

wwwroot/
└── js/
    └── gcode-renderer.js                    # Canvas-based G-code renderer

Program.cs                                    # Startup configuration

Documentation/
├── POPOUT_CONTROL_FEATURE.md                # Feature overview
├── PRINTER_CONTROL_API.md                   # API reference
└── API_IMPLEMENTATION_SUMMARY.md            # Implementation details
```

## Data Flow

### Temperature Setting Flow
```
User Input (Component)
    ↓
PrinterControlApiService.SetToolTemperatureAsync()
    ↓
HTTP POST /api/printer/temperature/tool
    ↓
PrinterControlController.SetToolTemperature()
    ↓
PrinterConsoleService.SetToolTemperatureAsync()
    ↓
MoonrakerClient.SendGcodeScriptAsync()
    ↓
Printer
```

### G-Code Rendering Flow
```
Printer Responses
    ↓
PrinterConsoleService (WebSocket subscription)
    ↓
ConsoleLine events
    ↓
MiniGcodeRenderer listens to events
    ↓
Retrieves history via API
    ↓
JavaScript renders on canvas
```

## API Endpoints Reference

| Method | Endpoint | Purpose |
|--------|----------|---------|
| GET | `/api/printer/config` | Get configuration |
| POST | `/api/printer/temperature/tool` | Set tool temp |
| POST | `/api/printer/temperature/bed` | Set bed temp |
| POST | `/api/printer/temperature/set` | Set both temps |
| POST | `/api/printer/temperature/preset` | Apply preset |
| POST | `/api/printer/gcode/send` | Send G-code |
| GET | `/api/printer/console/lines` | Get console output |
| GET | `/api/printer/gcode/history` | Get G-code history |
| GET | `/api/printer/gcode/bounds` | Get rendering bounds |
| GET | `/api/printer/status` | Get connection status |

## Key Features

### 1. Temperature Control
- Individual tool and bed temperature setters
- Material presets: PLA, PETG, ABS, TPU, Nylon, Cooldown
- Real-time validation and feedback
- Configurable temperature limits

### 2. G-Code Command Sending
- Text input for custom G-code
- Enter key support for quick sending
- Command history preservation
- Rate limiting support
- Confirmation requirements for dangerous commands

### 3. G-Code Visualization
- Canvas-based rendering
- Real-time path tracking
- Extrusion vs. rapid move visualization
- Start/end point indicators
- Coordinate bounds display

### 4. UI/UX
- Minimize/expand functionality
- Auto-clearing status messages
- Responsive glassmorphism design
- Cyan accent theme
- Available on all pages

## Configuration

### Temperature Limits
```json
"Stream": {
  "Console": {
    "ToolMaxTemp": 350,
    "BedMaxTemp": 120
  }
}
```

### Rate Limiting
```json
"Stream": {
  "Console": {
    "RateLimit": {
      "CommandsPerMinute": 60
    }
  }
}
```

### Command Restrictions
```json
"Stream": {
  "Console": {
    "DisallowedCommands": ["M999"],
    "RequireConfirmation": ["M104 S400"]
  }
}
```

## Component Lifecycle

### On Page Load
1. PopOut component initializes
2. Loads printer config via API
3. Sets up temperature input limits
4. Registers for console events

### On User Action
1. User enters temperature or G-code
2. Component makes API call
3. Server validates and sends to printer
4. Response displayed in status message
5. Message auto-clears after 3 seconds

### On Printer Response
1. Console receives response
2. Events fired to all subscribers
3. MiniGcodeRenderer extracts G-code
4. JavaScript renders to canvas
5. Bounds calculated and displayed

## Security Considerations

### Current Implementation
- ✅ Temperature range validation
- ✅ Command filtering (disallowed list)
- ✅ Confirmation requirements
- ✅ Rate limiting
- ✅ Input sanitization

### Recommendations
- Add API key authentication
- Implement role-based access control
- Log all printer commands
- Monitor for unusual patterns
- Consider two-factor for dangerous commands

## Performance Optimizations

1. **API Caching**
   - Config loaded once on component init
   - Minimal polling for status

2. **Canvas Rendering**
   - Efficient path drawing with canvas API
   - Bounds calculation limits processing
   - Throttled refresh on new commands

3. **Network Efficiency**
   - Query parameters for filtering
   - Minimal response payloads
   - Async operations prevent blocking

4. **UI Responsiveness**
   - Status messages auto-clear
   - Minimize button for space efficiency
   - Lazy component loading

## Testing Checklist

- [ ] PopOut appears on all pages
- [ ] Minimize/expand works
- [ ] Temperature setters validate input
- [ ] Presets apply correct temperatures
- [ ] G-code commands send successfully
- [ ] Status messages display and clear
- [ ] Mini renderer shows G-code
- [ ] Canvas renders paths correctly
- [ ] API endpoints return correct responses
- [ ] Error handling works properly
- [ ] Rate limiting prevents spam
- [ ] Confirmation dialogs work

## Troubleshooting

### PopOut Not Appearing
1. Check MainLayout.razor has `<PrinterControlPopout />`
2. Verify component is registered in `_Imports.razor`
3. Check browser console for errors

### API Endpoints Returning 404
1. Verify `app.MapControllers()` is in Program.cs
2. Check controller is in correct namespace
3. Verify route attributes are correct

### G-Code Not Rendering
1. Check JavaScript module loads (gcode-renderer.js)
2. Verify canvas element references are correct
3. Check browser console for JS errors

### Temperature Not Setting
1. Verify Moonraker connection is working
2. Check printer console for responses
3. Verify API key is configured if needed

## Future Enhancements

1. **Advanced Features**
   - Pause/Resume print controls
   - Fan speed adjustment
   - Filament runout detection
   - Print time estimation

2. **UI Improvements**
   - 3D G-code preview
   - Command history/favorites
   - Touch-friendly mobile layout
   - Dark/light theme toggle

3. **Integration**
   - WebSocket for real-time updates
   - Webhook notifications
   - Integration with timelapse system
   - Twitch chat command support

4. **Analytics**
   - Command usage statistics
   - Temperature history charts
   - Print success rates
   - API performance metrics

## Support & Documentation

- **API Docs**: See `PRINTER_CONTROL_API.md`
- **Feature Docs**: See `POPOUT_CONTROL_FEATURE.md`
- **Implementation**: See `API_IMPLEMENTATION_SUMMARY.md`
- **Issues**: Check Program.cs and controller logs

## Quick Start

1. **Access the Control**
   - Navigate to any PrintStreamer page
   - Look for floating panel in bottom-right corner

2. **Set Temperature**
   - Enter temperature or click preset button
   - View status message for confirmation

3. **Send G-Code**
   - Type command in text field
   - Press Enter or click Send
   - Monitor console for response

4. **View G-Code Preview**
   - Recent commands automatically render
   - Canvas shows tool path and coordinates

## API Usage Examples

```bash
# Set PLA temperature
curl -X POST "http://localhost:8080/api/printer/temperature/preset?preset=pla"

# Send home command
curl -X POST "http://localhost:8080/api/printer/gcode/send?command=G28"

# Get printer status
curl -X GET "http://localhost:8080/api/printer/status"

# Get recent G-code history
curl -X GET "http://localhost:8080/api/printer/gcode/history?maxLines=50"
```

---

**Status:** ✅ Fully Implemented and Integrated
**Version:** 1.0
**Last Updated:** November 6, 2025
