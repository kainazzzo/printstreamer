# Printer Control Popout Feature

## Overview
A persistent, floating control panel that appears on all pages, allowing users to manage printer controls, send G-code commands, and preview rendered G-code in real-time.

## Components Created

### 1. **PrinterControlPopout.razor** (`/Components/Shared/`)
Main popout control component with the following features:

- **UI Design**: Fixed bottom-right corner with modern cyan/dark theme
- **Minimize/Expand**: Collapsible to a compact 60x60px icon when not in use
- **Temperature Controls**:
  - Independent tool and bed temperature setters
  - Real-time validation against configured max temps
  - Quick preset buttons: PLA (200°C/60°C), PETG (240°C/70°C), ABS (250°C/100°C), Cooldown (0°C/0°C)
- **G-Code Command Sender**:
  - Text input for custom G-code commands
  - Enter key support for quick sending
  - Real-time validation
- **Mini G-Code Renderer**: Embedded preview of recent G-code commands
- **Status Messages**: Contextual feedback with auto-clearing after 3 seconds
- **Responsive Styling**: Custom CSS with glassmorphism effects and smooth transitions

### 2. **MiniGcodeRenderer.razor** (`/Components/Shared/`)
Compact G-code visualization component featuring:

- **Canvas-based Rendering**: High-performance 2D visualization
- **Live Updates**: Subscribes to printer console service for real-time G-code updates
- **Path Tracking**: Displays:
  - Extrusion moves (cyan lines)
  - Rapid moves (faint gray lines)
  - Start point (green dot)
  - End point (red dot)
  - Home position (green dot)
- **Statistics Display**: Shows line count, max X/Y bounds
- **200px Height**: Compact size suitable for the popout

### 3. **gcode-renderer.js** (`/wwwroot/js/`)
JavaScript module for G-code parsing and canvas rendering:

- **G-Code Parser**: Extracts position data from G0, G1, G28, M104, M109, M140, M190 commands
- **Canvas Renderer**: Scales and draws paths with proper transformations
- **Smart Styling**: Different colors and line widths for different move types
- **Grid/Axes**: Visual reference frame with origin markers

## Integration

The popout is integrated into `MainLayout.razor` with interactive server render mode, making it available on all pages:
- Console
- Streaming
- Timelapses
- Audio
- Configuration

## Usage

1. **Temperature Control**:
   - Enter desired temperatures in the tool/bed fields
   - Click "Set" or use preset buttons
   - Status message confirms the action

2. **Send G-Code**:
   - Type G-code command in the input field
   - Press Enter or click "Send"
   - Command appears in the mini renderer

3. **Minimize**:
   - Click the "−" button to collapse the popout
   - Click the collapsed icon to expand

## Styling Features

- **Glassmorphism**: Backdrop blur and transparency effects
- **Cyan Accent Theme**: #00bfff primary color with rgba variations
- **Smooth Animations**: 0.3s transitions for all state changes
- **Custom Scrollbars**: Styled to match the theme
- **Z-index: 9999**: Ensures it's always visible above page content

## Configuration Integration

The component respects the following configuration values:
- `Stream:Console:ToolMaxTemp` (default: 350°C)
- `Stream:Console:BedMaxTemp` (default: 120°C)
- `Stream:Console:*` (command restrictions, rate limiting, etc.)

## Future Enhancements

Potential additions:
- Pause/Resume print controls
- Fan speed control
- Bed leveling assistant
- Print time estimation
- Filament runout detection
- 3D preview mode for gcode renderer
- Command history/favorites
