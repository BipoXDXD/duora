# Synthetic S2 candidate only. No production image publication or license secret in these tests.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS source
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_NOLOGO=true
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY backend/src/ backend/src/
COPY backend/tests/Duora.MediaIsolationProbe/ backend/tests/Duora.MediaIsolationProbe/
FROM source AS sanitizer-build
RUN dotnet restore backend/src/Duora.MediaSanitizer/Duora.MediaSanitizer.csproj --locked-mode \
 && dotnet publish backend/src/Duora.MediaSanitizer/Duora.MediaSanitizer.csproj --no-restore -c Debug -o /out -p:UseAppHost=false
FROM source AS probe-build
RUN dotnet restore backend/tests/Duora.MediaIsolationProbe/Duora.MediaIsolationProbe.csproj --locked-mode \
 && dotnet publish backend/tests/Duora.MediaIsolationProbe/Duora.MediaIsolationProbe.csproj --no-restore -c Debug -o /out -p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/runtime:10.0.13-noble-chiseled-extra@sha256:11f4c921d75b7a1f19d4eb5b7069f9e92a9d1b8412a04b3475a664b62a9b88cb AS sanitizer
WORKDIR /app
COPY --from=sanitizer-build /out/ ./
USER 1654:1654
ENTRYPOINT ["dotnet", "Duora.MediaSanitizer.dll"]
FROM mcr.microsoft.com/dotnet/runtime:10.0.13-noble-chiseled-extra@sha256:11f4c921d75b7a1f19d4eb5b7069f9e92a9d1b8412a04b3475a664b62a9b88cb AS isolation-probe
WORKDIR /app
COPY --from=probe-build /out/ ./
USER 1654:1654
ENTRYPOINT ["dotnet", "Duora.MediaSanitizer.dll"]
