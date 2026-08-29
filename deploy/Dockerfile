# Build with the repository root as the context. The image is linux/amd64 because
# release output is published for the linux-x64 RID:
# docker buildx build --platform linux/amd64 -f deploy/Dockerfile .
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release

WORKDIR /src
COPY . .
RUN dotnet restore "Nekolla.Nekostick.slnx" --runtime linux-x64

FROM build AS publish
ARG BUILD_CONFIGURATION=Release

# The Host publish target publishes and embeds the matching NativeHelper for the
# same configuration and linux-x64 RID. Only the Host is a deployable artifact.
RUN dotnet publish "src/Nekolla.Nekostick.Host/Nekolla.Nekostick.Host.csproj" \
    --configuration "$BUILD_CONFIGURATION" \
    --runtime linux-x64 \
    --self-contained false \
    --no-restore \
    --output /app/publish \
    /p:PublishReadyToRun=true \
    /p:PublishSingleFile=true \
    /p:UseAppHost=true

FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# The Host reads these documented bootstrap settings directly.
ENV NEKOSTICK_LISTEN_ADDRESS=0.0.0.0 \
    NEKOSTICK_LISTEN_PORT=8080
EXPOSE 8080

COPY --from=publish /app/publish .
RUN mkdir -p /app/extensions /app/data

USER $APP_UID
STOPSIGNAL SIGTERM
ENTRYPOINT ["/app/Nekolla.Nekostick.Host", "run"]

