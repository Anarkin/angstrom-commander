FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/Daemon -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# A Daemon runs on someone's machine and reaches their files; root is the last thing it needs.
# The mount points are created and owned here because Docker takes an empty named volume's
# ownership from the image directory it covers — without this the Daemon could not write to
# its own state or upload directory.
RUN mkdir -p /state /uploads && chown -R $APP_UID:$APP_UID /state /uploads
USER $APP_UID

ENTRYPOINT ["dotnet", "AngstromCommander.Daemon.dll"]
