# Multi-stage build for printstreamer
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# copy everything and publish
COPY . ./

RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN apt-get update && apt-get install -y ffmpeg

# Expose the proxy port
EXPOSE 8080

# By default rely on configuration providers (environment variables / command line)
# Do not copy appsettings.json into the image; pass configuration at runtime instead.
ENTRYPOINT [ "dotnet", "printstreamer.dll" ]
