# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY StockSentinel.Web.csproj ./
RUN dotnet restore StockSentinel.Web.csproj -a "$TARGETARCH"
COPY *.cs ./
COPY wwwroot/ ./wwwroot/
COPY appsettings.example.json ./appsettings.json
COPY LICENSE THIRD-PARTY-NOTICES.txt ./
RUN dotnet publish StockSentinel.Web.csproj -c Release -a "$TARGETARCH" --no-restore \
    --self-contained false -p:UseAppHost=false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /app/data && chown "$APP_UID:$APP_UID" /app/data
ENV DOTNET_ENVIRONMENT=Production \
    URLS=http://+:8080 \
    Sentinel__DatabasePath=/app/data/sentinel.db
USER $APP_UID
# Bash is included in the standard runtime image; no extra health-check package.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/bin/bash", "-c", "port=${URLS##*:}; exec 3<>/dev/tcp/127.0.0.1/$port; printf 'GET /health HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3; read -r status <&3; [[ $status == *' 200 '* ]]"]
ENTRYPOINT ["dotnet", "StockSentinel.Web.dll"]
