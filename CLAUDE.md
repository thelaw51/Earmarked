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
| 2 | Speech-to-text for episodes without a published transcript, job queue hardening | Next |
| 3 | Book extraction with a local LLM, quote validation, Open Library resolution, ranked book list | Planned |
| 4 | Review queue, filters, export | Planned |

Phase 2 plan: transcription runs natively (not in Docker) on an Apple Silicon Mac on the LAN, behind an OpenAI-compatible `/v1/audio/transcriptions` endpoint. The LLM for Phase 3 (LM Studio) shares that Mac's memory, so expect one heavy job at a time. Treat "backend unreachable" as a reason to back off and retry, not as a job failure that uses up attempts.

Most podcasts publish no transcript, so nearly every episode ends up in `AwaitingTranscription` until Phase 2 lands.

## Layout

| Project | Contents |
|---|---|
| `src/PodcastBookRecs.Core` | EF Core model and migrations (`Data/`), feed parsing (`Feeds/`), transcript parsing (`Transcripts/`), pipeline services (`Pipeline/`), user admin (`Accounts/`), shared config, logging and Identity settings (`Configuration/`) |
| `src/PodcastBookRecs.Worker` | Hosts the pipeline: `DatabaseMigrator` runs first, then `PipelineService` runs four loops (UI commands every 5 s, due feed polls every minute, transcript fetching, heartbeat) |
| `src/PodcastBookRecs.Web` | Blazor Server UI |
| `tests/PodcastBookRecs.Tests` | Unit and integration tests |

The UI and the Worker never call each other. They share only the SQLite database: the UI writes `WorkerCommands` rows that the Worker polls, and the Worker writes `WorkerStatus` heartbeats that the UI reads.

## Pipeline rules

- **Episode states.** `New` (has a usable transcript link) → `TranscriptFetched`, or `AwaitingTranscription` (no usable link, waits for speech-to-text) → `Transcribed`. `Skipped` marks episodes outside the backfill window on a feed's first poll; they are stored so later polls don't treat them as new. `Failed` is reserved.
- **Identity.** Episodes are deduplicated per feed on the item GUID, falling back to the enclosure URL.
- **Late transcripts.** Every poll refreshes the transcript links of episodes in `EpisodeIntake.RefreshableStates` and requeues an `AwaitingTranscription` episode only when its set of link URLs changed. Any new "transcription in progress" state must stay out of `RefreshableStates`, or a poll could requeue an episode mid-job.
- **Retries.** Transient HTTP errors back off exponentially up to `Polling:MaxAttempts`; a dead or unparseable link moves on to the next link, then to `AwaitingTranscription`.

## Data model conventions

- **Keys are version 7 GUIDs**, created in code (`= Guid.CreateVersion7()`), not by the database. They sort by creation time. `WorkerStatus` is the exception: its key is the component name (e.g. `worker`). Don't order by GUID when exact order matters; `CommandProcessor` orders by `CreatedAt`.
- **All `DateTime`s are UTC.** `AppDbContext.ConfigureConventions` marks every `DateTime` read from SQLite as UTC. Convert to local time only for display (`Display.LocalTime`). Don't switch to `DateTimeOffset`: the SQLite provider can't translate its comparisons or ordering to SQL.
- **JSON columns** hold `Episode.TranscriptLinks` and `Transcript.Segments` through value converters. A transcript lives in its own table, one per episode.
- **Names stay provider-neutral.** Stored values and enum names never name a vendor (`TranscriptSource.Transcribed`, not `Whisper`). Record which provider and model produced data in their own columns.

## Migrations

- Only the Worker applies migrations, at startup. The UI's `SchemaGate` waits until none are pending and never creates the database file.
- **The app is deployed, so never regenerate or edit an existing migration.** Add a new one: `dotnet tool restore`, then `dotnet ef migrations add <Name> -p src/PodcastBookRecs.Core -s src/PodcastBookRecs.Core -o Data/Migrations`. Check with `dotnet ef migrations has-pending-model-changes` (same `-p`/`-s`).
- Migrations are marked as generated code in `.editorconfig`, so analysers skip them.

## SQLite on a shared volume

Both containers open one SQLite file in WAL mode with a 30 s busy timeout (`SqlitePragmaInterceptor`). Keep write transactions short. `/data` must be on local disk, never an SMB/NFS share.

## Security and privacy

- **Private feed URLs carry subscriber tokens** in the path or query (Supporting Cast, Patreon). Never log or display one: use `FeedUrlRedactor.Redact` (host only). `System.Net.Http.HttpClient` logging is held at Warning because it logs full request URLs. Episode enclosure URLs from private feeds carry the token too, so the UI never shows them.
- **Feed content is untrusted.** XML is parsed with DTDs ignored and no resolver. Feed and transcript downloads are capped on bytes actually read (`LoadIntoBufferAsync(max)`), not only on `Content-Length`. Only http(s) feed URLs are accepted (`FeedUrl.IsValid`).
- **Open follow-up for Phase 2:** transcript and enclosure URLs come from feeds, so a feed could point the Worker at a LAN address. Block private, loopback and link-local addresses before downloading audio.
- Don't render feed-supplied URLs as `href`s without checking the scheme is http(s).
- **Login.** ASP.NET Core Identity, users only (`IdentityUserContext<AppUser, Guid>`, no roles). A fallback policy requires a signed-in user on every endpoint, the Blazor hub included. Only static assets, `/health`, `/account/login` and `/Error` are anonymous; anything new that must be reachable signed out needs `[AllowAnonymous]` or `.AllowAnonymous()`. Lockout (5 attempts, 15 minutes) and the 12-character, length-only password rule live in `IdentityExtensions`, which the tests share.
- **No sign-up page.** Accounts come from `entrypoint.sh users add|reset-password <email>` (`UserCommands`, backed by `UserAdmin`). Don't add self-registration or a first-run setup page: on an exposed host, whoever reaches it first owns the app.
- Identity's `LockoutEnd` is a `DateTimeOffset`. That is the one exception to the UTC `DateTime` rule; Identity compares it in memory, never in SQL.

## Build and tooling

- `TreatWarningsAsErrors` with `AnalysisLevel` latest-recommended. Use `[LoggerMessage]` source-generated logging, and compute log arguments before the call (CA1873).
- Test names use underscores (CA1707 is off for `tests/` only). Pass `TestContext.Current.CancellationToken` to async calls (xUnit1051).
- Fixtures are short invented files under `tests/PodcastBookRecs.Tests/Fixtures/`, never real transcripts or feeds. Integration tests run against a migrated temp SQLite file with a stub `HttpMessageHandler`.
- `global.json` pins SDK 10.0.402 with `rollForward: feature`. Keep it that way: `actions/setup-dotnet` turns `latestFeature` or `latestPatch` into "newest available", which once pulled an unreleased SDK and broke CI. Bump the version deliberately.
- To try production code against real data without committing it, use a .NET 10 file-based app in a scratch folder (`#:project` pointing at Core, plus `#:property PublishAot=false` if it uses reflection-based JSON).

## Docker image

One image runs either app: `entrypoint.sh worker|web`. Things that broke before and are handled in the Dockerfile:

- `.editorconfig` must be copied into the build, or analyser rules switched off for migrations come back on.
- Publish must restore again after the source is copied: the SDK only adds the package that ships `blazor.web.js` once it sees `.razor` files.
- `entrypoint.sh` changes into each app's folder, because ASP.NET Core uses the working directory as its content root.
- The image publishes for one runtime (from `TARGETARCH`) and runs as UID 1654, which must own the `/data` bind mount. Data Protection keys live in `/data/keys`.

CI pushes `ghcr.io/thelaw51/podcastbookrecs` tagged `latest` and `sha-<short>` from `main`, plus semver tags from `v*` tags. The package is public. Deployment is a manual re-pull in Portainer; no auto-deploy.

## UI

- MudBlazor components, `Typo.*` and `Color.*`, no hard-coded colours. Every page has loading, empty and error states. Load data in `OnInitializedAsync` or `OnParametersSetAsync`.
- Prerendering is off (`App.razor`) so pages load data once and `SchemaGate` can wait without blocking the first response.
- **Account pages render statically.** `/account/login`, `/account/logout` and `/Error` carry `[ExcludeFromInteractiveRouting]` and use `AccountLayout`, because only an HTTP response can set or clear the auth cookie. MudBlazor inputs need a circuit, so the sign-in form uses `EditForm` with plain inputs styled in `app.css`. That is the deliberate exception to the MudBlazor rule. Static pages must never sit inside `SchemaGate`: its polling loop would hold the response open.
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
