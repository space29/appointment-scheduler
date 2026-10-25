FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Scheduler.Api/Scheduler.Api.csproj src/Scheduler.Api/
RUN dotnet restore src/Scheduler.Api/Scheduler.Api.csproj
COPY src/ src/
RUN dotnet publish src/Scheduler.Api/Scheduler.Api.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Scheduler.Api.dll"]
