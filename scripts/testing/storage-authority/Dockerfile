# Disposable native-arm64 acceptance runner, never a production service image.
FROM mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa
ENV DOTNET_PROCESSOR_COUNT=2 DOTNET_GCHeapHardLimit=0xC0000000
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN dotnet restore scripts/testing/storage-authority/RuntimeChecks.csproj --locked-mode --disable-parallel
RUN dotnet build scripts/testing/storage-authority/RuntimeChecks.csproj --no-restore -m:1 /p:UseSharedCompilation=false
# The image is non-root by default. Only the disposable identity fixture opts
# into root orchestration to create and then drop to the two distinct test UIDs.
USER 1654:1654
ENTRYPOINT ["dotnet", "/src/scripts/testing/storage-authority/bin/Debug/net10.0/RuntimeChecks.dll"]
