# Multi-stage build for printstreamer
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy csproj and restore first for better layer caching
COPY *.csproj ./
RUN dotnet restore

# copy everything and publish
COPY . ./
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Expose the proxy port
EXPOSE 8080

# By default rely on configuration providers (environment variables / command line)
# Do not copy appsettings.json into the image; pass configuration at runtime instead.
ENTRYPOINT [ "dotnet", "printstreamer.dll" ]
