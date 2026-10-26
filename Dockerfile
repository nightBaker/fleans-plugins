# Builds the plugin host image (every plugin in this repo), for linux/amd64 and linux/arm64:
#   docker build -t fleans-plugins .
#   docker buildx build --platform linux/amd64,linux/arm64 -t fleans-plugins .
# The SDK stage runs on the build machine's platform and cross-publishes for the target
# (no emulation); only the runtime stage is per-platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH") \
 && dotnet publish src/Fleans.Plugins.Host/Fleans.Plugins.Host.csproj -c Release -a "$arch" --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Fleans.Plugins.Host.dll"]
