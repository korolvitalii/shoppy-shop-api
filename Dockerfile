FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG RDS_CA_BUNDLE_URL=https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem

RUN mkdir -p /rds-certs \
    && curl --fail --show-error --silent --location \
       "$RDS_CA_BUNDLE_URL" \
       --output /rds-certs/global-bundle.pem \
    && test -s /rds-certs/global-bundle.pem

COPY . .
RUN dotnet restore src/ShoppyShop.Api/ShoppyShop.Api.csproj --locked-mode
RUN dotnet publish src/ShoppyShop.Api/ShoppyShop.Api.csproj \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080
ENV PGSSLROOTCERT=/app/certs/global-bundle.pem

EXPOSE 8080

COPY --from=build /app/publish .
COPY --from=build /rds-certs/global-bundle.pem /app/certs/global-bundle.pem

USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["dotnet", "ShoppyShop.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "ShoppyShop.Api.dll"]