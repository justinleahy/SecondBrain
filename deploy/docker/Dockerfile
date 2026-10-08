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
    && dotnet restore SecondBrain.slnx --locked-mode --runtime linux-x64 -p:RuntimeIdentifier=linux-x64 -p:SelfContained=false \
    && dotnet publish src/SecondBrain.Server/SecondBrain.Server.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        -warnaserror -p:ContinuousIntegrationBuild=true -p:UseAppHost=false --output /publish/server \
    && dotnet publish src/SecondBrain.Cli/SecondBrain.Cli.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        -warnaserror -p:ContinuousIntegrationBuild=true -p:UseAppHost=false --output /publish/cli \
    && dotnet publish src/SecondBrain.Extractor/SecondBrain.Extractor.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        -warnaserror -p:ContinuousIntegrationBuild=true -p:UseAppHost=false --output /publish/extractor

# The mock is only copied into the explicit CI fixture target below.
RUN dotnet publish tests/SecondBrain.MockProvider/SecondBrain.MockProvider.csproj \
    --configuration Release --runtime linux-x64 --self-contained false --no-restore \
    -warnaserror -p:ContinuousIntegrationBuild=true -p:UseAppHost=false --output /publish/mock-provider

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime-base
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    SECONDBRAIN_CONFIG=/etc/secondbrain/config.yaml \
    SECONDBRAIN_SERVER_PATH=/app/server/SecondBrain.Server.dll
# The base image already reserves UID/GID 1654. Name the deployment identities
# without any runtime ability to add users or switch UIDs.
RUN sed -i 's/^app:/secondbrain:/' /etc/passwd /etc/group \
    && printf 'secondbrain-extract:x:1655:1654:SecondBrain extractor:/nonexistent:/usr/sbin/nologin\nsecondbrain-sync:x:1656:1656:SecondBrain sync:/nonexistent:/usr/sbin/nologin\n' >> /etc/passwd \
    && printf 'secondbrain-sync:x:1656:\n' >> /etc/group
COPY --chmod=0555 deploy/docker/entrypoint.sh /app/entrypoint.sh
ENTRYPOINT ["/app/entrypoint.sh"]

FROM runtime-base AS extractor
COPY --from=build --chown=1655:1654 /publish/extractor/ /app/extractor/
USER 1655:1654
CMD ["dotnet", "/app/extractor/SecondBrain.Extractor.dll", "--socket", "/run/secondbrain/extractor.sock"]

FROM runtime-base AS mock-provider
COPY --from=build --chown=1654:1654 /publish/mock-provider/ /app/mock-provider/
USER 1654:1654
CMD ["dotnet", "/app/mock-provider/SecondBrain.MockProvider.dll", "--urls", "http://127.0.0.1:8181"]

FROM runtime-base AS server
EXPOSE 8080
COPY --from=build --chown=1654:1654 /publish/server/ /app/server/
COPY --from=build --chown=1654:1654 /publish/cli/ /app/cli/
COPY --chown=1654:1654 deploy/ /app/deploy/
USER 1654:1654
CMD ["dotnet", "/app/cli/brain.dll", "serve"]
