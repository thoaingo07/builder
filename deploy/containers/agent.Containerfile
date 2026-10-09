# Builder.Agent with the tools jobs need: git, go-task, ssh/scp, docker CLI (talks to a mounted
# Docker or Podman socket), kubectl, and optionally the Azure CLI (large: --build-arg INSTALL_AZ=true).
# Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Builder.Contracts/ src/Builder.Contracts/
COPY src/Builder.Agent/ src/Builder.Agent/
RUN dotnet publish src/Builder.Agent -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG TARGETARCH=amd64
ARG TASK_VERSION=3.52.0
ARG KUBECTL_VERSION=1.34.1
ARG DOCKER_VERSION=28.5.1
ARG INSTALL_AZ=false

RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates curl openssh-client tar gzip bash jq \
 && curl -fsSL "https://github.com/go-task/task/releases/download/v${TASK_VERSION}/task_linux_${TARGETARCH}.tar.gz" \
      | tar -xz -C /usr/local/bin task \
 && curl -fsSL -o /usr/local/bin/kubectl "https://dl.k8s.io/release/v${KUBECTL_VERSION}/bin/linux/${TARGETARCH}/kubectl" \
 && chmod +x /usr/local/bin/kubectl \
 && DARCH=$([ "$TARGETARCH" = "arm64" ] && echo aarch64 || echo x86_64) \
 && curl -fsSL "https://download.docker.com/linux/static/stable/${DARCH}/docker-${DOCKER_VERSION}.tgz" \
      | tar -xz -C /usr/local/bin --strip-components=1 docker/docker \
 && if [ "$INSTALL_AZ" = "true" ]; then curl -fsSL https://aka.ms/InstallAzureCLIDeb | bash; fi \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out ./
ENV Agent__WorkDirectory=/work \
    DOTNET_gcServer=0
VOLUME /work
# Runs as root so it can use a mounted container socket; jobs run inside this container.
ENTRYPOINT ["dotnet", "Builder.Agent.dll"]
