# PodcastBookRecs

[![CI](https://github.com/thelaw51/PodcastBookRecs/actions/workflows/ci.yml/badge.svg)](https://github.com/thelaw51/PodcastBookRecs/actions/workflows/ci.yml)

A self-hosted tracker for books mentioned on podcasts. It watches the feeds you choose, gets a transcript for each episode, and (from Phase 3) pulls out the books people mention and ranks them by how many different shows and episodes bring them up.

It runs as two containers from one image: a **Worker** that polls feeds and processes episodes, and a **Blazor Server UI**. They share a SQLite database on the `/data` volume.

## Status

| Phase | What it adds | State |
|---|---|---|
| 1 | Feed polling, published `<podcast:transcript>` fetching and parsing, podcast and episode pages, CI/CD | **Done** |
| 2 | Speech-to-text transcription (Whisper to start) for episodes without a published transcript | In progress: the [Mac transcription server](#transcription-server-on-a-mac) is ready; the Worker doesn't use it yet |
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

## Signing in

Every page needs a signed-in user. There is no sign-up page and no "forgot password" email. You create accounts from a shell inside the web container, after the Worker has created the database:

```bash
docker exec -it <web-container> entrypoint.sh users add you@example.com
```

If you're upgrading from a build without login, nobody can sign in until you run this. Update the stack, wait for the Worker to log that it applied its migrations, then add yourself.

The command prompts twice for the password, without echoing it. Passwords need at least 12 characters and nothing else.

Five wrong passwords in a row lock the account for 15 minutes. To unlock it early, or if you forget the password, set a new one:

```bash
docker exec -it <web-container> entrypoint.sh users reset-password you@example.com
```

That also clears the failed-attempt count. Every existing sign-in, open tabs included, is sent back to the sign-in page within 5 minutes.

"Keep me signed in" keeps the cookie for 14 days, renewed as you use the app. Without it the cookie ends when the browser closes.

## Remote access through Cloudflare Tunnel

The app fetches any feed URL you add, so anyone who signs in can make the Worker send requests from inside your network, and can see which private feeds you follow. The login has no second factor, so I'd still put Cloudflare Access in front of it. Access stops password guessing before a request reaches your network.

1. In Cloudflare Zero Trust, add a public hostname to your tunnel that points at `http://<docker-host>:8742`.
2. Under **SSL/TLS**, then **Edge Certificates**, turn on **Always Use HTTPS** and **HSTS**. Always Use HTTPS redirects only after the browser has sent its first plain-HTTP request, cookie and all. HSTS makes the browser use HTTPS from the start.
3. Add a rate-limiting rule for `POST /account/login`, for example 10 requests a minute per IP. Lockout is per account, so without this anyone who knows your email can keep you locked out.
4. Under **Access**, then **Applications**, add a self-hosted application for that hostname, with a policy that allows only your email address.

cloudflared talks plain HTTP to the app and sends `X-Forwarded-Proto: https`. The app trusts that header from private addresses (`ForwardedHeaders:KnownNetworks`), which is how it knows to mark the sign-in cookie Secure and to redirect to `https://`. If cloudflared reaches the app from a public address, add that address to the list. To check, sign in through the tunnel and look at the `pbr.auth` cookie in the browser's developer tools: it should say Secure.

Blazor Server keeps a WebSocket open to the server. cloudflared passes WebSockets through without any extra setting.

## Transcription server on a Mac

Episodes without a published transcript are transcribed by [whisper.cpp](https://github.com/ggml-org/whisper.cpp)'s server, running natively on an Apple Silicon Mac on your LAN. It has to run natively: Docker on macOS can't use the Apple GPU, so a containerised Whisper would fall back to the much slower CPU.

### Why whisper.cpp

- It runs on the Apple GPU through Metal, and Homebrew ships a prebuilt version.
- Its server can answer on the OpenAI path `/v1/audio/transcriptions`. The Worker talks to that standard API, so any OpenAI-compatible transcription server can replace it.
- Its `verbose_json` response includes timed segments, which book mentions need in order to link to a point in the episode. Some alternatives (for example vllm-metal) return only the text.

The default model is `large-v3-turbo`: close to `large-v3` accuracy at several times the speed, and about 1.8 GB of memory, which leaves room for LM Studio's model on a 16 GB Mac. On an M4 it transcribed clear speech at about 15× real time. Real podcasts, with music and crosstalk, run slower.

### Setup

On the Mac, with [Homebrew](https://brew.sh) installed:

```bash
mac/setup.sh --host <the Mac's LAN IP>
```

The script:

1. installs `whisper-cpp` and `ffmpeg` with Homebrew, if they aren't already installed
2. downloads the model to `~/Library/Application Support/whisper-server/models/` and checks its SHA-256 against the published hash
3. installs a launchd agent, `com.podcastbookrecs.whisper-server`, that starts the server at login and restarts it if it stops
4. waits until the server answers, then prints its address

It's safe to run again: it skips a model that's already verified and reinstalls the agent with the current settings. Options: `--port` (default `8178`) and `--model` (`large-v3-turbo`, or the smaller `large-v3-turbo-q8_0` and `large-v3-turbo-q5_0`).

Give the Mac a reserved IP address on your router, since the Worker's config points at it. The server listens on that address only. It has no authentication, so don't forward its port beyond your LAN.

### Living with it

- **Logs:** `~/Library/Logs/whisper-server.log`
- **Restart:** `launchctl kickstart -k gui/$(id -u)/com.podcastbookrecs.whisper-server`. Do this after `brew upgrade whisper-cpp` too.
- **Stop and remove:** `launchctl bootout gui/$(id -u)/com.podcastbookrecs.whisper-server`, then delete `~/Library/LaunchAgents/com.podcastbookrecs.whisper-server.plist`
- **After a reboot:** a launchd agent only runs once you're logged in. If FileVault keeps the Mac at the login screen, transcription waits until someone logs in. Once the Worker transcribes (the next part of Phase 2), it treats an unreachable server as "try again later", not as a failed episode.
- **Test it:** `curl http://<mac-ip>:8178/v1/audio/transcriptions -F file=@some.mp3 -F response_format=verbose_json`

## Configuration

Settings come from `/config/config.json`, then environment variables prefixed `PBR_` override them. Nested keys use a double underscore: `PBR_Polling__IntervalMinutes=30`.

| Key | Default | Meaning |
|---|---|---|
| `Polling:IntervalMinutes` | `60` | How often each feed is checked |
| `Polling:BackfillEpisodes` | `10` | Episodes queued when a feed is first added |
| `Polling:MaxAttempts` | `5` | Transient failures before an episode stops retrying a published transcript |
| `Feeds` | `[]` | Feeds imported on Worker startup if missing. Feeds added in the UI live in the database. |
| `OpenLibrary:ContactEmail` | | Sent to Open Library from Phase 3, which gives higher rate limits to identified clients |
| `ForwardedHeaders:KnownNetworks` | private ranges | CIDR blocks allowed to send `X-Forwarded-Proto` and `X-Forwarded-For`, e.g. `["172.18.0.0/16"]`. Loopback is always trusted. |
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

The UI is at http://localhost:5237. The Worker creates the database at `.local/data/podcastbookrecs.db`. Once it exists, add a user:

```bash
dotnet run --project src/PodcastBookRecs.Web -- users add you@example.com
```

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
