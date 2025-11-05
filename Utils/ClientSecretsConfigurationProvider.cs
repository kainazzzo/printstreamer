using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace PrintStreamer.Utils
{
    /// <summary>
    /// Configuration source that reads a Google OAuth client_secrets JSON file (the JSON you download
    /// from Google Cloud Console) and injects YouTube:OAuth:ClientId and YouTube:OAuth:ClientSecret
    /// into the configuration.
    /// </summary>
    public class ClientSecretsConfigurationSource : IConfigurationSource
    {
        public string FilePath { get; }
        public bool Optional { get; }
        public ILogger? Logger { get; }

        public ClientSecretsConfigurationSource(string filePath, bool optional = true, ILogger? logger = null)
        {
            FilePath = filePath;
            Optional = optional;
            Logger = logger;
        }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return new ClientSecretsConfigurationProvider(this);
        }
    }

    public class ClientSecretsConfigurationProvider : ConfigurationProvider
    {
        private readonly ClientSecretsConfigurationSource _source;
        private readonly ILogger? _logger;

        public ClientSecretsConfigurationProvider(ClientSecretsConfigurationSource source)
        {
            _source = source;
            _logger = source.Logger;
        }

        public override void Load()
        {
            var path = _source.FilePath;
            _logger?.LogInformation("ClientSecretsProvider Load() called with path: {Path}", path);
            
            if (string.IsNullOrWhiteSpace(path))
            {
                _logger?.LogWarning("ClientSecretsProvider: FilePath is null or empty");
                return;
            }

            if (!File.Exists(path))
            {
                _logger?.LogWarning("ClientSecretsProvider: File does not exist at {Path}", path);
                if (_source.Optional) return;
                throw new FileNotFoundException("Client secrets file not found", path);
            }

            try
            {
                var json = File.ReadAllText(path);
                _logger?.LogInformation("ClientSecretsProvider: Read {Length} bytes from {Path}", json?.Length ?? 0, path);
                
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger?.LogWarning("ClientSecretsProvider: File at {Path} is empty", path);
                    return;
                }
                
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Google client secret files sometimes have a top-level "installed" or "web" object
                JsonElement creds;
                if (root.TryGetProperty("installed", out creds) || root.TryGetProperty("web", out creds))
                {
                    _logger?.LogInformation("ClientSecretsProvider: Found 'installed' or 'web' property");
                    
                    if (creds.TryGetProperty("client_id", out var cid) && cid.ValueKind == JsonValueKind.String)
                    {
                        var clientId = cid.GetString() ?? string.Empty;
                        Data["YouTube:OAuth:ClientId"] = clientId;
                        _logger?.LogInformation("ClientSecretsProvider: Set ClientId (length={Length})", clientId.Length);
                    }

                    if (creds.TryGetProperty("client_secret", out var csec) && csec.ValueKind == JsonValueKind.String)
                    {
                        var clientSecret = csec.GetString() ?? string.Empty;
                        Data["YouTube:OAuth:ClientSecret"] = clientSecret;
                        _logger?.LogInformation("ClientSecretsProvider: Set ClientSecret (length={Length})", clientSecret.Length);
                    }
                }
                else
                {
                    _logger?.LogInformation("ClientSecretsProvider: No 'installed' or 'web' property, trying direct properties");
                    
                    // Fallback: try to read direct properties
                    if (root.TryGetProperty("client_id", out var cid2) && cid2.ValueKind == JsonValueKind.String)
                    {
                        var clientId = cid2.GetString() ?? string.Empty;
                        Data["YouTube:OAuth:ClientId"] = clientId;
                        _logger?.LogInformation("ClientSecretsProvider: Set ClientId directly (length={Length})", clientId.Length);
                    }
                    if (root.TryGetProperty("client_secret", out var csec2) && csec2.ValueKind == JsonValueKind.String)
                    {
                        var clientSecret = csec2.GetString() ?? string.Empty;
                        Data["YouTube:OAuth:ClientSecret"] = clientSecret;
                        _logger?.LogInformation("ClientSecretsProvider: Set ClientSecret directly (length={Length})", clientSecret.Length);
                    }
                }

                // Log loaded values (do not log secrets in full)
                try
                {
                    var gotId = Data.ContainsKey("YouTube:OAuth:ClientId") && !string.IsNullOrEmpty(Data["YouTube:OAuth:ClientId"]);
                    var gotSecret = Data.ContainsKey("YouTube:OAuth:ClientSecret") && !string.IsNullOrEmpty(Data["YouTube:OAuth:ClientSecret"]);
                    _source.Logger?.LogInformation("Loaded client secrets from '{Path}': ClientId={ClientIdPresent}, ClientSecret={ClientSecretPresent}", 
                        path, gotId ? "present" : "missing", gotSecret ? "present" : "missing");
                }
                catch { }
            }
            catch (JsonException jex)
            {
                _source.Logger?.LogWarning(jex, "Failed to parse client secrets file '{Path}': {Message}", path, jex.Message);
            }
            catch (Exception ex)
            {
                _source.Logger?.LogWarning(ex, "Error reading client secrets file '{Path}': {Message}", path, ex.Message);
            }
        }
    }
}
