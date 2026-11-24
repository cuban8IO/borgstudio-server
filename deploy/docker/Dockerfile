# BorgStudio Server container image.
#
# Built by the release workflow from the framework-dependent Linux builds it prepares in the build
# context:  <context>/amd64/…  and  <context>/arm64/…  (plus this file).
#
#   docker run -d --name borgstudio-server -p 8080:8080 -v borgstudio-data:/app/data ghcr.io/cuban8io/borgstudio-server
#
# The server speaks plain HTTP on 8080, meant for a reverse proxy in front of it. For HTTPS in the
# container set BorgStudio__Https__Enabled=true (port 8443) and publish that port. borg clients
# connect over SSH on port 2222 (publish it too: -p 2222:2222); every datastore besides
# /app/data/repositories needs its own volume.
FROM mcr.microsoft.com/dotnet/aspnet:10.0

ARG TARGETARCH
ARG VERSION=0.0.0-dev

LABEL org.opencontainers.image.title="BorgStudio Server" \
      org.opencontainers.image.description="Self-hosted server for BorgStudio" \
      org.opencontainers.image.source="https://github.com/cuban8IO/borgstudio-server" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}"

# OpenSSH's server and borg from Debian: the server runs sshd itself, under its own account, for
# borg connections. The account needs a shell, since sshd runs borg through it; it has no password.
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-server borgbackup \
    && rm -rf /var/lib/apt/lists/* \
    && usermod --shell /bin/sh app

WORKDIR /app
COPY ${TARGETARCH}/ /app/
# Artifacts lose the executable bit on the way here.
RUN chmod +x /app/BorgStudio.Server && mkdir -p /app/data && chown app:app /app/data

# Only BorgStudio's own settings decide the listeners (the base image would open 8080 by itself).
ENV ASPNETCORE_HTTP_PORTS= \
    BorgStudio__Https__Enabled=false \
    BorgStudio__Http__Enabled=true \
    BorgStudio__Http__Port=8080 \
    BorgStudio__Ssh__BorgPath=/usr/bin/borg \
    DOTNET_NOLOGO=1

USER app
VOLUME /app/data
EXPOSE 8080 8443 2222

ENTRYPOINT ["/app/BorgStudio.Server"]
