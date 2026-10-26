# PodcastBookRecs

[![CI](https://github.com/thelaw51/PodcastBookRecs/actions/workflows/ci.yml/badge.svg)](https://github.com/thelaw51/PodcastBookRecs/actions/workflows/ci.yml)

A self-hosted tracker for books mentioned on podcasts. It watches the feeds you choose, gets a transcript for each episode, pulls out the books people mention, matches them to real books in [Open Library](https://openlibrary.org), and ranks them by how many different shows and episodes bring them up.

It runs as two containers from one image: a **Worker** that polls feeds and processes episodes, and a **Blazor Server UI**. They share a SQLite database on the `/data` volume.

## Status

| Phase | What it adds | State |
|---|---|---|
| 1 | Feed polling, published `<podcast:transcript>` fetching and parsing, podcast and episode pages, CI/CD | **Done** |
| 2 | Speech-to-text transcription (Whisper to start) for episodes without a published transcript, on a [Mac transcription server](#transcription-server-on-a-mac) | **Done** |
| 3 | Book extraction with a local LLM, Open Library matching, ranked book list | **Done** |
| 4 | Review queue, filters, export | Planned |

Most podcasts don't publish transcripts, so most episodes go through speech-to-text.

## How an episode moves through the pipeline

1. The Worker polls each enabled feed every `Polling:IntervalMinutes` (60 by default), sending `If-None-Match`/`If-Modified-Since` so unchanged feeds cost one small request.
2. New items are deduplicated on their GUID, falling back to the enclosure URL.
3. The first time a feed is polled, only the newest `Polling:BackfillEpisodes` (10 by default) are queued. Older ones are stored as *Skipped* so later polls don't mistake them for new episodes.
4. An episode with a `<podcast:transcript>` link is fetched in the best available format: VTT, then SRT, then JSON, then HTML, then plain text. A dead or unparseable link moves on to the next one. Network errors retry with exponential backoff, up to `Polling:MaxAttempts`.
5. Episodes with no usable transcript are transcribed, one at a time, newest first, if `Transcription:Provider` is set (see [Transcription in the Worker](#transcription-in-the-worker)). Shows that have published transcripts before get a grace period first, in case one turns up late.
6. Episodes with a transcript are searched for book mentions, one at a time, newest first, if `Extraction:Provider` is set (see [Book extraction](#book-extraction)). Extraction runs alongside transcription.
7. Each book mention is looked up in Open Library, and mentions of the same book from any episode point at one book (see [Matching books in Open Library](#matching-books-in-open-library)). The *Books* page ranks them.

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

### Two-factor sign-in

Each user can add an authenticator app (1Password, Bitwarden, Aegis, Google Authenticator and so on), so signing in also needs a 6-digit code. Turn it on before exposing the app:

```bash
docker exec -it <web-container> entrypoint.sh users 2fa-enable you@example.com
```

The command prints a key to type into the app, and an `otpauth://` link you can paste into a password manager or turn into a QR code. It then asks for a code from the app, and turns two-factor sign-in on only if that code is right. It finishes by printing 10 recovery codes. Each one signs in once in place of an app code. Keep them somewhere other than your phone, because they aren't shown again.

Wrong codes count towards the same 5-attempt lockout as wrong passwords. Turning two-factor sign-in on or off signs out every existing session within 5 minutes.

The authenticator key and recovery codes are stored in the database on `/data`, next to the keys that sign the login cookie. Treat backups of `/data` as secrets: anyone with a copy can sign in. The terminal you enrolled from also has the key and codes in its scrollback.

For a lost or replaced phone, turn it off and enrol again:

```bash
docker exec -it <web-container> entrypoint.sh users 2fa-disable you@example.com
docker exec -it <web-container> entrypoint.sh users 2fa-enable you@example.com
```

`2fa-enable` refuses while two-factor sign-in is already on. That way a half-finished enrolment can't leave the account needing a key that no app holds.

## Remote access through Cloudflare Tunnel

The app fetches any feed URL you add, so anyone who signs in can make the Worker send requests from inside your network, and can see which private feeds you follow. Turn on [two-factor sign-in](#two-factor-sign-in) first. I'd still put Cloudflare Access in front of it: Access stops password guessing before a request reaches your network.

1. In Cloudflare Zero Trust, add a public hostname to your tunnel that points at `http://<docker-host>:8742`.
2. Under **SSL/TLS**, then **Edge Certificates**, turn on **Always Use HTTPS** and **HSTS**. Always Use HTTPS redirects only after the browser has sent its first plain-HTTP request, cookie and all. HSTS makes the browser use HTTPS from the start.
3. Add a rate-limiting rule for `POST /account/login` and `POST /account/login-2fa`, for example 10 requests a minute per IP. Lockout is per account, so without this anyone who knows your email can keep you locked out. Recovery codes don't count towards lockout, so the rule is also what slows guessing on the second step.
4. Under **Access**, then **Applications**, add a self-hosted application for that hostname, with a policy that allows only your email address.
5. Set `AllowedHosts` in `config.json` to the tunnel's hostname plus every name you use on the LAN, separated by semicolons, for example `"podcasts.example.com;localhost;docker-host.lan"`. The app answers other `Host` headers with 400, so a request can't make it build redirects to another site. Include whatever name your health probe uses; a probe inside the container usually sends `localhost`, so keep that in the list.

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
- **After a reboot:** a launchd agent only runs once you're logged in. If FileVault keeps the Mac at the login screen, transcription waits until someone logs in. The Worker treats an unreachable server as "try again later", not as a failed episode.
- **Test it:** `curl http://<mac-ip>:8178/v1/audio/transcriptions -F file=@some.mp3 -F response_format=verbose_json`

## Transcription in the Worker

With `Transcription:Provider` set, the Worker transcribes episodes that have no usable published transcript:

1. It downloads the episode's audio to `/data/work/<episode>/`.
2. ffmpeg (inside the Worker image) splits it into `Transcription:ChunkMinutes` pieces of 16 kHz mono WAV. Embedded cover art is dropped, and audio past `Transcription:MaxAudioHours` is ignored.
3. Each piece goes to the backend in turn. Finished pieces are saved, so a restart, or the Mac being off, resumes where it stopped.
4. The joined transcript is stored with the provider and model that made it, and the work folder is deleted.

If the backend can't be reached, or the data volume has less than `Transcription:MinFreeDiskGb` free, transcription pauses (30 seconds, doubling to 10 minutes) without counting it against the episode. Errors about one episode, such as audio that won't download or a chunk the backend rejects, use up its `Polling:MaxAttempts`. A 403 or 410 on the audio waits for the next feed poll, since signed links (Patreon, for example) expire and the poll refreshes them.

The Status page shows each backend as reachable or not, checked every 30 seconds. The Worker's own `/health` ignores backends, so a Mac that's switched off doesn't make Docker mark the Worker unhealthy.

### Pointing at a different transcription backend

The Worker talks to OpenAI's `POST /v1/audio/transcriptions` and needs `verbose_json` with timed `segments`. Any server that speaks it works by changing `Transcription:BaseUrl` (and `Model`, if the server cares): whisper.cpp on another machine, an MLX server, or a hosted API. Hosted APIs often cap uploads around 25 MB; 15 minutes of the WAV the Worker sends is about 29 MB, so set `Transcription:ChunkMinutes` to 10 for those. A backend with a different API needs a new adapter under `src/PodcastBookRecs.Core/Providers/Transcription/`, selected by a new `Provider` name.

## Book extraction

With `Extraction:Provider` set, the Worker looks for books in every episode that has a transcript:

1. The transcript is cut into windows of `Extraction:WindowWords` words, each repeating the last `Extraction:OverlapWords` of the one before, so a mention at a boundary isn't cut in half.
2. Each window goes to the language model with the podcast and episode titles (the episode title usually names the guest) and the instructions in [`book-mentions-v1.txt`](src/PodcastBookRecs.Core/Extraction/Prompts/book-mentions-v1.txt). The model answers in JSON, held to a schema: title, author, a short quote, the kind of mention, and a confidence.
3. Small models invent books, so a mention is kept only if its quote is really in the transcript. Matching ignores case and punctuation and allows small slips. The stored quote and timestamp come from the transcript, not the model.
4. Repeats of the same title within an episode are merged, keeping the earliest mention.
5. The mentions are stored with the provider, model and prompt version that produced them. *Re-run book extraction* on the episode page deletes them and extracts again from the stored transcript, without transcribing again.

Some mentions aren't books: a newspaper, a court case, a vague "my second book". Matching in Open Library, next, keeps those off the book list.

Pausing works as for transcription: an unreachable backend, or a model it doesn't have, pauses extraction without counting it against the episode; a failed or timed-out request uses up one of `Polling:MaxAttempts`, after which the episode shows *Book extraction failed* and keeps its transcript.

### LM Studio on the Mac

The default setup runs [LM Studio](https://lmstudio.ai) on the same Mac as the transcription server, with Qwen3 8B: about 5 GB of memory, so it fits next to Whisper on a 16 GB Mac, and roughly 20 to 50 seconds per window on an M4, so an hour-long episode takes about 3 minutes.

1. Download the model: `lms get qwen/qwen3-8b`, or search for it in LM Studio.
2. Start the server on your network: `lms server start --bind 0.0.0.0 --port 1234`, or in LM Studio's Developer tab turn on *Serve on Local Network*.
3. Make it start by itself: in the Developer tab's server settings, turn on starting the server when the app launches, and add LM Studio to your login items.
4. Leave *Just-in-Time model loading* on. The first request loads the model (allowed for by `Extraction:RequestTimeoutMinutes`), and LM Studio unloads it after an idle hour.
5. Keep the default context length at 8,192 tokens or more. A 1,800-word window needs about 4,500 with the instructions and the answer. If you lower it, lower `Extraction:WindowWords` too.

The Status page shows an *extraction* row, which turns red if LM Studio can't be reached or doesn't have the configured model. Requests go over plain HTTP on your LAN, like the transcription server's, so transcript text is visible to anything on that network.

### Pointing at a different language model

The Worker uses OpenAI's `POST /v1/chat/completions` with `response_format` set to a JSON schema. Ollama, llama.cpp's server, vLLM and hosted APIs speak it: change `Extraction:BaseUrl` and `Extraction:Model`. A server that can't hold its answer to a schema will produce more failed requests. A backend with a different API needs a new adapter under `src/PodcastBookRecs.Core/Providers/Extraction/`, selected by a new `Provider` name.

## Matching books in Open Library

The Worker checks each book mention against [Open Library](https://openlibrary.org), a free public catalogue. It's on by default and needs no setup.

1. It searches Open Library's title and author fields for the mention's title, and the author if the model gave one. If that finds nothing, it tries the title alone, because catalogues write authors differently ("J.R.R." against "JRR").
2. A result only counts if its title is the same as the mention's, ignoring case, punctuation, a leading "The" and any subtitle. When the model named an author, the result must also have an author with the same surname. Summaries and study guides are skipped. Of the results left, the one with the most editions wins, so mentions with different spellings land on the same book.
3. A matched mention points at one stored book, shared by every episode that mentions it. Anything without a match stays on its episode page marked *Not found in the catalogue*, and off the book list. Phase 4's review queue is where those get sorted out.
4. A title already looked up isn't asked about again: the next mention with the same title and author surname reuses the answer.

Requests go out at most one a second, Open Library's limit for anonymous clients, so a few hundred mentions take a few minutes. Set `OpenLibrary:ContactEmail` and it goes in the User-Agent, as Open Library asks of regular clients. If Open Library is down or asks the Worker to slow down, matching pauses and tries again later; the Status page has a *book lookup* row.

Matching sends book titles and author names, nothing else, to Open Library over HTTPS. If you'd rather not, set `BookLookup:Provider` to an empty string; mentions then stay on their episode pages and the book list stays empty.

### The book list

The *Books* page ranks every matched book by the number of different podcasts that mentioned it, then the number of episodes, then the most recent mention. The kind of mention (recommended, discussed and so on) doesn't change the order, because the model labels kinds unreliably; the list shows how many episodes recommended each book instead. Each book has its own page with every mention: podcast, episode, time and quote.

### Pointing at a different catalogue

The matching rules live in `CatalogMatcher` and know nothing about Open Library. Another catalogue (Google Books, say) needs a new adapter under `src/PodcastBookRecs.Core/Providers/BookLookup/` that implements `IBookCatalog`, and a new `BookLookup:Provider` name. Each stored book records the catalogue it came from, and each match the version of the rules (`CatalogMatcher.Version`) that made it; changing the rules and bumping the version re-matches every mention.

## Configuration

Settings come from `/config/config.json`, then environment variables prefixed `PBR_` override them. Nested keys use a double underscore: `PBR_Polling__IntervalMinutes=30`.

| Key | Default | Meaning |
|---|---|---|
| `Polling:IntervalMinutes` | `60` | How often each feed is checked |
| `Polling:BackfillEpisodes` | `10` | Episodes queued when a feed is first added |
| `Polling:MaxAttempts` | `5` | Transient failures before an episode stops retrying a published transcript |
| `Feeds` | `[]` | Feeds imported on Worker startup if missing. Feeds added in the UI live in the database. |
| `BookLookup:Provider` | `OpenLibrary` | The book catalogue mentions are matched against; empty turns matching off |
| `OpenLibrary:ContactEmail` | | Sent in the User-Agent so Open Library can get in touch about heavy use. Optional. |
| `AllowedHosts` | `*` | Host names the web app answers, separated by semicolons. Anything else gets 400. Set it before exposing the app; see Cloudflare Tunnel above. |
| `ForwardedHeaders:KnownNetworks` | private ranges | CIDR blocks allowed to send `X-Forwarded-Proto` and `X-Forwarded-For`, e.g. `["172.18.0.0/16"]`. Loopback is always trusted. |
| `Paths:Data` | `/data` | Database, key and work-folder storage. Set by the image; you shouldn't need to change it. |
| `Transcription:Provider` | | `OpenAiCompatible`, or empty to turn transcription off |
| `Transcription:BaseUrl` | | The backend, e.g. `http://<mac-ip>:8178` |
| `Transcription:Model` | `whisper` | Sent with each request and recorded on each transcript |
| `Transcription:Language` | `en` | Spoken language; empty lets the backend detect it |
| `Transcription:ChunkMinutes` | `15` | Length of each piece sent to the backend |
| `Transcription:RequestTimeoutMinutes` | `10` | How long one piece may take before it counts as a failure |
| `Transcription:PublishedTranscriptGraceDays` | `3` | How long new episodes of shows that publish transcripts wait for one |
| `Transcription:MaxAudioHours` | `8` | Audio past this is ignored; also caps the work folder's size |
| `Transcription:FfmpegTimeoutMinutes` | `30` | ffmpeg is stopped if splitting takes longer |
| `Transcription:MinFreeDiskGb` | `3` | Transcription pauses below this much free space on `/data` |
| `Extraction:Provider` | | `OpenAiCompatible`, or empty to turn book extraction off |
| `Extraction:BaseUrl` | | The backend, e.g. `http://<mac-ip>:1234` |
| `Extraction:Model` | `qwen/qwen3-8b` | The model name the backend knows; recorded on each mention |
| `Extraction:WindowWords` | `1800` | Words of transcript per request |
| `Extraction:OverlapWords` | `200` | Words each window repeats from the one before |
| `Extraction:RequestTimeoutMinutes` | `10` | How long one request may take, including loading the model, before it counts as a failure |
| `Http:AllowPrivateNetworkFeeds` | `false` | Feeds, transcripts and audio on private or local addresses are refused unless this is `true` |

Private feed URLs (for example a Supporting Cast subscriber feed) contain an access token. The app stores them as entered, but logs and the UI only ever show the host. Their audio URLs often carry the token too, so those are never shown either.

Feed content is untrusted. URLs taken from feeds (the feed itself, transcript links, episode audio) may not reach private, loopback or link-local addresses, so a feed can't point the Worker at devices on your network. ffmpeg only reads the downloaded file, in known audio formats, so a crafted file can't make it fetch anything else.

Endpoints: both containers answer `GET /health` on port 8080 inside the container.

## Development

Requires the .NET 10 SDK.

```bash
dotnet test
```

The ffmpeg tests need `ffmpeg` on your `PATH` (`brew install ffmpeg`). They're skipped locally without it, but fail on CI, which installs it.

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
| `src/PodcastBookRecs.Core` | EF Core model and migrations, feed and transcript parsing, pipeline services, the transcription and extraction interfaces and their providers, the extraction prompt, ffmpeg chunking |
| `src/PodcastBookRecs.Worker` | Background loops: commands from the UI, feed polling, transcript fetching, transcription, book extraction, heartbeat, backend status |
| `src/PodcastBookRecs.Web` | Blazor Server UI built with MudBlazor |
| `tests/PodcastBookRecs.Tests` | xUnit v3 tests with small invented fixtures |

The UI asks the Worker to do things ("poll now", "retry episode", "re-run book extraction") by inserting rows into a `WorkerCommands` table, which the Worker checks every 5 seconds.

## Licence

[MIT](LICENSE)
