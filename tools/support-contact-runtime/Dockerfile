# Offline assembly only: context is created by profile.py from verified public inputs.
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374
COPY --chown=0:0 python/ /opt/python/
COPY --chown=0:0 notices/ /opt/probe/notices/
COPY --chown=0:0 artifact/ /opt/probe/artifact/
COPY --chown=0:0 run_preserved_probe.py /opt/probe/run_preserved_probe.py
COPY --chown=0:0 profile-lock.json reviewed-python-aliases.json python-closure.json python-selection.json python-original-inventory.json python-license-inventory.json notice-manifest.json construction-manifest.json /opt/probe/provenance/
ENV PATH=/usr/bin:/bin DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_EnableDiagnostics=0 COMPlus_DbgEnableMiniDump=0
WORKDIR /opt/probe
USER 1654:1654
ENTRYPOINT ["/opt/python/bin/python3.13", "-I", "-B", "/opt/probe/run_preserved_probe.py", "--artifact-dir", "/opt/probe/artifact", "--dotnet", "/usr/share/dotnet/dotnet"]
