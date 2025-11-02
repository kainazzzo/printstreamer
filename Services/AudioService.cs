using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using System.Linq;
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
        private string? _current;
    private bool _playing;
    private bool _shuffle;
    private RepeatMode _repeat = RepeatMode.All; // default to repeat all
    // index into the library for rotation
    private int _libraryIndex = -1;

        public AudioService(IConfiguration config)
        {
            _config = config;
            _folder = _config.GetValue<string>("Audio:Folder") ?? _config.GetValue<string>("audio:folder") ?? "audio";
            _folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory.GetCurrentDirectory(), _folder));
            EnsureFolder();
            Rescan();
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

            _library = list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            // reset library index to before-first; first TryGetNextTrack will advance to 0
            _libraryIndex = -1;
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
            foreach (var n in names)
            {
                if (map.TryGetValue(n, out var path)) _queue.Enqueue(path);
            }
        }

        public void ClearQueue()
        {
            while (_queue.TryDequeue(out _)) { }
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
                return true;
            }

            // Immediate queue first
            if (_queue.TryDequeue(out var qpath))
            {
                path = qpath;
                _current = path;
                _playing = true;
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
            return true;
        }

        public string? Current => _current is string p ? System.IO.Path.GetFileName(p) : null;
        public string? CurrentPath => _current;
    }
}
