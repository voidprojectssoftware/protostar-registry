# The registry API image. Built by .github/workflows/edge.yml and release-please.yml; see
# docs/deploy/production.md for how to run it.
#
#   docker build --build-arg VERSION=0.2.0 -t protostar-registry .
#
# VERSION stamps the assembly (and so /v1/meta). The build context has no git history for MinVer to read,
# so the workflows compute the version and pass it in; without it MinVer finds no history and
# falls back to 0.1.0-alpha.0.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=
WORKDIR /src

# Restore on its own layer so a source-only change reuses the package cache.
COPY Directory.Build.props ./
COPY src/Protostar.Registry.Api/Protostar.Registry.Api.csproj src/Protostar.Registry.Api/
COPY src/Protostar.Registry.ServiceDefaults/Protostar.Registry.ServiceDefaults.csproj src/Protostar.Registry.ServiceDefaults/
RUN dotnet restore src/Protostar.Registry.Api/Protostar.Registry.Api.csproj

COPY src/Protostar.Registry.Api/ src/Protostar.Registry.Api/
COPY src/Protostar.Registry.ServiceDefaults/ src/Protostar.Registry.ServiceDefaults/
RUN dotnet publish src/Protostar.Registry.Api/Protostar.Registry.Api.csproj \
      -c Release --no-restore -o /app \
      -p:MinVerVersionOverride="${VERSION}"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ASP.NET Core keeps its Data Protection keys (the sign-in cookie and the GitHub sign-in's state) under the
# user's home. Creating the directory owned by the app user lets a named volume mounted there inherit that
# ownership, so the keys survive container rebuilds (docs/deploy/production.md).
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys && chown -R app:app /home/app/.aspnet

COPY --from=build /app ./

# Plain HTTP on 8080 inside the container; TLS terminates at the reverse proxy in front of it.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# The base image's non-root user (UID 1654).
USER $APP_UID

# The runtime image has no curl or wget; bash's /dev/tcp is enough to ask /health for its status line.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["dotnet", "Protostar.Registry.Api.dll"]
