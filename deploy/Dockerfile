# Build with the repository root as the context:
# docker build -f deploy/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release

WORKDIR /src
COPY . .
RUN dotnet restore "Nekolla.Nekostick.slnx"
RUN dotnet build "src/Nekolla.Nekostick.Host/Nekolla.Nekostick.Host.csproj" \
    --configuration "$BUILD_CONFIGURATION" \
    --no-restore \
    --output /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "src/Nekolla.Nekostick.Host/Nekolla.Nekostick.Host.csproj" \
    --configuration "$BUILD_CONFIGURATION" \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# The Host reads these documented bootstrap settings directly.
ENV NEKOSTICK_LISTEN_ADDRESS=0.0.0.0 \
    NEKOSTICK_LISTEN_PORT=8080
EXPOSE 8080

COPY --from=publish /app/publish .
RUN mkdir -p /app/extensions

USER $APP_UID
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "Nekolla.Nekostick.Host.dll", "run"]
