# Porchlight API + WebGL builder. The build context is the repo root (see .github/workflows/deploy.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer stays cached until a project file changes.
COPY shared/Porchlight.Shared.csproj shared/
COPY server/Porchlight.Server/Porchlight.Server.csproj server/Porchlight.Server/
RUN dotnet restore server/Porchlight.Server/Porchlight.Server.csproj

COPY shared/ shared/
COPY server/ server/
RUN dotnet publish server/Porchlight.Server/Porchlight.Server.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /out .

# The Unity WebGL build from the workflow's webgl job. Without one, browser/dist only holds .gitkeep
# and the server runs API-only (which is all the Switch needs).
COPY browser/dist/ ./wwwroot/

ENV ASPNETCORE_ENVIRONMENT=Production \
    URLS=http://0.0.0.0:8080 \
    Porchlight__WebRoot=/app/wwwroot

EXPOSE 8080
ENTRYPOINT ["dotnet", "Porchlight.Server.dll"]
