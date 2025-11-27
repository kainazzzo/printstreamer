using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using System.Linq;
using System.Security.Cryptography;
using System.Collections.Generic;

namespace PrintStreamer.Services
{
    public enum RepeatMode { None, One, All }

    public sealed class AudioOptions
    {
        public string Folder { get; set; } = "audio";
    }

    public sealed class AudioTrack
    {
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
    }

    public sealed class AudioState
    {
        public bool IsPlaying { get; init; }
        public string? Current { get; init; }
        public IReadOnlyList<string> Queue { get; init; } = Array.Empty<string>();
        public bool Shuffle { get; init; }
        public RepeatMode Repeat { get; init; }
    }

    /// <summary>
    /// Simple filesystem-backed track helper.
    /// Responsibilities:
    /// - Maintain a library (files on disk)
    /// - Provide enqueue/clear/skip behavior working with filenames
    /// - Expose Current/CurrentPath (playback systems manage position/cancellation)
    /// This service intentionally does not track playback position or history.
    /// </summary>
    public sealed class AudioService
    {
        private readonly IConfiguration _config;
        private readonly HashSet<string> _supported = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3",".aac",".m4a",".wav",".flac",".ogg",".opus"
        };

        private string _folder;
        private List<AudioTrack> _library = new();
private readonly ConcurrentQueue<string> _queue = new();
private readonly object _queueLock = new();
private string? _current;
private string _lastPlayedFile = string.Empty;
    private bool _playing;
    private bool _shuffle;
    private RepeatMode _repeat = RepeatMode.All; // default to repeat all
    // index into the library for rotation
    private int _libraryIndex = -1;

public AudioService(IConfiguration config)
{
    _config = config;
    _folder = _config.GetValue<string>("Audio:Folder") ?? "audio";
    _folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory.GetCurrentDirectory(), _folder));
    EnsureFolder();

    // initialize persistence path for last-played state
    _lastPlayedFile = System.IO.Path.Combine(_folder, "last_played.txt");

    Rescan();

    // Try to restore last-played track; if not available pick a random one
    RestoreLastPlayed();
}

        private void EnsureFolder()
        {
            try { Directory.CreateDirectory(_folder); } catch { }
        }

        public string Folder => _folder;

        public IReadOnlyList<AudioTrack> Library => _library.ToList();

        public void SetFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(folder) ? folder : System.IO.Path.Combine(Directory.GetCurrentDirectory(), folder));
            _folder = full;
            try { Directory.CreateDirectory(_folder); } catch { }
            _config["Audio:Folder"] = folder;
            Rescan();
        }

        public void Rescan()
        {
            var list = new List<AudioTrack>();
            try
            {
                if (Directory.Exists(_folder))
                {
                    foreach (var file in Directory.EnumerateFiles(_folder, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        var ext = System.IO.Path.GetExtension(file);
                        if (!_supported.Contains(ext)) continue;
                        list.Add(new AudioTrack { Name = System.IO.Path.GetFileNameWithoutExtension(file), Path = file });
                    }
                }
            }
            catch { }

            var sorted = list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _library = sorted;

            // Keep the index aligned with the currently playing track so manual skips advance correctly.
            var currentPath = _current;
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                var idx = sorted.FindIndex(t => string.Equals(t.Path, currentPath, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    System.Threading.Interlocked.Exchange(ref _libraryIndex, idx);
                    return;
                }
            }

            // If there's no currently selected track, initialize the rotating index to a random position
            // (store r-1 so the next Interlocked.Increment yields r). This prevents always starting at the first track after restart.
            if (sorted.Count > 0)
            {
                var r = System.Security.Cryptography.RandomNumberGenerator.GetInt32(sorted.Count);
                System.Threading.Interlocked.Exchange(ref _libraryIndex, r - 1);
            }
            else
            {
                System.Threading.Interlocked.Exchange(ref _libraryIndex, -1);
            }
        }

private void SaveLastPlayed()
{
    try
    {
        if (string.IsNullOrWhiteSpace(_lastPlayedFile)) return;
        if (string.IsNullOrWhiteSpace(_current))
        {
            if (System.IO.File.Exists(_lastPlayedFile)) System.IO.File.Delete(_lastPlayedFile);
            return;
        }

        // Write absolute path of current track
        System.IO.File.WriteAllText(_lastPlayedFile, _current);
    }
    catch { }
}

private void RestoreLastPlayed()
{
    try
    {
        if (string.IsNullOrWhiteSpace(_lastPlayedFile)) return;

        if (System.IO.File.Exists(_lastPlayedFile))
        {
            var saved = System.IO.File.ReadAllText(_lastPlayedFile).Trim();
            if (!string.IsNullOrWhiteSpace(saved))
            {
                // If the saved path is present in the library, restore it
                var idx = _library.FindIndex(t => string.Equals(t.Path, saved, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    _current = _library[idx].Path;
                    _playing = false;
                    System.Threading.Interlocked.Exchange(ref _libraryIndex, idx);
                    return;
                }
            }
        }

        // saved track not found -> pick a random track if library available and persist it
        if (_library.Count > 0)
        {
            var idx = System.Security.Cryptography.RandomNumberGenerator.GetInt32(_library.Count);
            _current = _library[idx].Path;
            _playing = false;
            System.Threading.Interlocked.Exchange(ref _libraryIndex, idx);
            SaveLastPlayed();
        }
    }
    catch { }
}

public AudioState GetState()
{
    return new AudioState
    {
        IsPlaying = _playing,
        Current = _current is string p ? System.IO.Path.GetFileName(p) : null,
        Queue = _queue.ToArray().Select(p => System.IO.Path.GetFileName(p)).ToList(),
        Shuffle = _shuffle,
        Repeat = _repeat
    };
}

public void Enqueue(params string[] names)
{
    if (names == null || names.Length == 0) return;
    var map = _library.ToDictionary(t => t.Name, t => t.Path, StringComparer.OrdinalIgnoreCase);
    lock (_queueLock)
    {
        foreach (var n in names)
        {
            if (map.TryGetValue(n, out var path)) _queue.Enqueue(path);
        }
    }
}

public void ClearQueue()
{
    lock (_queueLock)
    {
        while (_queue.TryDequeue(out _)) { }
    }
}

        public void Play() { _playing = true; }
        public void Pause() { _playing = false; }
        public void Toggle() { _playing = !_playing; }

        public bool IsPlaying => _playing;

        public void Next()
        {
            // Skip to next track (no position/cancellation handling here)
            TryGetNextTrack(out _);
        }

        public void Prev()
        {
            // No previous-track support; treat Prev as a skip as well.
            TryGetNextTrack(out _);
        }

        public void SetShuffle(bool enabled) { _shuffle = enabled; }
        public void SetRepeat(RepeatMode mode) { _repeat = mode; }

        /// <summary>
        /// Select a specific track by display name and start playing it immediately.
        /// Returns true if the name was found in the library.
        /// </summary>
        public bool TrySelectByName(string name, out string? selectedPath)
        {
            selectedPath = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            var match = _library.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match == null || string.IsNullOrWhiteSpace(match.Path)) return false;
            _current = match.Path;
            _playing = true;
            SaveLastPlayed();
            selectedPath = match.Path;

            // Align the rotating library index with the selected track so the next skip works as expected.
            var library = _library;
            var idx = library.FindIndex(t => string.Equals(t.Path, match.Path, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                System.Threading.Interlocked.Exchange(ref _libraryIndex, idx);
            }
            return true;
        }

        /// <summary>
        /// Select a random track from the library and make it the current track.
        /// This does not enable shuffle mode; it simply picks one random track and
        /// aligns the library index so subsequent skips behave as expected.
        /// </summary>
        public bool TrySelectRandomTrack(out string? selectedPath)
        {
            selectedPath = null;
            var library = _library;
            if (library.Count == 0) return false;
            var idx = RandomNumberGenerator.GetInt32(library.Count);
            var path = library[idx].Path;
            _current = path;
            _playing = true;
            SaveLastPlayed();
            System.Threading.Interlocked.Exchange(ref _libraryIndex, idx);
            selectedPath = path;
            return true;
        }

        /// <summary>
        /// Resolve a display name to an absolute file path, or null if not found.
        /// </summary>
        public string? GetPathForName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return _library.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Path;
        }

        public bool TryGetNextTrack(out string path)
        {
            path = string.Empty;

            // Shuffle mode: pick a random library item
            if (_shuffle)
            {
                var lib = _library;
                if (lib.Count == 0) return false;
                var idx = System.Security.Cryptography.RandomNumberGenerator.GetInt32(lib.Count);
                path = lib[idx].Path;
                _current = path;
                _playing = true;
                SaveLastPlayed();
                return true;
            }

            // Immediate queue first
            if (_queue.TryDequeue(out var qpath))
            {
                path = qpath;
                _current = path;
                _playing = true;
                SaveLastPlayed();
                return true;
            }

            // Fallback: choose next item from library according to repeat mode
            var library = _library;
            if (library.Count == 0) return false;

            if (_repeat == RepeatMode.One && _current is string cur)
            {
                path = cur;
                _playing = true;
                return true;
            }

            // Advance library index atomically
            var nextIdx = System.Threading.Interlocked.Increment(ref _libraryIndex);
            if (nextIdx >= library.Count)
            {
                if (_repeat == RepeatMode.All)
                {
                    // wrap to 0
                    System.Threading.Interlocked.Exchange(ref _libraryIndex, 0);
                    nextIdx = 0;
                }
                else
                {
                    // no repeat and we've reached the end
                    return false;
                }
            }

            path = library[nextIdx % library.Count].Path;
            _current = path;
            _playing = true;
            SaveLastPlayed();
            return true;
        }

        /// <summary>
        /// Try to consume the next item from the explicit queue (if any) and make it the current track.
        /// Returns true when a queued path was found and selected.
        /// </summary>
public bool TryConsumeQueue(out string path)
{
    path = string.Empty;
    if (_queue.TryDequeue(out var qpath))
    {
        path = qpath;
        _current = path;
        _playing = true;
        SaveLastPlayed();
        return true;
    }
    return false;
}

        /// <summary>
        /// Remove the queued entry at the given zero-based index. Returns true if removed.
        /// </summary>
public bool RemoveFromQueueAt(int index)
{
    if (index < 0) return false;
    var tmp = new List<string>();
    var removed = false;
    var i = 0;
    lock (_queueLock)
    {
        while (_queue.TryDequeue(out var p))
        {
            if (!removed && i == index)
            {
                removed = true;
            }
            else
            {
                tmp.Add(p);
            }
            i++;
        }
        foreach (var item in tmp) _queue.Enqueue(item);
    }
    return removed;
}

        /// <summary>
        /// Remove any queued entries that match the provided track names (case-insensitive, compares filename without extension).
        /// Returns the number of removed items.
        /// </summary>
public int RemoveFromQueue(params string[] names)
{
    if (names == null || names.Length == 0) return 0;
    var lookup = new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
    if (lookup.Count == 0) return 0;

    // Rebuild the queue excluding matching items. Use a small lock to avoid concurrent rebuild collisions.
    var removed = 0;
    var temp = new List<string>();
    lock (_queueLock)
    {
        while (_queue.TryDequeue(out var p))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(p) ?? string.Empty;
            if (lookup.Contains(name))
            {
                removed++;
                continue;
            }
            temp.Add(p);
        }

        // Re-enqueue preserved items in original order
        foreach (var item in temp)
        {
            _queue.Enqueue(item);
        }
    }

    return removed;
}

        public string? Current => _current is string p ? System.IO.Path.GetFileName(p) : null;
        public string? CurrentPath => _current;
    }
}
