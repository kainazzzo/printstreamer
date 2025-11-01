using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

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

	public sealed class AudioService
	{
		private readonly object _lock = new object();
		private readonly IConfiguration _config;
		private readonly HashSet<string> _supported = new(StringComparer.OrdinalIgnoreCase)
		{
			".mp3",".aac",".m4a",".wav",".flac",".ogg",".opus"
		};

		private string _folder;
		private List<AudioTrack> _library = new();
		// Immediate queue requested by UI/actions (FIFO)
		private readonly Queue<string> _queue = new();
		// Background rotation over library when immediate queue is empty
		private readonly LinkedList<string> _rotation = new();
		private LinkedListNode<string>? _rotCursor;
		private string? _current;
		private CancellationTokenSource? _trackCts;
		private bool _playing;
		private bool _shuffle;
		private RepeatMode _repeat = RepeatMode.None;

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

		public string Folder
		{
			get { lock(_lock) return _folder; }
		}

		public IReadOnlyList<AudioTrack> Library
		{
			get { lock(_lock) return _library.ToList(); }
		}

		public void SetFolder(string folder)
		{
			if (string.IsNullOrWhiteSpace(folder)) return;
			var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(folder) ? folder : System.IO.Path.Combine(Directory.GetCurrentDirectory(), folder));
			lock (_lock)
			{
				_folder = full;
				try { Directory.CreateDirectory(_folder); } catch { }
			}
			// Persist to config for next run
			_config["Audio:Folder"] = folder;
			Rescan();
		}

		public void Rescan()
		{
			List<AudioTrack> list = new();
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

			lock (_lock)
			{
				_library = list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
				_rotation.Clear();
				foreach (var t in _library) _rotation.AddLast(t.Path);
				if (_rotCursor == null) _rotCursor = _rotation.First;
			}
		}

		public AudioState GetState()
		{
			lock (_lock)
			{
				return new AudioState
				{
					IsPlaying = _playing,
					Current = _current is string p ? System.IO.Path.GetFileName(p) : (_rotCursor?.Value is string rp ? System.IO.Path.GetFileName(rp) : null),
					Queue = _queue.Select(p => System.IO.Path.GetFileName(p)).ToList(),
					Shuffle = _shuffle,
					Repeat = _repeat
				};
			}
		}

		public void Enqueue(params string[] names)
		{
			if (names == null || names.Length == 0) return;
			lock (_lock)
			{
				var map = _library.ToDictionary(t => t.Name, t => t.Path, StringComparer.OrdinalIgnoreCase);
				foreach (var n in names)
				{
					if (map.TryGetValue(n, out var path)) _queue.Enqueue(path);
				}
			}
		}

		public void ClearQueue()
		{
			lock (_lock)
			{
				_queue.Clear();
			}
		}

		public void Play() { lock(_lock) _playing = true; }
		public void Pause() { lock(_lock) _playing = false; }
		public void Toggle() { lock(_lock) _playing = !_playing; }

		public void Next()
		{
			// Signal current-track cancellation so the streaming loop advances
			CancellationTokenSource? cts;
			lock (_lock) { cts = _trackCts; }
			try { cts?.Cancel(); } catch { }
		}

		public void Prev()
		{
			// For simplicity, treat Prev as a skip
			CancellationTokenSource? cts;
			lock (_lock) { cts = _trackCts; }
			try { cts?.Cancel(); } catch { }
		}

		public void SetShuffle(bool enabled) { lock(_lock) _shuffle = enabled; }
		public void SetRepeat(RepeatMode mode) { lock(_lock) _repeat = mode; }

		// Determine the next track to play and advance internal pointers.
		public bool TryGetNextTrack(out string path)
		{
			path = string.Empty;
			lock (_lock)
			{
				// Immediate queue first
				if (_queue.Count > 0)
				{
					path = _queue.Dequeue();
					_current = path;
					_playing = true;
					return true;
				}

				if (_rotation.Count == 0)
				{
					return false;
				}

				if (_rotCursor == null)
				{
					_rotCursor = _rotation.First;
				}
				else if (_repeat != RepeatMode.One)
				{
					if (_shuffle && _rotation.Count > 0)
					{
						var idx = System.Security.Cryptography.RandomNumberGenerator.GetInt32(_rotation.Count);
						var n = _rotation.First;
						for (int i = 0; i < idx && n != null; i++) n = n.Next;
						_rotCursor = n ?? _rotation.First;
					}
					else
					{
						_rotCursor = _rotCursor.Next ?? (_repeat == RepeatMode.All ? _rotation.First : null);
						if (_rotCursor == null) return false;
					}
				}

				if (_rotCursor == null) return false;
				path = _rotCursor.Value;
				_current = path;
				_playing = true;
				return true;
			}
		}

		// Create/rotate a cancellation source for the current track so UI can skip
		public CancellationToken CreateOrSwapTrackToken(CancellationToken requestAborted)
		{
			CancellationTokenSource? old;
			var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
			lock (_lock)
			{
				old = _trackCts;
				_trackCts = linked;
			}
			try { old?.Cancel(); old?.Dispose(); } catch { }
			return linked.Token;
		}
	}
}
