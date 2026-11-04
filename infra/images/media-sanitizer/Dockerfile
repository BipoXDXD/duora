# Debug targets remain synthetic; sanitizer-release requires a licensed trusted build.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS source
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
FROM mcr.microsoft.com/dotnet/runtime:10.0.14-noble-chiseled-extra@sha256:2fbc014e87df3d50948f8710f200ddca0183388904ec35c8ad7e481ef6f8a230 AS sanitizer
WORKDIR /app
COPY --from=sanitizer-build /out/ ./
USER 1654:1654
ENTRYPOINT ["dotnet", "Duora.MediaSanitizer.dll"]
FROM mcr.microsoft.com/dotnet/runtime:10.0.14-noble-chiseled-extra@sha256:2fbc014e87df3d50948f8710f200ddca0183388904ec35c8ad7e481ef6f8a230 AS sanitizer-release
WORKDIR /app
COPY --from=sanitizer-release-build /out/ ./
USER 1654:1654
ENTRYPOINT ["dotnet", "Duora.MediaSanitizer.dll"]
FROM mcr.microsoft.com/dotnet/runtime:10.0.14-noble-chiseled-extra@sha256:2fbc014e87df3d50948f8710f200ddca0183388904ec35c8ad7e481ef6f8a230 AS isolation-probe
WORKDIR /app
COPY --from=probe-build /out/ ./
USER 1654:1654
ENTRYPOINT ["dotnet", "Duora.MediaSanitizer.dll"]
