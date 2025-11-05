using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PrintStreamer.Services
{
    internal class BroadcastRecord
    {
        public string? BroadcastId { get; set; }
        public string? RtmpUrl { get; set; }
        public string? StreamKey { get; set; }
        public string? Context { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public int TtlMinutes { get; set; }
    }

    internal class YouTubeBroadcastStore
    {
        private readonly string _path;
        private readonly Dictionary<string, BroadcastRecord> _map = new();
        private readonly object _lock = new();

        public YouTubeBroadcastStore(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var list = JsonSerializer.Deserialize<List<BroadcastRecord>>(json) ?? new List<BroadcastRecord>();
                    foreach (var r in list)
                    {
                        if (!string.IsNullOrWhiteSpace(r.Context)) _map[r.Context!] = r;
                    }
                }
            }
            catch
            {
                // ignore parse errors and start fresh
            }
        }

        public Task<BroadcastRecord?> GetAsync(string context)
        {
            if (string.IsNullOrWhiteSpace(context)) return Task.FromResult<BroadcastRecord?>(null);
            lock (_lock)
            {
                return Task.FromResult(_map.TryGetValue(context, out var r) ? r : null);
            }
        }

        public Task SaveAsync(BroadcastRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (string.IsNullOrWhiteSpace(record.Context)) throw new ArgumentException("Context is required", nameof(record));
            lock (_lock)
            {
                _map[record.Context!] = record;
                Persist();
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string context)
        {
            if (string.IsNullOrWhiteSpace(context)) return Task.CompletedTask;
            lock (_lock)
            {
                _map.Remove(context);
                Persist();
            }
            return Task.CompletedTask;
        }

        private void Persist()
        {
            try
            {
                var list = _map.Values.ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch
            {
                // best-effort; ignore persistence errors
            }
        }
    }
}
