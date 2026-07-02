# Build the API (from the repo root: docker build -f src/PartnerCenterBridge.Api/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.sln ./
COPY src/ ./src/
RUN dotnet restore src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj
RUN dotnet publish src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# Links the ghcr.io package to the repository.
LABEL org.opencontainers.image.source="https://github.com/Spillers-Technology/PartnerCenterBridge" \
      org.opencontainers.image.description="Partner Center Bridge API - repeatable cross-tenant Microsoft 365 ops for MSPs" \
      org.opencontainers.image.licenses=MIT
WORKDIR /app

# Phase 3 needs PowerShell 7 + the Exchange Online V3 module in the image so the API can shell out
# to run app-only EXO cmdlets. (Graph handles everything else; EXO covers mailbox config only.)
RUN apt-get update \
 && apt-get install -y --no-install-recommends wget ca-certificates \
 && wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O /tmp/pm.deb \
 && dpkg -i /tmp/pm.deb && rm /tmp/pm.deb \
 && apt-get update && apt-get install -y --no-install-recommends powershell \
 && rm -rf /var/lib/apt/lists/* \
 && pwsh -NoProfile -Command "Set-PSRepository -Name PSGallery -InstallationPolicy Trusted; Install-Module ExchangeOnlineManagement -Scope AllUsers -Force"

COPY --from=build /app ./
# Keys, uploaded packages, and the EXO auth certificate live on mounted volumes.
ENV DataProtection__KeyRingPath=/keys Packages__Path=/packages ASPNETCORE_URLS=http://+:8080
VOLUME ["/keys", "/packages", "/certs"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "PartnerCenterBridge.Api.dll"]
