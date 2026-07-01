# Build the API (from the repo root: docker build -f src/PartnerCenterBridge.Api/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.sln ./
COPY src/ ./src/
RUN dotnet restore src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj
RUN dotnet publish src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Keys and uploaded packages live on mounted volumes so they survive restarts.
ENV DataProtection__KeyRingPath=/keys Packages__Path=/packages ASPNETCORE_URLS=http://+:8080
VOLUME ["/keys", "/packages"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "PartnerCenterBridge.Api.dll"]
