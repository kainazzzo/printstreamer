// PrintStreamer JavaScript Interop
// Provides HLS player, MJPEG stream monitoring, and toast notifications

let hlsInstance = null;
let retryTimeout = null;
let isStreamPlaying = false;

// Toast notification system
window.showToast = function (message, type = 'info') {
    const container = document.getElementById('toast-container');
    if (!container) {
        console.warn('Toast container not found');
        return;
    }
    
    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    
    const icon = type === 'success' ? '✓' : type === 'error' ? '✗' : 'ℹ';
    
    // Convert URLs in message to clickable links
    const messageWithLinks = message.replace(/(https?:\/\/[^\s]+)/g, '<a href="$1" target="_blank" style="color:#66ccff;text-decoration:underline">$1</a>');
    
    toast.innerHTML = `<span style='font-size:1.2em'>${icon}</span><span>${messageWithLinks}</span>`;
    
    container.appendChild(toast);
    
    // Auto-remove after duration
    const duration = message.includes('http') ? 8000 : 4000;
    setTimeout(() => {
        toast.style.animation = 'slideOut 0.3s ease-in forwards';
        setTimeout(() => toast.remove(), 300);
    }, duration);
};

// Initialize MJPEG stream and HLS player
window.initializeStreams = function () {
    console.log('[Streams] Initializing...');
    
    const mjpegImg = document.getElementById('mjpeg');
    const hlsContainer = document.getElementById('hlsContainer');
    
    if (mjpegImg) {
        let imgReady = false;
        
        // Sync HLS container height with MJPEG image
        function syncHeights() {
            if (mjpegImg.complete && mjpegImg.naturalHeight > 0 && hlsContainer) {
                hlsContainer.style.minHeight = mjpegImg.offsetHeight + 'px';
            }
        }
        
        mjpegImg.addEventListener('load', () => {
            syncHeights();
            if (!imgReady) {
                imgReady = true;
                try { attachHls(); } catch (e) { console.error('[Streams] HLS init error:', e); }
            }
        });
        
        mjpegImg.addEventListener('error', () => {
            console.log('[Streams] MJPEG error, retrying in 2s');
            setTimeout(() => { mjpegImg.src = '/stream?ts=' + Date.now(); }, 2000);
        });
        
        window.addEventListener('resize', syncHeights);
        setTimeout(syncHeights, 100);
    }
    
    // Start HLS attachment
    attachHls();
};

// Reload streams (after camera toggle)
window.reloadStreams = function () {
    const mjpegImg = document.getElementById('mjpeg');
    if (mjpegImg) {
        mjpegImg.src = '/stream?ts=' + Date.now();
    }
    setTimeout(() => { try { attachHls(); } catch (e) {} }, 800);
};

// HLS player management
async function attachHls() {
    cleanup();
    
    const video = document.getElementById('hlsPlayer');
    const hlsUrl = '/hls/stream.m3u8';
    
    if (!video) {
        console.warn('[HLS] Video element not found');
        return;
    }
    
    hlsStatus('checking for stream...');
    
    // Check if manifest exists
    const manifestCheck = await checkManifest(hlsUrl);
    if (!manifestCheck.ok) {
        if (manifestCheck.status === 404) {
            hlsStatus('waiting for stream (404)');
        } else if (manifestCheck.error) {
            hlsStatus('waiting for stream (error)');
        } else {
            hlsStatus('waiting for stream (' + manifestCheck.status + ')');
        }
        retryTimeout = setTimeout(() => attachHls(), 3000);
        return;
    }
    
    hlsStatus('stream found, attaching...');
    
    try {
        // Load hls.js if not already loaded
        if (!window.Hls) {
            await loadHls();
        }
        
        if (window.Hls && window.Hls.isSupported()) {
            // Use hls.js
            hlsInstance = new window.Hls({
                liveSyncDuration: 2,
                maxBufferLength: 10,
                enableWorker: true,
                lowLatencyMode: true
            });
            
            hlsInstance.on(window.Hls.Events.MANIFEST_PARSED, function() {
                hlsStatus('playing');
                isStreamPlaying = true;
                video.muted = true;
                video.play().catch((e) => {
                    console.warn('[HLS] Play failed:', e);
                    hlsStatus('play failed (interaction needed)');
                });
            });
            
            hlsInstance.on(window.Hls.Events.ERROR, function(event, data) {
                console.warn('[HLS] Error:', data);
                
                if (data.fatal) {
                    hlsStatus('error: ' + (data.details || 'unknown'));
                    isStreamPlaying = false;
                    
                    switch(data.type) {
                        case window.Hls.ErrorTypes.NETWORK_ERROR:
                            hlsStatus('network error, retrying...');
                            retryTimeout = setTimeout(() => attachHls(), 3000);
                            break;
                        case window.Hls.ErrorTypes.MEDIA_ERROR:
                            hlsStatus('media error, attempting recovery...');
                            try {
                                hlsInstance.recoverMediaError();
                            } catch (e) {
                                retryTimeout = setTimeout(() => attachHls(), 3000);
                            }
                            break;
                        default:
                            hlsStatus('fatal error, retrying...');
                            retryTimeout = setTimeout(() => attachHls(), 3000);
                            break;
                    }
                }
            });
            
            hlsInstance.loadSource(hlsUrl);
            hlsInstance.attachMedia(video);
            
        } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
            // Use native HLS (Safari)
            video.src = hlsUrl;
            video.addEventListener('loadedmetadata', function() {
                hlsStatus('playing (native)');
                isStreamPlaying = true;
                video.muted = true;
                video.play().catch(() => {});
            });
            video.addEventListener('error', function() {
                hlsStatus('error, retrying...');
                isStreamPlaying = false;
                retryTimeout = setTimeout(() => attachHls(), 3000);
            });
        } else {
            hlsStatus('HLS not supported');
            isStreamPlaying = false;
        }
        
    } catch (err) {
        console.warn('[HLS] Attach failed:', err);
        hlsStatus('attach failed, retrying...');
        retryTimeout = setTimeout(() => attachHls(), 3000);
    }
}

function cleanup() {
    if (hlsInstance) {
        try {
            hlsInstance.destroy();
        } catch (e) {
            console.warn('[HLS] Error destroying instance:', e);
        }
        hlsInstance = null;
    }
    if (retryTimeout) {
        clearTimeout(retryTimeout);
        retryTimeout = null;
    }
    isStreamPlaying = false;
}

async function checkManifest(hlsUrl) {
    try {
        const resp = await fetch(hlsUrl, { cache: 'no-store' });
        if (resp.ok) {
            const ct = resp.headers.get('content-type') || '';
            if (ct.includes('mpegurl') || ct.includes('vnd.apple.mpegurl') || ct.includes('application/x-mpegurl')) {
                return { ok: true, contentType: ct };
            }
            console.warn('[HLS] Unexpected manifest content-type:', ct);
            return { ok: true, contentType: ct };
        }
        return { ok: false, status: resp.status };
    } catch (err) {
        return { ok: false, error: err.message };
    }
}

function loadHls() {
    return new Promise((resolve, reject) => {
        if (window.Hls) return resolve(window.Hls);
        const s = document.createElement('script');
        s.src = 'https://cdn.jsdelivr.net/npm/hls.js@1.5.0/dist/hls.min.js';
        s.onload = () => resolve(window.Hls);
        s.onerror = () => reject(new Error('Failed to load hls.js'));
        document.head.appendChild(s);
    });
}

function hlsStatus(text) {
    try {
        const statusEl = document.getElementById('hlsStatus');
        if (statusEl) {
            statusEl.textContent = 'HLS status: ' + text;
        }
    } catch (e) {}
    console.log('[HLS] ' + text);
}

// Monitor playback health
setInterval(() => {
    try {
        const video = document.getElementById('hlsPlayer');
        if (!video) return;
        
        if (video.readyState < 2) {
            // Not enough data
            if (hlsInstance && typeof hlsInstance.stopLoad === 'function') {
                hlsInstance.stopLoad();
                hlsInstance.startLoad();
            }
        } else if (!video.paused && video.readyState >= 3) {
            hlsStatus('playing');
        }
    } catch (e) {
        console.warn('[HLS] Health check error:', e);
    }
}, 5000);

// Expose functions for Blazor
window.attachHls = attachHls;
window.detachHls = cleanup;
window.getStreamPlayingStatus = function() {
    return isStreamPlaying;
};

// Register a YouTube iframe end handler that seeks to the last frame and pauses
window.printstreamer = window.printstreamer || {};
window.printstreamer._players = window.printstreamer._players || {};
window.printstreamer._ytReady = window.printstreamer._ytReady || false;

window.printstreamer.registerYoutubeEndHandler = function (iframeId) {
    return new Promise((resolve, reject) => {
        try {
            const setupPlayer = () => {
                try {
                    if (window.printstreamer._players[iframeId]) return resolve();
                    // eslint-disable-next-line no-undef
                    const player = new YT.Player(iframeId, {
                        events: {
                            'onStateChange': function (e) {
                                try {
                                    // eslint-disable-next-line no-undef
                                    if (e.data === YT.PlayerState.ENDED) {
                                        const p = window.printstreamer._players[iframeId];
                                        if (!p) return;
                                        try {
                                            const d = p.getDuration();
                                            if (d && d > 0) {
                                                // Seek to just before the end and pause so the last frame remains visible
                                                p.seekTo(Math.max(0, d - 0.05), true);
                                                p.pauseVideo();
                                            } else {
                                                p.pauseVideo();
                                            }
                                        } catch (e) { /* ignore */ }
                                    }
                                } catch (ee) { /* ignore */ }
                            }
                        }
                    });
                    window.printstreamer._players[iframeId] = player;
                    return resolve();
                } catch (err) {
                    return reject(err);
                }
            };

            if (window.YT && window.YT.Player) {
                setupPlayer();
            } else {
                // load api if not already
                if (!window.printstreamer._ytLoading) {
                    window.printstreamer._ytLoading = true;
                    const tag = document.createElement('script');
                    tag.src = 'https://www.youtube.com/iframe_api';
                    const firstScriptTag = document.getElementsByTagName('script')[0];
                    firstScriptTag.parentNode.insertBefore(tag, firstScriptTag);

                    window.onYouTubeIframeAPIReady = function () {
                        window.printstreamer._ytReady = true;
                    };
                }

                // Wait until API is ready
                const waitForApi = () => {
                    if (window.YT && window.YT.Player) return setupPlayer();
                    setTimeout(waitForApi, 150);
                };
                waitForApi();
            }
        } catch (ex) {
            reject(ex);
        }
    });
};

// Simple audio preview controller for the audio stream endpoint
window.audioPreview = {
    play: function() {
        try {
            const a = document.getElementById('audioPreview');
            if (!a) return;
            a.play().catch(() => {});
        } catch {}
    },
    pause: function() {
        try {
            const a = document.getElementById('audioPreview');
            if (!a) return;
            a.pause();
        } catch {}
    },
    reload: function() {
        try {
            const a = document.getElementById('audioPreview');
            if (!a) return;
            a.src = '/api/audio/stream?ts=' + Date.now();
            a.play().catch(() => {});
        } catch {}
    }
};
