FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/Server -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The base image defines APP_UID for exactly this; nothing here needs root, and the Server is
# the one component exposed to the internet.
USER $APP_UID
ENTRYPOINT ["dotnet", "AngstromCommander.Server.dll"]
