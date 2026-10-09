FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
ARG CONFIGURATION=Release
RUN dotnet publish "Samples~/DemoArena/Hosting/DHMP.UnityArenaHost~/DHMP.UnityArenaHost.csproj" -c "$CONFIGURATION" --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "DHMP.UnityArenaHost.dll"]
