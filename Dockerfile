# syntax=docker/dockerfile:1.8
FROM mcr.microsoft.com/dotnet/sdk:10.0.101 AS build
WORKDIR /src
COPY . .
ARG VERSION=0.2.5
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.1
USER root
RUN apt-get update && apt-get install -y --no-install-recommends bubblewrap && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /out/ .
RUN mkdir -p /var/lib/murchalka/configuration/grants /var/lib/murchalka/modules/inbox && \
    chown -R $APP_UID:$APP_UID /var/lib/murchalka
USER $APP_UID
ENTRYPOINT ["dotnet", "Murchalka.Runtime.Host.dll"]
