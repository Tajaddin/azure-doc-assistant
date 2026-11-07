# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/DocAssistant.Core/DocAssistant.Core.csproj src/DocAssistant.Core/
COPY src/DocAssistant.Infrastructure/DocAssistant.Infrastructure.csproj src/DocAssistant.Infrastructure/
COPY src/DocAssistant.Api/DocAssistant.Api.csproj src/DocAssistant.Api/
RUN dotnet restore src/DocAssistant.Api/DocAssistant.Api.csproj
COPY src/DocAssistant.Core src/DocAssistant.Core
COPY src/DocAssistant.Infrastructure src/DocAssistant.Infrastructure
COPY src/DocAssistant.Api src/DocAssistant.Api
RUN dotnet publish src/DocAssistant.Api/DocAssistant.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends wget && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
# the React build is copied here by the web stage of the compose file when present
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 CMD wget -qO- http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "DocAssistant.Api.dll"]
