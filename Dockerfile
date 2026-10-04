# .NET API. Build from the repository root:
#   docker build -t shop-api-dotnet .
# The server needs the same PostgreSQL schema the Nest API (eCommerce-api) migrates; see README.md.

# ---- build stage: restore, then publish a framework-dependent build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/EcomApi/EcomApi.csproj src/EcomApi/
RUN dotnet restore src/EcomApi/EcomApi.csproj
COPY src src
RUN dotnet publish src/EcomApi/EcomApi.csproj -c Release -o /out --no-restore -p:UseAppHost=false

# ---- runtime stage: the ASP.NET Core runtime and the published output, as an unprivileged user ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_ENVIRONMENT=Production \
    PORT=3333 \
    DOTNET_gcServer=0 \
    DOTNET_EnableDiagnostics=0
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
EXPOSE 3333
HEALTHCHECK --interval=15s --timeout=3s --start-period=15s --retries=3 CMD ["dotnet", "EcomApi.dll", "-healthcheck"]
ENTRYPOINT ["dotnet", "EcomApi.dll"]
