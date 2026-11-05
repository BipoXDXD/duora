# Test tool only, Linux arm64 SDK 10.0.403 pinned by digest; the recorded experiments ran on 10.0.402.
FROM mcr.microsoft.com/dotnet/sdk@sha256:f34803987e201bd321984712d40a4bed1f797d4756b9b21eb5b6ddd2b178d2ef
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props .editorconfig global.json ./
COPY backend/src/Duora.Core/Media/IObjectStore.cs backend/src/Duora.Core/Media/ObjectKey.cs backend/src/Duora.Core/Media/PhotoVariants.cs backend/src/Duora.Core/Media/
COPY backend/src/Duora.Infrastructure/Media/ExactLengthStream.cs backend/src/Duora.Infrastructure/Media/FileSystemObjectStore.cs backend/src/Duora.Infrastructure/Media/
COPY scripts/testing/private-generation-closure/ scripts/testing/private-generation-closure/
RUN dotnet build scripts/testing/private-generation-closure/Probe.csproj -p:RestoreLockedMode=true -o /probe
RUN mkdir /scratch && chown 1654:1654 /scratch
ENV DUORA_CLOSURE_PROBE=/probe/Probe.dll DUORA_CLOSURE_ROOT=/scratch
USER 1654:1654
WORKDIR /tmp
ENTRYPOINT ["python3", "/src/scripts/testing/private-generation-closure/test_protocol.py"]
