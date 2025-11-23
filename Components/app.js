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
        // Use requestAnimationFrame and directly manipulate the element scrollTop
        // to avoid any implicit scrolling of the document/window caused by
        // element.scrollIntoView.
        requestAnimationFrame(() => {
            try {
                console.log('[psScroll] scrolling to bottom', elementId);
                // Scroll to the visual end; prefer scrollTo for consistent behavior
                if (typeof el.scrollTo === 'function') {
                    el.scrollTo({ top: el.scrollHeight, behavior: 'auto' });
                } else {
                    el.scrollTop = el.scrollHeight;
                }
            } catch(e) { /* ignore */ }
        });
    }catch(e){ /* ignore */ }
};

 // Simple scroll-to-top helper for console windows
window.psScrollToTop = function(elementId){
    try{
        const el = document.getElementById(elementId);
        if(!el) return;
        requestAnimationFrame(() => {
            try {
                const style = window.getComputedStyle(el);
                const dir = style && style.flexDirection ? style.flexDirection : '';
                const isReverse = dir.indexOf('reverse') !== -1;
                console.log('[psScroll] scrolling to top', elementId, { flexDirection: dir, computedIsReverse: isReverse });
                // If container uses column-reverse, invert top/bottom semantics
                if (isReverse) {
                    // Visual top corresponds to DOM end
                    el.scrollTop = el.scrollHeight;
                } else {
                    el.scrollTop = 0;
                }
            } catch(e) { /* ignore */ }
        });
    }catch(e){ /* ignore */ }
};

 // Scroll to position with retries to handle asynchronous DOM updates
window.psScrollToPositionWithRetry = function(elementId, position, attempts = 6, delay = 50){
    try{
        console.debug('[psScroll] called', { elementId, position, attempts, delay });
        const el = document.getElementById(elementId);
        if(!el){
            console.warn('[psScroll] element not found', elementId);
            return;
        }
        const doScroll = () => {
            try {
                const info = { scrollTop: el.scrollTop, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight, children: el.children.length };
                const style = window.getComputedStyle(el);
                const dir = style && style.flexDirection ? style.flexDirection : '';
                const isReverse = dir.indexOf('reverse') !== -1;
                console.debug('[psScroll] doScroll', position, info, { flexDirection: dir, isReverse });
                // If we are using a reversed flex-direction, invert requested position.
                let resolved = position;
                if (isReverse) {
                    resolved = (position === 'top') ? 'bottom' : 'top';
                }
                if (resolved === 'top') {
                    el.scrollTop = 0;
                    console.debug('[psScroll] set scrollTop=0 (resolved top)');
                } else {
                    // Try to scroll to the DOM end and ensure real final position is visible
                    if (typeof el.scrollTo === 'function') {
                        el.scrollTo({ top: el.scrollHeight, behavior: 'auto' });
                    } else {
                        el.scrollTop = el.scrollHeight - el.clientHeight;
                    }
                    console.debug('[psScroll] set scrollTop=scrollHeight (resolved bottom)', el.scrollHeight, el.clientHeight);
                }
            } catch (e) { console.error('[psScroll] doScroll error', e); }
        };

        // Initial attempt on next frame
        requestAnimationFrame(doScroll);

        // Retry a few times spaced out to cover Blazor render timing
        let tries = 1;
        const iv = setInterval(() => {
            tries++;
            requestAnimationFrame(doScroll);
            if (tries >= attempts) {
                clearInterval(iv);
                console.debug('[psScroll] retries finished');
            }
        }, delay);
    }catch(e){ console.error('[psScroll] outer error', e); }
};

 // Force a hard scroll after a short delay (useful when Blazor reorders DOM)
window.psForceScroll = function(elementId, position, delayMs = 100){
    try{
        setTimeout(() => {
            try {
                const el = document.getElementById(elementId);
                if(!el) return;
                const style = window.getComputedStyle(el);
                const dir = style && style.flexDirection ? style.flexDirection : '';
                const isReverse = dir.indexOf('reverse') !== -1;
                // If using reverse, invert semantics
                let resolved = position;
                if (isReverse) resolved = (position === 'top') ? 'bottom' : 'top';
                if(resolved === 'top'){
                    if (typeof el.scrollTo === 'function') {
                        el.scrollTo({ top: 0, behavior: 'auto' });
                    } else {
                        el.scrollTop = 0;
                    }
                    console.debug('[psForceScroll] scrolled top', elementId);
                } else {
                    if (typeof el.scrollTo === 'function') {
                        el.scrollTo({ top: el.scrollHeight, behavior: 'auto' });
                    } else {
                        el.scrollTop = el.scrollHeight - el.clientHeight;
                    }
                    console.debug('[psForceScroll] scrolled bottom', elementId, { scrollHeight: el.scrollHeight });
                }
            } catch (e) { console.error('[psForceScroll] inner error', e); }
        }, delayMs);
    }catch(e){ console.error('[psForceScroll] outer error', e); }
};

// Scroll element reference to bottom
window.scrollToBottom = function(elementRef){
    try{
        // For Blazor element references, we need to get the actual DOM element
        if(elementRef && elementRef.id){
            const el = document.getElementById(elementRef.id);
            if(el){
                const style = window.getComputedStyle(el);
                const dir = style && style.flexDirection ? style.flexDirection : '';
                const isReverse = dir.indexOf('reverse') !== -1;
                if(isReverse){
                    el.scrollTop = 0;
                } else {
                    el.scrollTop = el.scrollHeight - el.clientHeight;
                }
            }
        }
    }catch(e){ /* ignore */ }
};

 // Scroll element reference to specific position (top or bottom)
window.scrollToPosition = function(elementRef, position){
    try{
        // For Blazor element references, we need to get the actual DOM element
        if(elementRef && elementRef.id){
            const el = document.getElementById(elementRef.id);
            if(el){
                const style = window.getComputedStyle(el);
                const dir = style && style.flexDirection ? style.flexDirection : '';
                const isReverse = dir.indexOf('reverse') !== -1;
                let resolved = position;
                if (isReverse) resolved = (position === 'top') ? 'bottom' : 'top';
                if(resolved === 'top'){
                    el.scrollTop = 0;
                } else if(resolved === 'bottom'){
                    el.scrollTop = el.scrollHeight - el.clientHeight;
                }
            }
        }
    }catch(e){ /* ignore */ }
};

 // Scroll to specific position by element id (string)
window.scrollToPositionById = function(elementId, position){
    try{
        const el = document.getElementById(elementId);
        if(!el) return;
        const style = window.getComputedStyle(el);
        const dir = style && style.flexDirection ? style.flexDirection : '';
        const isReverse = dir.indexOf('reverse') !== -1;
        let resolved = position;
        if (isReverse) resolved = (position === 'top') ? 'bottom' : 'top';
        if(resolved === 'top'){
            el.scrollTop = 0;
        } else if(resolved === 'bottom'){
            el.scrollTop = el.scrollHeight - el.clientHeight;
        }
    }catch(e){ /* ignore */ }
};

 // Return scrollHeight for an element id (used to wait until content is rendered)
window.getScrollHeightById = function(elementId){
    try{
        const el = document.getElementById(elementId);
        if(!el) return 0;
        return el.scrollHeight || 0;
    }catch(e){ return 0; }
};

// Read a computed style property on an element by id
window.getComputedStylePropertyById = function(elementId, prop) {
    try {
        const el = document.getElementById(elementId);
        if (!el) return null;
        const style = window.getComputedStyle(el);
        return style && style[prop] ? style[prop] : null;
    } catch(e) { return null; }
};

// Watch a console element with a MutationObserver to perform reliable auto-scrolling.
// Stores per-element watcher state in window._psWatchers.
window._psWatchers = window._psWatchers || {};

window.psWatchConsole = function(elementId, autoScroll = true, flipLayout = false){
    try {
        console.debug('[psWatch] register', { elementId, autoScroll, flipLayout });
        const el = document.getElementById(elementId);
        if(!el){
            console.warn('[psWatch] element not found', elementId);
            return;
        }

        // If already watching, disconnect first
        if(window._psWatchers[elementId] && window._psWatchers[elementId].observer){
            try { window._psWatchers[elementId].observer.disconnect(); } catch {}
        }

        const style = window.getComputedStyle(el);
        const flexDirection = style && style.flexDirection ? style.flexDirection : '';
        const state = {
            autoScroll: !!autoScroll,
            flipLayout: !!flipLayout,
            observer: null,
            flexDirection: flexDirection
        };
            // Do not react to the immediate mutation that happens during DOM flips; wait for stable state
            state.ignoreNextMutation = false;
        console.debug('[psWatch] computed flexDirection', { elementId, flexDirection });

        const callback = function(mutationsList){
            try {
                        if(!state.autoScroll) return;
                        if(state.ignoreNextMutation){
                            state.ignoreNextMutation = false;
                            console.debug('[psWatch] ignored initial mutation after watch update', { elementId });
                            return;
                        }
                // When content changes, scroll to the appropriate end.
                const pos = state.flipLayout ? 'top' : 'bottom';
                console.debug('[psWatch] mutation', { elementId, pos, flipLayout: state.flipLayout, flexDirection: state.flexDirection });
                // Use the robust retry scroller
                window.psScrollToPositionWithRetry(elementId, pos, 6, 40);
            } catch (e) { console.error('[psWatch] mutation handler error', e); }
        };

        const observer = new MutationObserver(callback);
        observer.observe(el, { childList: true, subtree: false, characterData: false });

        state.observer = observer;
        window._psWatchers[elementId] = state;

        // Do an initial sync scroll
        const initialPos = state.flipLayout ? 'top' : 'bottom';
        window.psScrollToPositionWithRetry(elementId, initialPos, 6, 40);

        console.debug('[psWatch] watching', { elementId, children: el.children.length, flexDirection: state.flexDirection, flipLayout: state.flipLayout });
    } catch (e) {
        console.error('[psWatch] error', e);
    }
};

window.psUpdateWatch = function(elementId, autoScroll = true, flipLayout = false){
    try {
        console.debug('[psWatch] update', { elementId, autoScroll, flipLayout });
        const w = window._psWatchers[elementId];
        if(!w) {
            // Not registered yet — try to register
            return window.psWatchConsole(elementId, autoScroll, flipLayout);
        }
        const previousFlip = w.flipLayout;
        w.autoScroll = !!autoScroll;
        w.flipLayout = !!flipLayout;
        // Mark the watcher to ignore the next mutation to avoid race conditions during DOM flip
        if (previousFlip !== w.flipLayout) {
            w.ignoreNextMutation = true;
            console.debug('[psWatch] set ignoreNextMutation due to flip change', { elementId, previousFlip, newFlip: w.flipLayout });
        }
        try {
            const el = document.getElementById(elementId);
            if (el) {
                const style = window.getComputedStyle(el);
                w.flexDirection = style && style.flexDirection ? style.flexDirection : '';
                console.debug('[psWatch] update computed flexDirection', { elementId, flexDirection: w.flexDirection, flipLayout: w.flipLayout });
            }
        } catch (e) { /* ignore */ }
        // If autoScroll enabled, perform an immediate scroll to sync state
        if(w.autoScroll){
            const pos = w.flipLayout ? 'top' : 'bottom';
            window.psScrollToPositionWithRetry(elementId, pos, 6, 30);
        }
    } catch (e) {
        console.error('[psWatch] update error', e);
    }
};

// Unregister a console watcher (disconnect observer) to avoid race conditions while flipping
window.psUnwatchConsole = function(elementId){
    try {
        console.debug('[psWatch] unwatch', elementId);
        const w = window._psWatchers[elementId];
        if(!w) return;
        try { if (w.observer) w.observer.disconnect(); } catch(e) { /* ignore */ }
        delete window._psWatchers[elementId];
    } catch (e) {
        console.error('[psWatch] unwatch error', e);
    }
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
