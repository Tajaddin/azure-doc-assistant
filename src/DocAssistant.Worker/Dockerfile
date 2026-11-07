# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/DocAssistant.Core/DocAssistant.Core.csproj src/DocAssistant.Core/
COPY src/DocAssistant.Infrastructure/DocAssistant.Infrastructure.csproj src/DocAssistant.Infrastructure/
COPY src/DocAssistant.Worker/DocAssistant.Worker.csproj src/DocAssistant.Worker/
RUN dotnet restore src/DocAssistant.Worker/DocAssistant.Worker.csproj
COPY src/DocAssistant.Core src/DocAssistant.Core
COPY src/DocAssistant.Infrastructure src/DocAssistant.Infrastructure
COPY src/DocAssistant.Worker src/DocAssistant.Worker
RUN dotnet publish src/DocAssistant.Worker/DocAssistant.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Npgsql probes the GSSAPI library at start-up; the runtime image does not ship it
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENV DOTNET_EnableDiagnostics=0 ASPNETCORE_URLS=http://+:9464
USER $APP_UID
EXPOSE 9464
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 CMD curl -fsS http://127.0.0.1:9464/healthz || exit 1
ENTRYPOINT ["dotnet", "DocAssistant.Worker.dll"]
