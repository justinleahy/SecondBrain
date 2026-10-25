# Build with --platform linux/amd64: the pinned SDK archive targets linux-x64.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build

# Install Microsoft's exact reviewed SDK archive with
# the SHA-512 from its official 10.0 release metadata; global.json selects it.
RUN curl --fail --show-error --location \
        https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.111/dotnet-sdk-10.0.111-linux-x64.tar.gz \
        --output /tmp/dotnet-sdk.tar.gz \
    && printf '%s  %s\n' \
        'aae221be96a3b510d5b6fffefc69d8ad2fa595a1430299419316bb71c65f260a457ca9af24d044e1709b28a9118798caafec535ccfe58f7767c5acb735c00392' \
        '/tmp/dotnet-sdk.tar.gz' | sha512sum --check --strict \
    && tar --gzip --extract --no-same-owner --file /tmp/dotnet-sdk.tar.gz --directory /usr/share/dotnet \
    && rm /tmp/dotnet-sdk.tar.gz

WORKDIR /source
COPY . .
RUN test "$(dotnet --version)" = 10.0.111 \
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

# Signature-verified Wolfi base; the runtime stays aligned with the release pin.
FROM cgr.dev/chainguard/aspnet-runtime@sha256:d60e6201e24b3462db6d17f0698650f604c82b3ac7aa1e1ba13fd430ac2f636c AS runtime-base
USER 0:0
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    SECONDBRAIN_CONFIG=/etc/secondbrain/config.yaml \
    SECONDBRAIN_SERVER_PATH=/app/server/SecondBrain.Server.dll
# Fixed non-root deployment identities; serving processes cannot change them.
RUN dotnet --list-runtimes | grep -Fq 'Microsoft.NETCore.App 10.0.13 ' \
    && dotnet --list-runtimes | grep -Fq 'Microsoft.AspNetCore.App 10.0.13 ' \
    && printf 'secondbrain:x:1654:1654:SecondBrain:/nonexistent:/sbin/nologin\nsecondbrain-extract:x:1655:1654:SecondBrain extractor:/nonexistent:/sbin/nologin\nsecondbrain-sync:x:1656:1654:SecondBrain sync:/nonexistent:/sbin/nologin\n' >> /etc/passwd \
    && printf 'secondbrain:x:1654:\n' >> /etc/group
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
