using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace PrintStreamer.Services
{
    /// <summary>
    /// Centralized manager for YouTube API polling that implements rate limiting,
    /// caching, backoff, and idle detection to reduce quota consumption.
    /// </summary>
    public class YouTubePollingManager
    {
        private readonly ILogger<YouTubePollingManager> _logger;
        private readonly YouTubePollingOptions _options;
        private readonly ConcurrentDictionary<string, CachedResponse> _cache = new();
        private readonly SemaphoreSlim _rateLimitSemaphore;
        private readonly Queue<DateTime> _requestTimestamps = new();
        private readonly object _rateLimitLock = new();
        private DateTime _lastActivity = DateTime.UtcNow;
        private int _totalRequests = 0;
        private int _cacheHits = 0;
        private int _rateLimitWaits = 0;

        public YouTubePollingManager(IOptions<YouTubePollingOptions> options, ILogger<YouTubePollingManager> logger)
        {
            _options = options.Value;
            _logger = logger;
            _rateLimitSemaphore = new SemaphoreSlim(_options.RequestsPerMinute, _options.RequestsPerMinute);
        }

        /// <summary>
        /// Execute a YouTube API call with rate limiting and caching
        /// </summary>
        public async Task<T> ExecuteWithRateLimitAsync<T>(
            Func<Task<T>> apiCall,
            string cacheKey,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                // Bypass manager when disabled
                return await apiCall();
            }

            // Check cache first
            if (_cache.TryGetValue(cacheKey, out var cached) && 
                DateTime.UtcNow - cached.Timestamp < TimeSpan.FromSeconds(_options.CacheDurationSeconds))
            {
                Interlocked.Increment(ref _cacheHits);
                _logger.LogDebug("Cache hit for {CacheKey}", cacheKey);
                return (T)cached.Response;
            }

            // Apply rate limiting
            await WaitForRateLimitAsync(cancellationToken);

            // Execute API call
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await apiCall();
                sw.Stop();

                Interlocked.Increment(ref _totalRequests);
                _lastActivity = DateTime.UtcNow;

                // Cache the result
                _cache[cacheKey] = new CachedResponse
                {
                    Response = result!,
                    Timestamp = DateTime.UtcNow
                };

                _logger.LogDebug("YouTube API call {CacheKey} completed in {ElapsedMs}ms (total: {Total}, cache hits: {Hits})",
                    cacheKey, sw.ElapsedMilliseconds, _totalRequests, _cacheHits);

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogWarning(ex, "YouTube API call {CacheKey} failed after {ElapsedMs}ms", cacheKey, sw.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>
        /// Poll a YouTube API endpoint until a condition is met or timeout
        /// </summary>
        public async Task<T?> PollUntilConditionAsync<T>(
            Func<Task<T>> fetchFunc,
            Func<T, bool> condition,
            TimeSpan timeout,
            string context,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            var attempt = 0;

            _logger.LogInformation("Starting poll: context={Context}, timeout={TimeoutSec}s", context, timeout.TotalSeconds);

            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    var result = await fetchFunc();
                    
                    if (condition(result))
                    {
                        _logger.LogInformation("Poll succeeded: context={Context}, attempts={Attempts}", context, attempt);
                        return result;
                    }

                    // Calculate next interval with backoff and jitter
                    var interval = CalculateInterval(attempt);
                    var remaining = deadline - DateTime.UtcNow;
                    
                    if (remaining < interval)
                    {
                        // Not enough time for another poll
                        _logger.LogWarning("Poll timeout: context={Context}, attempts={Attempts}", context, attempt);
                        return result;
                    }

                    _logger.LogDebug("Poll continuing: context={Context}, attempt={Attempt}, nextInterval={IntervalSec}s",
                        context, attempt, interval.TotalSeconds);

                    await Task.Delay(interval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Poll cancelled: context={Context}, attempts={Attempts}", context, attempt);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Poll error: context={Context}, attempt={Attempt}", context, attempt);
                    
                    // Apply exponential backoff on errors
                    var backoffInterval = CalculateInterval(attempt);
                    await Task.Delay(backoffInterval, cancellationToken);
                }
            }

            _logger.LogWarning("Poll exhausted: context={Context}, attempts={Attempts}", context, attempt);
            return default;
        }

        /// <summary>
        /// Calculate polling interval based on attempt count, idle state, and jitter
        /// </summary>
        public TimeSpan CalculateInterval(int attempt = 1)
        {
            var idleTime = DateTime.UtcNow - _lastActivity;
            var isIdle = idleTime > TimeSpan.FromMinutes(_options.IdleThresholdMinutes);

            // Start with base interval
            int intervalSeconds;
            if (isIdle)
            {
                // Use max interval during idle
                intervalSeconds = _options.MaxIntervalSeconds;
            }
            else if (attempt == 1)
            {
                // First attempt uses base interval
                intervalSeconds = _options.BaseIntervalSeconds;
            }
            else
            {
                // Apply exponential backoff for retries
                var backoffSeconds = _options.BaseIntervalSeconds * Math.Pow(_options.BackoffMultiplier, attempt - 1);
                intervalSeconds = (int)Math.Min(backoffSeconds, _options.MaxIntervalSeconds);
            }

            // Ensure minimum interval
            intervalSeconds = Math.Max(intervalSeconds, _options.MinIntervalSeconds);

            // Add random jitter to prevent thundering herd
            var jitter = Random.Shared.Next(0, _options.MaxJitterSeconds);
            var totalSeconds = intervalSeconds + jitter;

            return TimeSpan.FromSeconds(totalSeconds);
        }

        /// <summary>
        /// Get polling statistics
        /// </summary>
        public PollingStats GetStats()
        {
            var idleTime = DateTime.UtcNow - _lastActivity;
            return new PollingStats
            {
                TotalRequests = _totalRequests,
                CacheHits = _cacheHits,
                RateLimitWaits = _rateLimitWaits,
                IdleTimeMinutes = idleTime.TotalMinutes,
                IsIdle = idleTime > TimeSpan.FromMinutes(_options.IdleThresholdMinutes),
                CachedItemCount = _cache.Count
            };
        }

        /// <summary>
        /// Clear cache (useful for testing)
        /// </summary>
        public void ClearCache()
        {
            _cache.Clear();
            _logger.LogInformation("Cache cleared");
        }

        private async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
        {
            TimeSpan? waitTime = null;
            int requestCount = 0;

            // Check rate limit and calculate wait time
            lock (_rateLimitLock)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-1);
                while (_requestTimestamps.Count > 0 && _requestTimestamps.Peek() < cutoff)
                {
                    _requestTimestamps.Dequeue();
                }

                requestCount = _requestTimestamps.Count;

                // Check if we're at rate limit
                if (_requestTimestamps.Count >= _options.RequestsPerMinute)
                {
                    // Calculate how long to wait
                    var oldestRequest = _requestTimestamps.Peek();
                    waitTime = oldestRequest.AddMinutes(1) - DateTime.UtcNow;
                }
            }

            // Wait outside the lock
            if (waitTime.HasValue && waitTime.Value > TimeSpan.Zero)
            {
                Interlocked.Increment(ref _rateLimitWaits);
                _logger.LogWarning("Rate limit reached, waiting {WaitMs}ms (requests in last minute: {Count})",
                    waitTime.Value.TotalMilliseconds, requestCount);
                
                await Task.Delay(waitTime.Value, cancellationToken);
            }

            // Record this request
            lock (_rateLimitLock)
            {
                _requestTimestamps.Enqueue(DateTime.UtcNow);
            }
        }

        private class CachedResponse
        {
            public object Response { get; set; } = null!;
            public DateTime Timestamp { get; set; }
        }
    }

    public class PollingStats
    {
        public int TotalRequests { get; set; }
        public int CacheHits { get; set; }
        public int RateLimitWaits { get; set; }
        public double IdleTimeMinutes { get; set; }
        public bool IsIdle { get; set; }
        public int CachedItemCount { get; set; }
    }
}
