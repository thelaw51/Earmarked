# PodcastBookRecs

Self-hosted tracker for books mentioned on podcasts. .NET 10, Blazor Server with MudBlazor, EF Core on SQLite. A Worker and a UI ship as one Docker image and share `/data`. See [README.md](README.md) for the pipeline, deployment and configuration.

This repo is **public**. Never commit hostnames, IP addresses, email addresses, real feed URLs, real transcripts or a real `config.json`. Use placeholders.

```bash
dotnet test                                    # build and run all tests (xUnit v3 on Microsoft.Testing.Platform)
dotnet run --project src/PodcastBookRecs.Worker
dotnet run --project src/PodcastBookRecs.Web   # http://localhost:5237
```

Local runs read `.local/config/config.json` and write `.local/data/` (both git-ignored), set by each project's `launchSettings.json`.

## Status and roadmap

| Phase | Scope | State |
|---|---|---|
| 1 | Feed polling, published `<podcast:transcript>` fetch and parse, podcast/episode/status UI, CI/CD to GHCR | Done and deployed |
| 2 | Speech-to-text for episodes without a published transcript (whisper.cpp on a Mac, `mac/`), chunked and resumable | Done |
| 3 | Book extraction with a local LLM, quote validation, Open Library resolution, ranked book list | Next |
| 4 | Review queue, filters, export | Planned |

Transcription runs natively (not in Docker) on an Apple Silicon Mac on the LAN, behind an OpenAI-compatible `/v1/audio/transcriptions` endpoint; `mac/setup.sh` installs it. Phase 3's LLM (LM Studio) shares that Mac's memory, so expect one heavy job at a time, and reuse the Phase 2 patterns: our own interface, an adapter under `Core/Providers/`, `BackendUnavailableException` for "can't reach it", and a health check tagged `BackendHealth.Tag` so it shows on the Status page.

Most podcasts publish no transcript, so most episodes go through speech-to-text.

## Layout

| Project | Contents |
|---|---|
| `src/PodcastBookRecs.Core` | EF Core model and migrations (`Data/`), feed parsing (`Feeds/`), transcript parsing (`Transcripts/`), pipeline services (`Pipeline/`), shared config and logging (`Configuration/`), backend contracts (`Backends/`), the transcription interface (`Transcription/`) and its adapters (`Providers/`), ffmpeg chunking (`Audio/`), the private-address guard (`Net/`) |
| `src/PodcastBookRecs.Worker` | Hosts the pipeline: `DatabaseMigrator` runs first, then `PipelineService` runs its loops (UI commands every 5 s, due feed polls every minute, transcript fetching, heartbeat, and transcription when a provider is set). `BackendStatusPublisher` writes backend health rows every 30 s. |
| `src/PodcastBookRecs.Web` | Blazor Server UI |
| `tests/PodcastBookRecs.Tests` | Unit and integration tests |

The UI and the Worker never call each other. They share only the SQLite database: the UI writes `WorkerCommands` rows that the Worker polls, and the Worker writes `WorkerStatus` heartbeats that the UI reads.

## Pipeline rules

- **Episode states.** `New` (has a usable transcript link) → `TranscriptFetched`, or `AwaitingTranscription` (no usable link) → `Transcribing` (claimed by `EpisodeTranscriber`) → `Transcribed`. `Skipped` marks episodes outside the backfill window on a feed's first poll; they are stored so later polls don't treat them as new. `Failed` is used when transcription runs out of attempts.
- **Identity.** Episodes are deduplicated per feed on the item GUID, falling back to the enclosure URL.
- **Late transcripts.** Every poll refreshes the transcript links of episodes in `EpisodeIntake.RefreshableStates` and requeues an `AwaitingTranscription` episode only when its set of link URLs changed. `Transcribing` must stay out of `RefreshableStates`, or a poll could requeue an episode mid-job. Feeds that have ever carried transcript links get `PodcastFeed.PublishesTranscripts`, and their new episodes wait `Transcription:PublishedTranscriptGraceDays` before transcription.
- **Episode state changes go through `EpisodeStateLock`.** The poller's refresh, the transcriber's claim and completion, and "Reset and retry" all hold it, and the claim is a conditional `ExecuteUpdate`. Any new code that changes an episode's state in the Worker must take the same lock.
- **Transcription.** `EpisodeTranscriber` downloads audio, splits it with ffmpeg into WAV chunks, and saves each finished chunk as `chunk_NNN.json` in `/data/work/<episode>/`, so it resumes after a restart. The poller refreshes `EnclosureUrl` for waiting and transcribing episodes, because signed links expire.
- **Retries.** Transient HTTP errors back off exponentially up to `Polling:MaxAttempts`; a dead or unparseable link moves on to the next link, then to `AwaitingTranscription`. For transcription, `BackendUnavailableException` (connection refused, DNS, connect timeout, HTTP 502/503/504) and low disk pause without counting an attempt; everything about the episode itself (download errors, ffmpeg errors, rejected or timed-out chunks) counts one.
- **Plain-text transcripts** get speakers from `Name:` paragraph prefixes when at least half the paragraphs have one (`TranscriptParser`).

## Data model conventions

- **Keys are version 7 GUIDs**, created in code (`= Guid.CreateVersion7()`), not by the database. They sort by creation time. `WorkerStatus` is the exception: its key is the component name (e.g. `worker`). Don't order by GUID when exact order matters; `CommandProcessor` orders by `CreatedAt`.
- **All `DateTime`s are UTC.** `AppDbContext.ConfigureConventions` marks every `DateTime` read from SQLite as UTC. Convert to local time only for display (`Display.LocalTime`). Don't switch to `DateTimeOffset`: the SQLite provider can't translate its comparisons or ordering to SQL.
- **JSON columns** hold `Episode.TranscriptLinks` and `Transcript.Segments` through value converters. A transcript lives in its own table, one per episode.
- **Names stay provider-neutral.** Stored values and enum names never name a vendor (`TranscriptSource.Transcribed`, not `Whisper`). Record which provider and model produced data in their own columns (`Transcript.Provider`, `Transcript.Model`).

## Migrations

- Only the Worker applies migrations, at startup. The UI's `SchemaGate` waits until none are pending and never creates the database file.
- **The app is deployed, so never regenerate or edit an existing migration.** Add a new one: `dotnet tool restore`, then `dotnet ef migrations add <Name> -p src/PodcastBookRecs.Core -s src/PodcastBookRecs.Core -o Data/Migrations`. Check with `dotnet ef migrations has-pending-model-changes` (same `-p`/`-s`).
- Migrations are marked as generated code in `.editorconfig`, so analysers skip them.

## SQLite on a shared volume

Both containers open one SQLite file in WAL mode with a 30 s busy timeout (`SqlitePragmaInterceptor`). Keep write transactions short. `/data` must be on local disk, never an SMB/NFS share.

## Security and privacy

- **Private feed URLs carry subscriber tokens** in the path or query (Supporting Cast, Patreon). Never log or display one: use `FeedUrlRedactor.Redact` (host only). `System.Net.Http.HttpClient` logging is held at Warning because it logs full request URLs. Episode enclosure URLs from private feeds carry the token too, so the UI never shows them.
- **Feed content is untrusted.** XML is parsed with DTDs ignored and no resolver. Feed and transcript downloads are capped on bytes actually read (`LoadIntoBufferAsync(max)`), not only on `Content-Length`; audio is capped while streaming. Only http(s) feed URLs are accepted (`FeedUrl.IsValid`).
- **Private-address guard.** The `feeds` and `enclosures` HTTP clients connect through `OutboundConnections.ConnectToPublicAddressAsync`: it resolves once, refuses any non-public address (`PrivateAddressGuard`), connects to the vetted IP, and needs `UseProxy = false`. Any new client for feed-supplied URLs must use `ServiceCollectionExtensions.CreateFeedHandler`. User-configured backends (the transcription client) are exempt. `Http:AllowPrivateNetworkFeeds` turns the guard off.
- **ffmpeg runs on untrusted files** with `-protocol_whitelist file` and `-format_whitelist` (no playlists or concat, which would let a file make ffmpeg open other inputs), a duration cap, a wall-clock timeout, `ArgumentList` arguments and bounded stderr. Keep all of these if you change the command. ffmpeg's stderr goes to logs, never to `LastError`.
- Don't render feed-supplied URLs as `href`s without checking the scheme is http(s).
- The app has no login. Remote access must go through Cloudflare Access (documented in the README).

## Build and tooling

- `TreatWarningsAsErrors` with `AnalysisLevel` latest-recommended. Use `[LoggerMessage]` source-generated logging, and compute log arguments before the call (CA1873).
- Test names use underscores (CA1707 is off for `tests/` only). Pass `TestContext.Current.CancellationToken` to async calls (xUnit1051).
- The ffmpeg tests run the real binary: skipped locally without ffmpeg, failing on CI (`CI=true`), which installs it in the "Build and test" job.
- `HttpClient` throws the same `TaskCanceledException` for a connect timeout and a request timeout. The transcription client does its own connecting (`OutboundConnections.ConnectWithTimeout`) so "can't connect" always surfaces as `HttpRequestError.ConnectionError`.
- Fixtures are short invented files under `tests/PodcastBookRecs.Tests/Fixtures/`, never real transcripts or feeds. Integration tests run against a migrated temp SQLite file with a stub `HttpMessageHandler`.
- `global.json` pins SDK 10.0.402 with `rollForward: feature`. Keep it that way: `actions/setup-dotnet` turns `latestFeature` or `latestPatch` into "newest available", which once pulled an unreleased SDK and broke CI. Bump the version deliberately.
- To try production code against real data without committing it, use a .NET 10 file-based app in a scratch folder (`#:project` pointing at Core, plus `#:property PublishAot=false` if it uses reflection-based JSON).

## Docker image

One image runs either app: `entrypoint.sh worker|web`. Things that broke before and are handled in the Dockerfile:

- `.editorconfig` must be copied into the build, or analyser rules switched off for migrations come back on.
- Publish must restore again after the source is copied: the SDK only adds the package that ships `blazor.web.js` once it sees `.razor` files.
- `entrypoint.sh` changes into each app's folder, because ASP.NET Core uses the working directory as its content root.
- The image publishes for one runtime (from `TARGETARCH`) and runs as UID 1654, which must own the `/data` bind mount. Data Protection keys live in `/data/keys`; transcription scratch files in `/data/work`.
- ffmpeg comes from Ubuntu's apt package (6.1 at the time of writing). It roughly doubles the image size; a static build would be smaller but adds a third-party supply-chain dependency.

CI pushes `ghcr.io/thelaw51/podcastbookrecs` tagged `latest` and `sha-<short>` from `main`, plus semver tags from `v*` tags. The package is public. Deployment is a manual re-pull in Portainer; no auto-deploy.

## UI

- MudBlazor components, `Typo.*` and `Color.*`, no hard-coded colours. Every page has loading, empty and error states. Load data in `OnInitializedAsync` or `OnParametersSetAsync`.
- Prerendering is off (`App.razor`) so pages load data once and `SchemaGate` can wait without blocking the first response.
- User-facing text says "podcast"; the entity is `PodcastFeed`. Routes take GUIDs: `/podcasts/{Id:guid}`, `/episodes/{Id:guid}`.
- UI actions never touch the pipeline directly: they insert a `WorkerCommand`.

## Third-party services must be swappable

This is a standing requirement for all work on this project. Every external service is used through our own interface, so moving to a different provider means writing one new adapter and changing config, not editing the pipeline or the UI.

That covers today's and planned services: Whisper (transcription), LM Studio (book extraction), Open Library (book lookup), Audiobookshelf (local audio files), and anything added later. Feed polling is exempt because RSS and Podcasting 2.0 are open standards, not providers.

### Rules

- **Interface first.** Core defines an interface named for the job, not the vendor: `ITranscriptionService`, not `IWhisperClient`. Pipeline services depend only on that interface.
- **One adapter per provider.** Each implementation lives in its own folder, e.g. `Core/Providers/Transcription/OpenAiCompatible/`. Nothing outside that folder references the provider's types, SDK or wire format.
- **Our types at the boundary.** Adapters return the project's own records (such as `TranscriptSegment`), never provider DTOs. Provider-specific errors become our own exceptions, e.g. one meaning "backend unavailable" that the pipeline handles the same way for every provider.
- **Chosen by config.** A `Provider` key in each service's config section picks the implementation, alongside its base URL, model name and credentials. Switching provider must not need a code change or a new image.
- **Prefer standard protocols over vendor SDKs.** An OpenAI-compatible endpoint lets one adapter serve whisper.cpp, MLX servers, LM Studio, Ollama and hosted APIs alike. Only add a vendor SDK when no standard protocol does the job, and keep it inside the adapter.
- **Record provenance.** Store which provider and model produced each piece of derived data (transcripts, mentions, book matches), so results from different backends can be told apart and re-run selectively.
- **Same status contract.** Every external backend reports reachability through one shared interface, which the Status page shows. Adding a provider adds a row without changing the UI.
- **Tests against the interface.** Pipeline tests use fakes of the interface, never a real provider. Each adapter gets its own tests, using small recorded response fixtures.
- **Document the switch.** The README says how to point each service at a different provider.

When a change touches a third-party service, check it against these rules before calling it done.

## Branch protection and CI job names

`main` is protected by the "Protect main" ruleset: no direct pushes, force pushes or deletion, and every change goes through a pull request. A PR can only merge when the required checks named "Build and test" and "Docker image" pass.

Those names are the `name:` of the two jobs in `.github/workflows/ci.yml`, and the ruleset matches them exactly. If you rename either job, or split or replace it, update the ruleset's required checks in the same change (Settings → Rules → Rulesets → Protect main). Otherwise every PR waits forever for a check that never reports.
