# Debug targets remain synthetic; sanitizer-release requires a licensed trusted build.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS source
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
FROM source AS sanitizer-release-build
RUN dotnet restore backend/src/Duora.MediaSanitizer/Duora.MediaSanitizer.csproj --locked-mode
RUN --mount=type=secret,id=sixlabors_license,required=true \
    dotnet publish backend/src/Duora.MediaSanitizer/Duora.MediaSanitizer.csproj --no-restore -c Release -o /out \
      -p:UseAppHost=false -p:ContinuousIntegrationBuild=true \
      -p:SixLaborsLicenseFile=/run/secrets/sixlabors_license
FROM mcr.microsoft.com/dotnet/runtime:10.0.15-noble-chiseled-extra@sha256:b64d614c3c1c440abe6d57f510c5a4cfc0ccd56a9b96f96c1503efe4a7ac3e56 AS sanitizer
WORKDIR /app
COPY --from=sanitizer-build /out/ ./
USER 1654:1654
ENTRYPOINT ["dotnet", "Duora.MediaSanitizer.dll"]
FROM mcr.microsoft.com/dotnet/runtime:10.0.15-noble-chiseled-extra@sha256:b64d614c3c1c440abe6d57f510c5a4cfc0ccd56a9b96f96c1503efe4a7ac3e56 AS sanitizer-release
WORKDIR /app
COPY --from=sanitizer-release-build /out/ ./
USER 1654:1654
ENTRYPOINT ["dotnet", "Duora.MediaSanitizer.dll"]
FROM mcr.microsoft.com/dotnet/runtime:10.0.15-noble-chiseled-extra@sha256:b64d614c3c1c440abe6d57f510c5a4cfc0ccd56a9b96f96c1503efe4a7ac3e56 AS isolation-probe
WORKDIR /app
COPY --from=probe-build /out/ ./
USER 1654:1654
ENTRYPOINT ["dotnet", "Duora.MediaSanitizer.dll"]
