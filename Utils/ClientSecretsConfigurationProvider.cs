using Microsoft.Extensions.Configuration;
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

        public ClientSecretsConfigurationProvider(ClientSecretsConfigurationSource source)
        {
            _source = source;
        }

        public override void Load()
        {
            var path = _source.FilePath;
            if (string.IsNullOrWhiteSpace(path)) return;

            if (!File.Exists(path))
            {
                if (_source.Optional) return;
                throw new FileNotFoundException("Client secrets file not found", path);
            }

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Google client secret files sometimes have a top-level "installed" or "web" object
                JsonElement creds;
                if (root.TryGetProperty("installed", out creds) || root.TryGetProperty("web", out creds))
                {
                    if (creds.TryGetProperty("client_id", out var cid) && cid.ValueKind == JsonValueKind.String)
                    {
                        Data["YouTube:OAuth:ClientId"] = cid.GetString() ?? string.Empty;
                    }

                    if (creds.TryGetProperty("client_secret", out var csec) && csec.ValueKind == JsonValueKind.String)
                    {
                        Data["YouTube:OAuth:ClientSecret"] = csec.GetString() ?? string.Empty;
                    }
                }
                else
                {
                    // Fallback: try to read direct properties
                    if (root.TryGetProperty("client_id", out var cid2) && cid2.ValueKind == JsonValueKind.String)
                        Data["YouTube:OAuth:ClientId"] = cid2.GetString() ?? string.Empty;
                    if (root.TryGetProperty("client_secret", out var csec2) && csec2.ValueKind == JsonValueKind.String)
                        Data["YouTube:OAuth:ClientSecret"] = csec2.GetString() ?? string.Empty;
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
