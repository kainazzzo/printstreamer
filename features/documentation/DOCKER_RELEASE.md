# Docker Build & Release Guide for PrintStreamer

This guide explains how to build, configure, and securely run PrintStreamer in Docker, including best practices for managing sensitive credentials like your YouTube OAuth client secret.

---

## 1. Build the Docker Image

Clone the repository and build the Docker image:

```bash
git clone https://github.com/kainazzzo/printstreamer.git
cd printstreamer
docker build -t printstreamer:latest .
```

---

## 2. Configuration Options

PrintStreamer can be configured using either an `appsettings.json` file or environment variables. **For Docker, environment variables are recommended for secrets.**

### Example `appsettings.json`

```json
{
  "Stream": {
    "Source": "http://YOUR_PRINTER_IP/webcam/?action=stream"
  },
  "YouTube": {
    "OAuth": {
      "ClientId": "YOUR_CLIENT_ID.apps.googleusercontent.com",
      "ClientSecret": "YOUR_CLIENT_SECRET"
    }
  },
  "Moonraker": {
    "ApiUrl": "http://YOUR_MOONRAKER_HOST:7125"
  },
  
}
```

> **Note:** Do NOT bake secrets into your image or commit them to version control.

---

## 3. Using Environment Variables

All configuration keys can be set as environment variables using double underscores (`__`) for nesting. For example:

```bash
```
  
---

## 4. Using Docker Secrets for Sensitive Data

For production, **never pass secrets (like `ClientSecret`) directly as environment variables**. Use Docker secrets to keep them secure.

### Step 1: Create a Docker Secret

First, save your YouTube OAuth client secret (from Google Cloud Console) to a file:

```bash
echo -n "YOUR_CLIENT_SECRET" > youtube_client_secret.txt
```

Create the Docker secret:

```bash
docker secret create youtube_client_secret youtube_client_secret.txt
```

### Step 2: Reference the Secret in Docker Compose

Create a `docker-compose.yml` like this:

```yaml
version: '3.7'
services:
  printstreamer:
    image: printstreamer:latest
    ports:
      - "8080:8080"
    environment:
      - Stream__Source=http://YOUR_PRINTER_IP/webcam/?action=stream
      - YouTube__OAuth__ClientId=YOUR_CLIENT_ID.apps.googleusercontent.com
      - Moonraker__ApiUrl=http://YOUR_MOONRAKER_HOST:7125
      
    secrets:
      - youtube_client_secret
secrets:
  youtube_client_secret:
    file: youtube_client_secret.txt
```

### Step 3: Read the Secret in the Container

Update your entrypoint or application to read the secret from `/run/secrets/youtube_client_secret` and set it as the environment variable `YouTube__OAuth__ClientSecret` before starting the app. For example, add this to your `Dockerfile` or entry script:

```dockerfile
# In Dockerfile (if using an entrypoint script)
COPY entrypoint.sh /entrypoint.sh
ENTRYPOINT ["/entrypoint.sh"]
```

And in `entrypoint.sh`:

```bash
#!/bin/sh
export YouTube__OAuth__ClientSecret=$(cat /run/secrets/youtube_client_secret)
exec dotnet printstreamer.dll "$@"
```

Make sure your script is executable:

```bash
chmod +x entrypoint.sh
```

---

## 5. Running the Container

With Docker Compose:

```bash
docker compose up -d
```

Or with plain Docker (not as secure for secrets):

```bash
docker run -p 8080:8080 \
  -e Stream__Source="http://YOUR_PRINTER_IP/webcam/?action=stream" \
  -e YouTube__OAuth__ClientId="YOUR_CLIENT_ID.apps.googleusercontent.com" \
  -e Moonraker__ApiUrl="http://YOUR_MOONRAKER_HOST:7125" \
  
  printstreamer:latest
```

---

## 6. Security Best Practices

- **Never commit secrets** to your repository.
- Use Docker secrets or a secrets manager for all sensitive values.
- Restrict access to the Docker host and secrets files.
- Regularly rotate your OAuth credentials.

---

## 7. References

- [Docker Secrets Documentation](https://docs.docker.com/engine/swarm/secrets/)
- [Google Cloud Console](https://console.cloud.google.com/apis/credentials)
- [PrintStreamer README](./README.md)

---

**You're ready to build and securely deploy PrintStreamer with Docker!**
