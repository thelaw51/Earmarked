# PodcastBookRecs

Self-hosted tracker for books mentioned on podcasts. .NET 10, Blazor Server with MudBlazor, EF Core on SQLite. A Worker and a UI ship as one Docker image and share `/data`. See [README.md](README.md) for the pipeline, deployment and configuration.

```bash
dotnet test
```

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
