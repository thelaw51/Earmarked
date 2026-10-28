#!/bin/sh
# Starts one of the two apps in this image. Each runs from its own folder because ASP.NET Core uses the
# working directory as the content root, which is where it finds appsettings.json and wwwroot.
set -e

case "$1" in
    worker) shift; cd /app/worker; exec dotnet Earmarked.Worker.dll "$@" ;;
    web) shift; cd /app/web; exec dotnet Earmarked.Web.dll "$@" ;;
    # Account management, run inside the web container: docker exec -it <container> entrypoint.sh users add you@example.com
    users) cd /app/web; exec dotnet Earmarked.Web.dll "$@" ;;
    *) echo "Usage: entrypoint.sh worker|web|users" >&2; exit 64 ;;
esac
