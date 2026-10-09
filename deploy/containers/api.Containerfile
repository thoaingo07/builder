# Builder.Api (+ the migrator console at /migrator). Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Builder.slnx ./
COPY db/ db/
COPY src/ src/
RUN dotnet publish src/Builder.Api -c Release -o /out/api \
 && dotnet publish src/Builder.Migrations -c Release -o /out/migrator

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# git: the planner reads Taskfiles and the editor commits them
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/api ./
COPY --from=build /out/migrator /migrator
ENV ASPNETCORE_URLS=http://+:19100 \
    Storage__DataDirectory=/data
RUN mkdir -p /data && chown $APP_UID /data
VOLUME /data
EXPOSE 19100
USER $APP_UID
ENTRYPOINT ["dotnet", "Builder.Api.dll"]
