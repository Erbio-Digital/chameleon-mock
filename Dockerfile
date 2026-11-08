# Panel (web/) -> src/Chameleon.Host/wwwroot/ui
FROM node:20-alpine AS web
RUN corepack enable && corepack prepare pnpm@9.15.0 --activate
WORKDIR /src/web
COPY web/package.json web/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY web/ ./
RUN pnpm build

# Build (Host only: connectors are plugins installed at startup)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Chameleon.sln .
COPY src/Chameleon.Engine/Chameleon.Engine.csproj src/Chameleon.Engine/
COPY src/Chameleon.Sdk/Chameleon.Sdk.csproj src/Chameleon.Sdk/
COPY src/Chameleon.Storage.Postgres/Chameleon.Storage.Postgres.csproj src/Chameleon.Storage.Postgres/
COPY src/Chameleon.Host/Chameleon.Host.csproj src/Chameleon.Host/
RUN dotnet restore src/Chameleon.Host/Chameleon.Host.csproj
COPY . .
COPY --from=web /src/src/Chameleon.Host/wwwroot/ui src/Chameleon.Host/wwwroot/ui
RUN dotnet publish src/Chameleon.Host/Chameleon.Host.csproj -c Release -o /app

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
COPY docker/chameleon /usr/local/bin/chameleon
# Connector packages are downloaded here at startup (or by `chameleon install`).
RUN chmod 755 /usr/local/bin/chameleon && mkdir -p /plugins && chown "$APP_UID" /plugins
ENV CHAMELEON_PLUGINS_DIR=/plugins
# Without ASPNETCORE_URLS the Host binds to localhost:5000 (local dev), unreachable from outside the container.
ENV ASPNETCORE_URLS=http://+:8080
# Local dev secrets volume (CHAMELEON_DEV_SECRETS_FILE), writable by the non-root user.
RUN mkdir -p /data && chown "$APP_UID" /data
# Without oidc the secret key and CA are generated here on first start; with oidc they come from configuration.
ENV CHAMELEON_DEV_SECRETS_FILE=/data/dev-secrets.json
USER $APP_UID
# 8080 HTTP; 8443 mTLS when CHAMELEON_MTLS_PORT=8443.
EXPOSE 8080 8443
ENTRYPOINT ["dotnet", "Chameleon.Host.dll"]
