# One image holds both apps. Compose picks which one runs: `command: ["worker"]` or `command: ["web"]`.
# Shipping them together keeps the UI and the Worker (which owns migrations) on the same schema version.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Publish for the target platform only, so the image doesn't carry native SQLite builds for every OS and CPU.
ARG TARGETARCH
RUN echo "linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64)" > /tmp/rid

# Restore first, from project files only, so the package layer is cached until a dependency changes.
# Publish below restores again on purpose: the SDK only adds the package holding blazor.web.js once it can
# see the .razor files, and those aren't copied yet at this point.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/PodcastBookRecs.Core/PodcastBookRecs.Core.csproj src/PodcastBookRecs.Core/
COPY src/PodcastBookRecs.Worker/PodcastBookRecs.Worker.csproj src/PodcastBookRecs.Worker/
COPY src/PodcastBookRecs.Web/PodcastBookRecs.Web.csproj src/PodcastBookRecs.Web/
RUN dotnet restore src/PodcastBookRecs.Worker -r $(cat /tmp/rid) && dotnet restore src/PodcastBookRecs.Web -r $(cat /tmp/rid)

COPY src/ src/

# VERSION is only passed for v* tag builds; other builds keep the version in Directory.Build.props.
ARG VERSION=
ARG SOURCE_REVISION=
RUN dotnet publish src/PodcastBookRecs.Worker -c Release -r $(cat /tmp/rid) --self-contained false -o /app/worker \
        ${VERSION:+-p:Version=$VERSION} -p:SourceRevisionId=$SOURCE_REVISION \
 && dotnet publish src/PodcastBookRecs.Web -c Release -r $(cat /tmp/rid) --self-contained false -o /app/web \
        ${VERSION:+-p:Version=$VERSION} -p:SourceRevisionId=$SOURCE_REVISION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --chmod=755 docker/entrypoint.sh /usr/local/bin/entrypoint.sh

# The image's non-root user is UID 1654. Bind-mounted /data must be writable by that UID (see README).
RUN mkdir -p /data /config && chown "$APP_UID:$APP_UID" /data /config
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080 \
    PBR_CONFIG_DIR=/config \
    PBR_Paths__Data=/data
EXPOSE 8080

ENTRYPOINT ["entrypoint.sh"]
CMD ["web"]
