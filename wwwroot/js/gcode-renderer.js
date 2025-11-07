/**
 * G-Code Preview Renderer
 * Renders G-code commands on a canvas for visualization
 */

export function renderGcodePreview(canvasElement, gcodeCommands) {
    const canvas = canvasElement;
    if (!canvas) return { maxX: 0, maxY: 0 };

    const ctx = canvas.getContext('2d');
    if (!ctx) return { maxX: 0, maxY: 0 };

    // Set canvas size to match container
    canvas.width = canvas.offsetWidth;
    canvas.height = canvas.offsetHeight;

    // Clear canvas
    ctx.fillStyle = 'rgba(0, 0, 0, 0.6)';
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    // Parse and render G-code
    let x = 0, y = 0;
    let maxX = 0, maxY = 0;
    let minX = 0, minY = 0;
    const positions = [];
    let isExtracting = true; // Start with extrusion (colored)

    for (const cmd of gcodeCommands) {
        const upperCmd = cmd.toUpperCase();

        // G0 = rapid move, G1 = linear move
        if (upperCmd.startsWith('G0') || upperCmd.startsWith('G1')) {
            const newPos = parseGcodePosition(cmd, x, y);
            if (newPos) {
                positions.push({ x, y, type: isExtracting ? 'extrude' : 'move' });
                x = newPos.x;
                y = newPos.y;
                positions.push({ x, y, type: isExtracting ? 'extrude' : 'move' });
                
                maxX = Math.max(maxX, x);
                maxY = Math.max(maxY, y);
                minX = Math.min(minX, x);
                minY = Math.min(minY, y);
            }
        }
        // G28 = home
        else if (upperCmd.startsWith('G28')) {
            x = 0;
            y = 0;
            positions.push({ x, y, type: 'home' });
        }
        // M104/M109 = set tool temp (extrusion)
        else if (upperCmd.startsWith('M104') || upperCmd.startsWith('M109')) {
            isExtracting = true;
        }
        // M140/M190 = set bed temp
        else if (upperCmd.startsWith('M140') || upperCmd.startsWith('M190')) {
            // Don't change extraction state
        }
    }

    // Draw the positions
    if (positions.length > 0) {
        drawPath(ctx, positions, canvas.width, canvas.height, minX, minY, maxX, maxY);
    }

    // Draw grid/origin
    drawGrid(ctx, canvas.width, canvas.height, minX, minY, maxX, maxY);

    return { maxX, maxY };
}

function parseGcodePosition(cmd, currentX, currentY) {
    let x = currentX;
    let y = currentY;
    let hasX = false, hasY = false;

    // Extract X coordinate
    const xMatch = cmd.match(/X([-\d.]+)/i);
    if (xMatch) {
        x = parseFloat(xMatch[1]);
        hasX = true;
    }

    // Extract Y coordinate
    const yMatch = cmd.match(/Y([-\d.]+)/i);
    if (yMatch) {
        y = parseFloat(yMatch[1]);
        hasY = true;
    }

    return hasX || hasY ? { x, y } : null;
}

function drawPath(ctx, positions, canvasWidth, canvasHeight, minX, minY, maxX, maxY) {
    // Calculate scaling with padding
    const padding = 20;
    const usableWidth = canvasWidth - 2 * padding;
    const usableHeight = canvasHeight - 2 * padding;

    const rangeX = Math.max(maxX - minX, 1);
    const rangeY = Math.max(maxY - minY, 1);

    const scaleX = usableWidth / rangeX;
    const scaleY = usableHeight / rangeY;
    const scale = Math.min(scaleX, scaleY);

    function canvasX(x) {
        return padding + (x - minX) * scale;
    }

    function canvasY(y) {
        return canvasHeight - padding - (y - minY) * scale;
    }

    // Draw paths
    ctx.lineWidth = 1.5;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';

    let currentType = null;

    for (let i = 0; i < positions.length; i++) {
        const pos = positions[i];
        const cx = canvasX(pos.x);
        const cy = canvasY(pos.y);

        if (currentType !== pos.type) {
            // Change line style
            switch (pos.type) {
                case 'extrude':
                    ctx.strokeStyle = '#00bfff';
                    ctx.lineWidth = 1.5;
                    break;
                case 'move':
                    ctx.strokeStyle = '#666';
                    ctx.lineWidth = 0.5;
                    break;
                case 'home':
                    ctx.fillStyle = '#6bcf7f';
                    ctx.beginPath();
                    ctx.arc(cx, cy, 4, 0, Math.PI * 2);
                    ctx.fill();
                    continue;
            }

            if (i > 0) {
                ctx.stroke();
            }

            currentType = pos.type;
            ctx.beginPath();
            ctx.moveTo(cx, cy);
        } else {
            ctx.lineTo(cx, cy);
        }
    }

    if (currentType) {
        ctx.stroke();
    }

    // Draw start point
    if (positions.length > 0) {
        const first = positions[0];
        ctx.fillStyle = '#6bcf7f';
        ctx.beginPath();
        ctx.arc(canvasX(first.x), canvasY(first.y), 3, 0, Math.PI * 2);
        ctx.fill();

        // Draw end point
        const last = positions[positions.length - 1];
        ctx.fillStyle = '#ff6b6b';
        ctx.beginPath();
        ctx.arc(canvasX(last.x), canvasY(last.y), 3, 0, Math.PI * 2);
        ctx.fill();
    }
}

function drawGrid(ctx, canvasWidth, canvasHeight, minX, minY, maxX, maxY) {
    const padding = 20;
    const gridColor = '#333';
    
    ctx.strokeStyle = gridColor;
    ctx.lineWidth = 0.5;

    // Draw axes
    ctx.strokeStyle = '#555';
    ctx.beginPath();
    ctx.moveTo(padding, canvasHeight - padding);
    ctx.lineTo(canvasWidth - padding, canvasHeight - padding);
    ctx.stroke();

    ctx.beginPath();
    ctx.moveTo(padding, 0);
    ctx.lineTo(padding, canvasHeight);
    ctx.stroke();
}
