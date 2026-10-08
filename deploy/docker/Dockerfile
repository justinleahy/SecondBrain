# Item 1 placeholder; lane C adds provisioning and deployment hardening in item 14.
# Build with --platform linux/amd64: the pinned SDK archive targets linux-x64.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build

# MCR has no 10.0.105 SDK image tag. Install Microsoft's exact archive with
# the SHA-512 from its official 10.0 release metadata; global.json selects it.
RUN curl --fail --show-error --location \
        https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.105/dotnet-sdk-10.0.105-linux-x64.tar.gz \
        --output /tmp/dotnet-sdk.tar.gz \
    && printf '%s  %s\n' \
        '2b0ed13106e4c859ccb9e3a5f1a450e84605ebd89bd84ac3453b30810ecfa62b6957baaa2ed041cc2e932f512113e16e3ae200fe5c4b48be66767747a06f4e41' \
        '/tmp/dotnet-sdk.tar.gz' | sha512sum --check --strict \
    && tar --gzip --extract --no-same-owner --file /tmp/dotnet-sdk.tar.gz --directory /usr/share/dotnet \
    && rm /tmp/dotnet-sdk.tar.gz

WORKDIR /source
COPY . .
RUN test "$(dotnet --version)" = 10.0.105 \
    && dotnet restore src/SecondBrain.Server/SecondBrain.Server.csproj --locked-mode --runtime linux-x64 -p:RuntimeIdentifier=linux-x64 -p:SelfContained=false \
    && dotnet publish src/SecondBrain.Server/SecondBrain.Server.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        -warnaserror -p:ContinuousIntegrationBuild=true -p:UseAppHost=false --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build --chown=1654:1654 /app/ .
USER 1654:1654
ENTRYPOINT ["dotnet", "SecondBrain.Server.dll"]
