# Base images are pinned by digest, not only by tag: Railway rebuilds this file rather than deploying
# the image CI scanned, and a tag can move between the scan and that build. Dependabot's docker
# ecosystem proposes digest bumps, and each bump goes through the scan like any other change.
FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/ShoppyShop.Api/ShoppyShop.Api.csproj --locked-mode
RUN dotnet publish src/ShoppyShop.Api/ShoppyShop.Api.csproj \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled@sha256:5165aeeb688df15e869a56a7584bb94bf16a14e0b3ba77b49fcdbe350f6b8020 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

COPY --from=build /app/publish .

USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["dotnet", "ShoppyShop.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "ShoppyShop.Api.dll"]
