# syntax=docker/dockerfile:1.8
FROM mcr.microsoft.com/dotnet/sdk:10.0.101 AS build
WORKDIR /src
COPY . .
ARG VERSION=0.3.0
ARG NUGET_USERNAME
RUN --mount=type=secret,id=nuget_token \
    cp NuGet.Config /tmp/NuGet.Config && \
    dotnet nuget update source murchalka \
      --username "${NUGET_USERNAME}" \
      --password "$(cat /run/secrets/nuget_token)" \
      --store-password-in-clear-text \
      --configfile NuGet.Config && \
    dotnet restore src/Murchalka.Runtime.Host/Murchalka.Runtime.Host.csproj --configfile NuGet.Config && \
    mv /tmp/NuGet.Config NuGet.Config && \
    dotnet publish src/Murchalka.Runtime.Host/Murchalka.Runtime.Host.csproj \
      --configuration Release \
      --no-restore \
      --output /out \
      -p:UseAppHost=false \
      -p:Version="${VERSION}"

FROM debian:bookworm-slim AS native-build
RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY src/Murchalka.Runtime.ModuleSupervisor.Native/murchalka-netns-exec.c .
RUN mkdir -p /out && gcc -std=c17 -O2 -Wall -Wextra -Werror -pedantic murchalka-netns-exec.c -o /out/murchalka-netns-exec

FROM mcr.microsoft.com/dotnet/aspnet:10.0.1
USER root
RUN apt-get update && apt-get install -y --no-install-recommends bubblewrap && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /out/ .
COPY --from=native-build --chown=root:root /out/murchalka-netns-exec /usr/local/libexec/murchalka-netns-exec
RUN mkdir -p /var/lib/murchalka/configuration/grants /var/lib/murchalka/modules/inbox && \
    chown -R $APP_UID:$APP_UID /var/lib/murchalka
USER $APP_UID
ENTRYPOINT ["dotnet", "Murchalka.Runtime.Host.dll"]
