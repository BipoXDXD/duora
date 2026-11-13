# Standalone synthetic experiment; no production image or service uses this Dockerfile.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:35c2411e519ef047d430b7237234ff56d1f6e1dfde49320c7583d2e970942cf6
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
