FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AndrewDemo.AgentRateLimit.DatabaseInit/AndrewDemo.AgentRateLimit.DatabaseInit.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AndrewDemo.AgentRateLimit.DatabaseInit.dll"]
