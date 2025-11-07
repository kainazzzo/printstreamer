// PrintStreamer JavaScript Interop
// Provides MJPEG stream monitoring and toast notifications

let isStreamPlaying = false; // legacy; real status comes from window._printstreamer_mjpeg_ready

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

// Initialize MJPEG stream
window.initializeStreams = function () {
    console.log('[Streams] Initializing MJPEG');

    const mjpegImg = document.getElementById('mjpeg');
    // Track MJPEG readiness for UI health checks
    window._printstreamer_mjpeg_ready = false;

    if (mjpegImg) {
        mjpegImg.addEventListener('load', () => {
            window._printstreamer_mjpeg_ready = true;
        });

        mjpegImg.addEventListener('error', () => {
            console.log('[Streams] MJPEG error, retrying in 2s');
            window._printstreamer_mjpeg_ready = false;
            setTimeout(() => { mjpegImg.src = '/stream?ts=' + Date.now(); }, 2000);
        });
    }
};

// Reload streams (after camera toggle)
window.reloadStreams = function () {
    const mjpegImg = document.getElementById('mjpeg');
    if (mjpegImg) {
        mjpegImg.src = '/stream?ts=' + Date.now();
    }
};

// Expose functions for Blazor
window.getStreamPlayingStatus = function() {
    try { return !!window._printstreamer_mjpeg_ready; } catch (e) { return false; }
};

// Simple scroll-to-bottom helper for console windows
window.psScrollToBottom = function(elementId){
    try{
        const el = document.getElementById(elementId);
        if(!el) return;
        el.scrollTop = el.scrollHeight;
    }catch(e){ /* ignore */ }
};

// Scroll element reference to bottom
window.scrollToBottom = function(elementRef){
    try{
        // For Blazor element references, we need to get the actual DOM element
        if(elementRef && elementRef.id){
            const el = document.getElementById(elementRef.id);
            if(el){
                el.scrollTop = el.scrollHeight;
            }
        }
    }catch(e){ /* ignore */ }
};

// Player control helpers exposed for Blazor UI
window.streamControls = {
    play: function() {},
    pause: function() {},
    toggleMute: function() { return true; },
    setVolume: function(vol) {},
    isMuted: function() { return true; },
    isPlaying: function() { try { return !!window._printstreamer_mjpeg_ready; } catch { return false; } }
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

// Local file preview player (per-track)
window.trackPreview = {
    play: function(name) {
        try {
            const a = document.getElementById('trackPreview');
            if (!a) return;
            const url = '/api/audio/preview?name=' + encodeURIComponent(name) + '&ts=' + Date.now();
            a.src = url;
            a.play().catch(() => {});
        } catch {}
    }
};
