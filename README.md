# PodcastBookRecs

[![CI](https://github.com/thelaw51/PodcastBookRecs/actions/workflows/ci.yml/badge.svg)](https://github.com/thelaw51/PodcastBookRecs/actions/workflows/ci.yml)

A self-hosted tracker for books mentioned on podcasts. It watches the feeds you choose, gets a transcript for each episode, and (from Phase 3) pulls out the books people mention and ranks them by how many different shows and episodes bring them up.

It runs as two containers from one image: a **Worker** that polls feeds and processes episodes, and a **Blazor Server UI**. They share a SQLite database on the `/data` volume.

## Status

| Phase | What it adds | State |
|---|---|---|
| 1 | Feed polling, published `<podcast:transcript>` fetching and parsing, podcast and episode pages, CI/CD | **Done** |
| 2 | Speech-to-text transcription (Whisper to start) for episodes without a published transcript | Next |
| 3 | Book extraction with a local LLM, Open Library matching, ranked book list | Planned |
| 4 | Review queue, filters, export | Planned |

Most podcasts don't publish transcripts, so until Phase 2 lands those episodes sit in the *Awaiting transcription* state.

## How an episode moves through the pipeline

1. The Worker polls each enabled feed every `Polling:IntervalMinutes` (60 by default), sending `If-None-Match`/`If-Modified-Since` so unchanged feeds cost one small request.
2. New items are deduplicated on their GUID, falling back to the enclosure URL.
3. The first time a feed is polled, only the newest `Polling:BackfillEpisodes` (10 by default) are queued. Older ones are stored as *Skipped* so later polls don't mistake them for new episodes.
4. An episode with a `<podcast:transcript>` link is fetched in the best available format: VTT, then SRT, then JSON, then HTML, then plain text. A dead or unparseable link moves on to the next one. Network errors retry with exponential backoff, up to `Polling:MaxAttempts`.
5. Episodes with no usable transcript wait for the transcription service.

## Deploying with Portainer

The image is public at `ghcr.io/thelaw51/podcastbookrecs`, so Portainer can pull it without logging in.

### 1. Prepare the folders on the Docker host

```bash
sudo mkdir -p /path/to/podcastbookrecs/config /path/to/podcastbookrecs/data
sudo chown -R 1654:1654 /path/to/podcastbookrecs/data
```

The container runs as the .NET image's non-root `app` user, UID 1654, so it needs to own `data`. Do this before the first deploy: if the folder doesn't exist, Docker creates it owned by root and the Worker can't write the database. Put `data` on the host's local disk, not a NAS share. SQLite locking isn't reliable over SMB or NFS.

Copy [`config.example.json`](config.example.json) to `/path/to/podcastbookrecs/config/config.json` and edit it. The container mounts `config` read-only.

### 2. Create the stack

Paste [`docker-compose.yml`](docker-compose.yml) into a new Portainer stack and replace the `/path/to/...` host paths. Two stack environment variables are optional:

| Variable | Default | Meaning |
|---|---|---|
| `PBR_PORT` | `8742` | Host port for the UI |
| `PBR_TAG` | `latest` | Image tag both containers run |

The UI is then at `http://<docker-host>:8742`.

### Updating and rolling back

Nothing updates automatically. To update:

1. Back up the `data` folder. A new version may migrate the database, and migrations only go forwards.
2. Open the stack in Portainer, tick **Re-pull image**, and click **Update the stack**. Always update the whole stack, not one container: the UI assumes both containers run the same build.

Every CI build is also tagged `sha-<first 7 characters of the commit>`. To roll back, set `PBR_TAG` to an earlier build's tag (for example `sha-1a2b3c4`) and update the stack again. The tags are listed on the package page on GitHub. Both containers read the same variable, so they never run different versions.

If the version you're leaving had added a migration, an older build won't understand the newer database. Stop the stack, restore the `data` backup you took before updating, then start the older tag.

Version tags (`v1.2.0`) also publish `1.2.0` and `1.2`.

## Remote access through Cloudflare Tunnel

The app has no login of its own. It also fetches any feed URL you add, so anyone who can reach the UI can make the Worker send requests from inside your network, and can see which private feeds you follow. **Only expose it behind Cloudflare Access.**

1. In Cloudflare Zero Trust, add a public hostname to your tunnel that points at `http://<docker-host>:8742`.
2. Under **Access**, then **Applications**, add a self-hosted application for that hostname, with a policy that allows only your email address.

Blazor Server keeps a WebSocket open to the server. cloudflared passes WebSockets through without any extra setting.

## Configuration

Settings come from `/config/config.json`, then environment variables prefixed `PBR_` override them. Nested keys use a double underscore: `PBR_Polling__IntervalMinutes=30`.

| Key | Default | Meaning |
|---|---|---|
| `Polling:IntervalMinutes` | `60` | How often each feed is checked |
| `Polling:BackfillEpisodes` | `10` | Episodes queued when a feed is first added |
| `Polling:MaxAttempts` | `5` | Transient failures before an episode stops retrying a published transcript |
| `Feeds` | `[]` | Feeds imported on Worker startup if missing. Feeds added in the UI live in the database. |
| `OpenLibrary:ContactEmail` | | Sent to Open Library from Phase 3, which gives higher rate limits to identified clients |
| `Paths:Data` | `/data` | Database and key storage. Set by the image; you shouldn't need to change it. |

Private feed URLs (for example a Supporting Cast subscriber feed) contain an access token. The app stores them as entered, but logs and the UI only ever show the host.

Endpoints: both containers answer `GET /health` on port 8080 inside the container.

## Development

Requires the .NET 10 SDK.

```bash
dotnet test
```

To run locally, put a config file at `.local/config/config.json` (git ignores `.local/`), then start each app in its own terminal:

```bash
dotnet run --project src/PodcastBookRecs.Worker
```

```bash
dotnet run --project src/PodcastBookRecs.Web
```

The UI is at http://localhost:5237. The database is created at `.local/data/podcastbookrecs.db`.

To add a migration after changing the model:

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/PodcastBookRecs.Core -s src/PodcastBookRecs.Core -o Data/Migrations
```

Only the Worker applies migrations, at startup. The UI shows a waiting message until the database is current.

### Project layout

| Project | Contents |
|---|---|
| `src/PodcastBookRecs.Core` | EF Core model and migrations, feed and transcript parsing, pipeline services |
| `src/PodcastBookRecs.Worker` | Background loops: commands from the UI, feed polling, transcript fetching, heartbeat |
| `src/PodcastBookRecs.Web` | Blazor Server UI built with MudBlazor |
| `tests/PodcastBookRecs.Tests` | xUnit v3 tests with small invented fixtures |

The UI asks the Worker to do things ("poll now", "retry episode") by inserting rows into a `WorkerCommands` table, which the Worker checks every 5 seconds.

## Licence

[MIT](LICENSE)
