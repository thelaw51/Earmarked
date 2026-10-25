#!/bin/sh
# Starts one of the two apps in this image. Each runs from its own folder because ASP.NET Core uses the
# working directory as the content root, which is where it finds appsettings.json and wwwroot.
set -e

case "$1" in
    worker) shift; cd /app/worker; exec dotnet PodcastBookRecs.Worker.dll "$@" ;;
    web) shift; cd /app/web; exec dotnet PodcastBookRecs.Web.dll "$@" ;;
    *) echo "Usage: entrypoint.sh worker|web" >&2; exit 64 ;;
esac
