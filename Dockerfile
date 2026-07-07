FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish UCHModLoader.Server/UCHModLoader.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# PORT is injected by Railway at runtime; resolve it at container start,
# not at image build. Falls back to 8080 for local docker runs.
ENTRYPOINT ["/bin/sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet UCHModLoader.Server.dll"]
