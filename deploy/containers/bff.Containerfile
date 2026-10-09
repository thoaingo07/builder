# Builder.Bff serving the built Vue UI. Build context: repository root.
FROM docker.io/library/node:22-bookworm-slim AS ui
WORKDIR /ui
COPY ui/package.json ui/package-lock.json ./
RUN npm ci
COPY ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Builder.Bff/ src/Builder.Bff/
RUN dotnet publish src/Builder.Bff -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
COPY --from=ui /ui/dist /app/ui
ENV ASPNETCORE_URLS=http://+:19000 \
    Ui__Path=/app/ui \
    DataDirectory=/data
VOLUME /data
EXPOSE 19000
USER $APP_UID
ENTRYPOINT ["dotnet", "Builder.Bff.dll"]
