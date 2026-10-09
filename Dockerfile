# Build stage.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Queuer.slnx ./
COPY src/Queuer/Queuer.csproj src/Queuer/
RUN dotnet restore src/Queuer/Queuer.csproj
COPY src/Queuer/ src/Queuer/
RUN dotnet publish src/Queuer/Queuer.csproj -c Release -o /app/publish --no-restore

# Run stage. No ports: bot polls Telegram outbound.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/data && chown app:app /app/data
USER app
ENTRYPOINT ["dotnet", "Queuer.dll"]
