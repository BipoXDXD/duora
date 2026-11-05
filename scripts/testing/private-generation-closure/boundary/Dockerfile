# Standalone synthetic experiment; no production image or service uses this Dockerfile.
FROM mcr.microsoft.com/dotnet/sdk@sha256:f34803987e201bd321984712d40a4bed1f797d4756b9b21eb5b6ddd2b178d2ef
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props .editorconfig global.json ./
COPY backend/src/Duora.Core/Media/IObjectStore.cs backend/src/Duora.Core/Media/ObjectKey.cs backend/src/Duora.Core/Media/PhotoVariants.cs backend/src/Duora.Core/Media/
COPY backend/src/Duora.Infrastructure/Media/ExactLengthStream.cs backend/src/Duora.Infrastructure/Media/FileSystemObjectStore.cs backend/src/Duora.Infrastructure/Media/
COPY scripts/testing/private-generation-closure/Barrier.cs scripts/testing/private-generation-closure/
COPY scripts/testing/private-generation-closure/boundary/ scripts/testing/private-generation-closure/boundary/
RUN dotnet build scripts/testing/private-generation-closure/boundary/BoundaryProbe.csproj -p:RestoreLockedMode=true -o /probe
USER 17010:17010
ENTRYPOINT ["dotnet", "/probe/BoundaryProbe.dll"]
