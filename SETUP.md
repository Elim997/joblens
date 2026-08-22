# SETUP.md: Environment Setup (Windows)

Do these in order. Steps 1 to 5 are things you run yourself in a terminal. Step 6
is where you hand off to Claude Code. Do not start Claude Code building until the
WhatsApp bridge and Postgres are both up, so it has real data and a real database
to work against instead of mocks.

## 0. Install prerequisites
- .NET 10 SDK
- Docker Desktop (runs Postgres + pgvector)
- Go (builds and runs the WhatsApp bridge)
- A C compiler for Windows: install MSYS2, then add its `ucrt64\bin` to PATH.
  The bridge's SQLite driver needs CGO, which needs a C compiler on Windows.
- Git
- A Google AI Studio key (free tier, no card) - embeddings only. Chat/reasoning
  (relevance scoring and resume tailoring) does not use Gemini at all; see
  "OmniRoute (external prerequisite)" below.
- **OmniRoute**, running separately, reachable at whatever `Llm:BaseUrl` you
  configure (e.g. `http://localhost:20128/v1`), with its own API key. OmniRoute
  is not part of this repo and JobLens does not start it, stop it, manage its
  process, or manage the provider OAuth sessions (Claude/Codex) behind it - see
  the "OmniRoute (external prerequisite)" section below for the setup and
  startup order this implies.

## 1. Stand up the WhatsApp bridge (read-only data source)
```
git clone https://github.com/lharries/whatsapp-mcp.git
cd whatsapp-mcp/whatsapp-bridge
go env -w CGO_ENABLED=1
go run main.go
```
- Scan the QR with the phone whose number is in the job group.
- Wait a few minutes for message history to sync.
- The message database lands at `whatsapp-bridge/store/messages.db`.
- You only need this Go bridge. Skip the Python MCP server in that repo entirely.
  Your .NET reads the SQLite directly, and you never send anything.
- You may need to re-scan the QR roughly every 20 days.

## 2. Find the group(s) in the SQLite (you have already done this)
- Open `whatsapp-bridge/store/messages.db` with DB Browser for SQLite or the
  `sqlite3` CLI.
- Tables: `chats` and `messages`.
- A job group's `chat_jid` looks like `<whatsapp-group-chat-jid>`. Put every group
  you want ingested in `JobLens:GroupChatJids` (a list - see step 5).
- Do NOT filter by sender: about 650 posts share the group's own id, so sender
  does not separate jobs from promos. `IJobFeedSource` filters by
  `WHERE chat_jid IN (...)` and skips media-only rows; job vs promo is a
  content-structure decision.
- The `messages` columns that matter: `chat_jid`, `sender`, `content`,
  `timestamp`, `is_from_me`, `media_type`.

## 3. Stand up Postgres + pgvector (Docker)
```
docker run -d --name joblens-pg -e POSTGRES_PASSWORD=postgres -p 5432:5432 pgvector/pgvector:pg17
docker exec -it joblens-pg createdb -U postgres joblens
docker exec -it joblens-pg psql -U postgres -d joblens -c "CREATE EXTENSION IF NOT EXISTS vector;"
```
- If port 5432 is already taken, use `-p 5433:5432` and adjust the connection
  string later.

## 4. Scaffold the .NET solution
```
dotnet new sln -n JobLens
dotnet new webapi -n JobLens.Api -o src/JobLens.Api
dotnet new classlib -n JobLens.Core -o src/JobLens.Core
dotnet new xunit -n JobLens.Tests -o tests/JobLens.Tests
dotnet sln add src/JobLens.Api src/JobLens.Core tests/JobLens.Tests
dotnet add src/JobLens.Api reference src/JobLens.Core
dotnet add tests/JobLens.Tests reference src/JobLens.Core
```
Packages:
```
dotnet add src/JobLens.Core package Microsoft.Extensions.AI
dotnet add src/JobLens.Core package Microsoft.Extensions.AI.OpenAI
dotnet add src/JobLens.Core package OpenAI   # used to call Gemini's OpenAI-compatible endpoint
dotnet add src/JobLens.Core package Npgsql
dotnet add src/JobLens.Core package Pgvector
dotnet add src/JobLens.Core package Microsoft.Data.Sqlite
# Only if you later swap the provider to Claude:
# dotnet add src/JobLens.Core package Anthropic
```
Put `CLAUDE.md` at the repo root (you already have it).

## 5. Configure secrets and settings

### Required configuration keys

| Key | Secret? | Purpose |
|---|---|---|
| `JobLens:MessagesDbPath` | No, but identifying (local path) | Path to the bridge's `messages.db`. |
| `JobLens:GroupChatJids` | No, but identifying | List of WhatsApp group chat_jids to ingest from. |
| `Postgres:ConnectionString` | Yes (has a password) | Local Postgres+pgvector connection. |
| `Gemini:ApiKey` | Yes | Google AI Studio key - embeddings only (`gemini-embedding-001`, 1536 dimensions). |
| `Llm:BaseUrl` | No | OmniRoute's base URL, e.g. `http://localhost:20128/v1`. |
| `Llm:ApiKey` | Yes | OmniRoute's API key. Treat this as a real secret even though OmniRoute currently runs on localhost - a local-only key is still a key, and this repo does not assume "localhost" means "safe to expose." |
| `Llm:ScoringModel` | No | Model ID OmniRoute routes relevance scoring through - normally `coding-fallback`. |
| `Llm:TailoringModel` | No | Model ID OmniRoute routes resume tailoring through - a separately pinned model, **never** `coding-fallback` (`Program.ValidateRequiredConfig` refuses to start otherwise). Currently `cc/claude-sonnet-5` as a **provisional** default - see README.md's provider-architecture section for why. |

Plus the existing `Rezi:*` keys below. `Program.ValidateRequiredConfig` fails
fast at startup with a clear message if any required key above is missing or
malformed - it never lets the app come up and fail later at first use.

`Llm:BaseUrl`, `Llm:ScoringModel`, and `Llm:TailoringModel` aren't secret or
identifying, so the checked-in `appsettings.json` already ships safe local
defaults for them - you don't strictly need to set them yourself unless you
want to override the default locally. `Gemini:ApiKey` and `Llm:ApiKey` are the
two values that must never land in a tracked file.

### User-secrets (preferred for local development)

Secrets and machine-specific values, never committed (this repo may go public).
Placeholders only below - fill in your own real values when you run these,
not while implementing/reviewing documentation changes:
```
cd src/JobLens.Api
dotnet user-secrets init
dotnet user-secrets set "Gemini:ApiKey" "YOUR_GEMINI_API_KEY" --project src/JobLens.Api
dotnet user-secrets set "Llm:ApiKey" "YOUR_OMNIROUTE_API_KEY" --project src/JobLens.Api
dotnet user-secrets set "Llm:BaseUrl" "http://localhost:20128/v1" --project src/JobLens.Api
dotnet user-secrets set "Llm:ScoringModel" "coding-fallback" --project src/JobLens.Api
dotnet user-secrets set "Llm:TailoringModel" "cc/claude-sonnet-5" --project src/JobLens.Api
dotnet user-secrets set "Postgres:ConnectionString" "Host=localhost;Port=5432;Database=joblens;Username=postgres;Password=postgres"
dotnet user-secrets set "JobLens:MessagesDbPath" "C:/path/to/whatsapp-mcp/whatsapp-bridge/store/messages.db"
dotnet user-secrets set "JobLens:GroupChatJids:0" "<whatsapp-group-chat-jid>"
dotnet user-secrets set "JobLens:GroupChatJids:1" "<second_group_chat_jid>"
dotnet user-secrets set "Rezi:BaseResumes:0:Name" "QA Automation Developer"
dotnet user-secrets set "Rezi:BaseResumes:0:Id" "<qa_automation_developer_resume_id>"
dotnet user-secrets set "Rezi:BaseResumes:1:Name" "Junior Backend Engineer"
dotnet user-secrets set "Rezi:BaseResumes:1:Id" "<junior_backend_engineer_resume_id>"
dotnet user-secrets set "Rezi:BaseResumes:2:Name" "Full Stack Developer"
dotnet user-secrets set "Rezi:BaseResumes:2:Id" "<full_stack_developer_resume_id>"
dotnet user-secrets set "Rezi:ForEditResumeId" "<for_edit_resume_id>"
```
`Llm:TailoringModel = cc/claude-sonnet-5` above is the **provisional**
default - not a final pin (see README.md). The `Llm:BaseUrl` /
`Llm:ScoringModel` / `Llm:TailoringModel` commands above are optional
overrides of `appsettings.json`'s already-safe defaults, not secrets; only
`Gemini:ApiKey` and `Llm:ApiKey` are things user-secrets exists to protect.

Use forward slashes in the path even on Windows, and give the full absolute path.
`GroupChatJids` is a list, so each group gets its own indexed key
(`:0`, `:1`, ...); it stays in user-secrets rather than appsettings.json
because a chat_jid identifies a real WhatsApp group.

`Rezi:BaseResumes` is likewise a list in user-secrets, not appsettings - the IDs
identify resumes in one specific Rezi account. `IResumeTailor` reads
these bases and picks the best-fit one per posting; it never writes to
them. `POST /tailor?messageId=X` only creates or reuses an immutable persisted
draft and performs zero Rezi writes. `Rezi:ForEditResumeId` is the sole Rezi
slot that `POST /tailored/{draftId}/export-to-rezi` can update, and export happens
only through that explicit endpoint. See README.md's tailoring/export safety
contract. Find your own resume IDs with `list_resumes` via
`dotnet run --project tools/ReziLogin`
(prints resume names next to their ids) after completing the re-login in
section 7 below.

### Environment-variable equivalents

Anywhere user-secrets isn't available (CI, containers, a non-dev machine), the
same keys work as environment variables using .NET's `__` section-separator
convention, e.g. `Llm__ApiKey`, `Llm__BaseUrl`, `Llm__ScoringModel`,
`Llm__TailoringModel`, `Gemini__ApiKey`. The rest of the keys above follow the
same pattern (`Postgres__ConnectionString`, `JobLens__MessagesDbPath`, ...).

Committed `appsettings.json` holds only non-secret, non-identifying config -
the `Llm:BaseUrl`/`Llm:ScoringModel`/`Llm:TailoringModel` defaults from the
table above, plus the target category list:
```json
"JobLens": {
  "TargetCategories": [ "Software", "QA" ]
}
```
There is no sender allowlist. The source filters by `chat_jid IN (...)` and
skips media-only rows; job vs promo is decided by content structure.

### OmniRoute (external prerequisite)

OmniRoute is not part of this repo. JobLens treats it purely as an external
chat/reasoning provider reachable at `Llm:BaseUrl`: it doesn't start it, stop
it, manage its process, manage the provider OAuth sessions behind it (Claude,
Codex), refresh those tokens, or implement any provider-specific quota/retry
logic - all of that is OmniRoute's job, not JobLens's. The normal dev flow is:

1. Start OmniRoute yourself (e.g. `omniroute serve`), and make sure it's
   listening at whatever host/port you put in `Llm:BaseUrl`.
2. Start whichever other local infrastructure JobLens needs (Postgres, the
   WhatsApp bridge - steps 1 and 3 above).
3. Run JobLens.

Gemini stays a separate, embeddings-only dependency - it doesn't go through
OmniRoute, and OmniRoute doesn't need to be up for anything embeddings-only
(e.g. `/query`'s embed-then-search step still needs Gemini, but never touches
OmniRoute; only `/run` and `/tailor` need OmniRoute reachable).

Register pgvector with Npgsql in `Program.cs`:
```csharp
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connString);
dataSourceBuilder.UseVector();
var dataSource = dataSourceBuilder.Build();
```

Register Gemini embeddings and the two OmniRoute chat-client roles separately -
this is what `Program.cs` actually does today, not a single shared client:
```csharp
// Gemini: embeddings only, via its OpenAI-compatible endpoint.
var gemini = new OpenAIClient(
    new ApiKeyCredential(geminiKey),
    new OpenAIClientOptions { Endpoint = new Uri("https://generativelanguage.googleapis.com/v1beta/openai/") });
IEmbeddingGenerator<string, Embedding<float>> embeddings =
    gemini.GetEmbeddingClient("gemini-embedding-001").AsIEmbeddingGenerator();

// OmniRoute: all chat/reasoning, as two independently-configured roles - never
// one shared/unqualified IChatClient.
IChatClient CreateOmniRouteChatClient(LlmOptions options, string model)
{
    var client = new OpenAIClient(
        new ApiKeyCredential(options.ApiKey),
        new OpenAIClientOptions { Endpoint = new Uri(options.BaseUrl) });
    return client.GetChatClient(model).AsIChatClient();
}
IScoringChatClient scoring = new ScoringChatClient(CreateOmniRouteChatClient(llm, llm.ScoringModel));
ITailoringChatClient tailoring = new TailoringChatClient(CreateOmniRouteChatClient(llm, llm.TailoringModel));
```
Confirm the OpenAI .NET SDK behaves against both compatibility endpoints (see
README.md's provider-architecture section for exactly what's verified about
OmniRoute's Chat Completions compatibility vs. what's still intended-only).

## 6. Hand off to Claude Code
- Open the repo folder in VS Code and start Claude Code.
- Confirm it picked up `CLAUDE.md`: ask it to summarize the project; it should
  describe the read-only WhatsApp pipeline, not guess.
- Build the milestones from CLAUDE.md in order, one commit each, tests green
  before moving on:
  1. skeleton + health check + config
  2. `IJobFeedSource` reading `messages.db`, filtered to the group chat_jids
  3. parser + category filter
  4. embed + store in pgvector, plus the semantic `query` command
  5. relevance scoring (vector prefilter, then `IScoringChatClient` through OmniRoute)
  6. notify on matches
  7. eval harness
- Give Claude Code the `messages.db` path and let it inspect the schema for the
  `IJobFeedSource` query.

Do not let it scaffold a frontend, app Dockerfile, or Telegram source until the
WhatsApp-to-notify loop runs end to end and the eval passes.

## 7. Rezi resume tailoring: re-login

`IResumeClient` talks to Rezi's MCP server (`https://api.rezi.ai/mcp`), which uses OAuth
2.1 with no refresh token - access tokens last about 30 days, and getting a new one
always requires a one-time interactive browser sign-in (see the Phase 0 spike in
`spikes/rezi-mcp-auth-spike/` for why: confirmed live against Rezi's own OAuth discovery
metadata). The running `JobLens.Api` service never opens a browser itself - if the token
is missing or has expired, any resume-tailoring call fails immediately with a
`ReziAuthenticationRequiredException` telling you to do this:

```
dotnet run --project tools/ReziLogin
```

This opens your browser to Rezi's sign-in page once, then saves a fresh ~30-day token to
an encrypted local cache (`%LOCALAPPDATA%\JobLens\rezi-token.dat`, DPAPI-protected,
gitignored, never committed). `JobLens.Api` picks up the new token automatically on its
very next request - no restart needed. You'll need to do this roughly once a month.

## 8. One-command dev startup

Once steps 1-5 have been done at least once (bridge cloned and logged in, Postgres
container created, secrets set), `scripts/start-joblens.ps1` starts the whole stack -
Postgres, the WhatsApp bridge, and the API - with one command.

**One-time setup:**
```
cd scripts
copy dev-config.ps1.example dev-config.ps1
notepad dev-config.ps1   # fill in BridgeDir, ApiProjectPath, etc.
```
`dev-config.ps1` is gitignored (machine-specific paths, never committed).

**Every time after that:**
```
.\scripts\start-joblens.ps1
```

**What it does, in order:**
1. Checks the `joblens-pg` Docker container: starts it if it exists but is stopped,
   leaves it alone if already running. Never creates or recreates it - if it doesn't
   exist yet, the script stops and points you back to step 3.
2. Checks whether the WhatsApp bridge is already up (first by a PID file this script
   itself writes, then by whether anything is listening on its port, 8080 by default).
   If not, it starts `go run main.go` in the bridge's own console window, in the
   bridge's directory - so a first-run QR prompt is visible there and the bridge's
   own log output stays visible throughout. Never touches the bridge's session files
   or `messages.db`, and never starts a second bridge process if one is already up.
3. Runs `dotnet run` for `JobLens.Api` in the current window. **Ctrl+C here stops
   everything this script started.**
4. On exit, stops only the bridge window *this run* started (a tree-kill, since `go
   run` spawns the actual bridge process as a child of `go.exe`, which is itself a
   child of the window's shell). If the bridge was already running before this script
   ran, it's left alone - the script never stops something it didn't start. Postgres
   is always left running.

**Verifying the bridge is actually live:**
- Its own console window logs `✓ Connected to WhatsApp!` once connected, and keeps
  logging as messages arrive.
- Or watch `messages.db` grow: `(Get-Item $MessagesDbPath).Length` (from
  `dev-config.ps1`'s value), run twice a minute or two apart while the group is
  active. For exact row counts, use the `sqlite3` CLI or DB Browser for SQLite as
  described in step 2.

**Known limitations:**
- Windows/PowerShell only, matching the rest of this repo's dev setup.
- First-time bridge login still needs a human to scan the QR code in the bridge's
  window - that step isn't and can't be automated by this script.
- Assumes Go, Docker, and the .NET SDK are already installed (steps 0/1/3/4) and the
  Postgres container and bridge repo already exist - this script only starts things,
  it never provisions them.
- The bridge shutdown is a hard `taskkill /T /F` (tree-kill), not a graceful signal -
  fine here because the bridge persists its session/messages to SQLite continuously,
  not just on clean exit, but it means no graceful WhatsApp disconnect handshake.
- If the bridge was started outside this script (e.g. you ran `go run main.go`
  yourself in another window), this script detects and leaves it running, but can't
  stop it for you on exit - close that window yourself when you're done.

## 9. Scheduled operation (Windows Task Scheduler)

Section 8 is the interactive dev loop. This section is the unattended one: a
published build that Windows runs three times a day and that exits when it's
done.

```
Windows Task Scheduler
  -> <deploy root>\run-joblens-scheduled.ps1
    -> <deploy root>\app\JobLens.Api.exe --run-once
      -> RunLock -> preflight -> ingest -> backlog scoring -> structured report -> exit
```

No Kestrel host is left running, no second JobLens process is started, and no
HTTP call is made to a local API - `--run-once` is the same in-process pipeline
the API's endpoints use, driven straight from the CLI.

### Prerequisites

Everything from sections 0-5 and 7, already working interactively, plus the same
external processes JobLens always needs, each with its own lifecycle:

- **PostgreSQL + pgvector** (the `joblens-pg` container) running.
- **The WhatsApp bridge** running, so `messages.db` keeps receiving new messages.
- **OmniRoute** reachable at `Llm:BaseUrl`.
- **A valid Rezi token** if you want tailoring to happen (section 7).

The scheduled task starts and stops **none** of these, by design. It launches
JobLens and nothing else. If a dependency is down, JobLens reports it through
preflight and exits with a status that says so, rather than trying to repair your
machine at 23:00. Practically: keep `scripts/start-joblens.ps1`'s dependencies up,
or accept that a run landing in a window where they're down will exit degraded or
fatal and log why.

### Publish

```
.\scripts\publish-joblens.ps1
```

This publishes **Release**, **framework-dependent** (about 11 MB, ~29 files) into
a stable, user-owned location and copies the scheduled launcher next to it:

```
%LOCALAPPDATA%\JobLens\
  app\                       published binaries, replaced on every publish
  app\JobLens.Api.exe        what the task ultimately runs
  run-joblens-scheduled.ps1  what the task directly invokes
  logs\                      one log per run
  run.lock                   RunLock's file        (pre-existing, never touched)
  rezi-token.dat             DPAPI token cache     (pre-existing, never touched)
```

Framework-dependent rather than self-contained because the deployment target *is*
the machine that builds this repository, so the .NET 10 SDK - and therefore the
runtime - is already installed. A self-contained publish would add roughly 70 MB
of duplicated runtime per publish and a `RuntimeIdentifier` to keep in sync, for
no benefit on a single-machine deployment.

`%LOCALAPPDATA%\JobLens` is deliberate too: it needs no administrator rights, it's
already where `run.lock` and the DPAPI-protected `rezi-token.dat` live, and it
resolves per user from the environment - so nothing in this repository has to name
your Windows account. Use `-DeployRoot` if you want it somewhere else.

The publish script clears `app\` before publishing (so a file dropped from the
project can't linger), and refuses to clear a non-empty `app\` that doesn't look
like a previous JobLens publish. Logs, the lock, the token cache, user-secrets,
`messages.db`, and PostgreSQL are all outside `app\` and are never touched.

### Verify the published build manually, as yourself, before scheduling anything

```
& "$env:LOCALAPPDATA\JobLens\app\JobLens.Api.exe" --preflight
```

`--preflight` is read-only: it validates required configuration, probes
PostgreSQL, reads the WhatsApp SQLite source and checks how fresh the newest
message in your configured groups is, TCP-probes the bridge, and reports whether
the Rezi token cache is present and readable. It never writes, never ingests,
never scores, and never prints a key, a value, or a token. Exit codes: `0`
success, `1` fatal, `3` degraded, `4` cancelled.

Run this from the **same Windows account** the task will use, because that is
what proves the parts that differ per user actually resolve: `%LOCALAPPDATA%`,
the run lock, the DPAPI token cache, user-secrets, and the `messages.db` path.
A preflight that passed under a different account proves nothing about the
scheduled one.

Then do one real run:

```
& "$env:LOCALAPPDATA\JobLens\app\JobLens.Api.exe" --run-once
```

and one through the launcher, which is what the task actually invokes:

```
& "$env:LOCALAPPDATA\JobLens\run-joblens-scheduled.ps1"
```

Running `--run-once` twice is safe and is worth doing: ingest de-duplicates by
message id, so the second run reports the same postings as `alreadyStored` and
stores no duplicates.

### Register the task

```
.\scripts\register-joblens-task.ps1 -WhatIf   # prints the exact plan, changes nothing
.\scripts\register-joblens-task.ps1           # registers or updates it
```

The script refuses to register if nothing is published yet, prints everything it
is about to register first, and is safe to re-run - re-running updates the single
task named `JobLens Scheduled Run` in place and touches no other task.

**Times.** One task with three daily triggers, in local machine time: **12:00**,
**18:00**, **23:00**. One task rather than three keeps the schedule a single
policy that can't drift apart. Override with `-At`.

**Identity.** The task runs as **you**, logon type **Interactive** ("run only when
the user is logged on"), not elevated. This is a requirement, not a default:

- `rezi-token.dat` is DPAPI-protected for your account. Only a real interactive
  logon session of that same account can decrypt it.
- "Run whether user is logged on or not" would need either a stored Windows
  password - which this repo will not ask for or keep anywhere - or an S4U logon,
  and an S4U token has no access to your DPAPI master key, so Rezi authentication
  would break.
- `SYSTEM` or any other account resolves a *different* `%LOCALAPPDATA%`, a
  different user-secrets store and a different run-lock path, and cannot read your
  token cache.

So stay logged on. A run that falls in a logged-off or sleeping window is picked
up afterwards by the missed-run setting below.

**Settings, and why:**

| Setting | Value | Why |
|---|---|---|
| Multiple instances | Do not start a new instance | A second concurrent run would just hit `RunLock` and exit `2`. `RunLock` - not this setting - is the authoritative guard; it also covers the API's `/ingest` and `/run`, which Task Scheduler can't see. |
| Missed runs | Run as soon as possible afterwards | Safe here: ingest is idempotent (repeats come back as `alreadyStored`) and scoring drains the stored backlog rather than only what just arrived, so a catch-up run does real work and creates no duplicates. |
| Execution time limit | 2 hours | A normal run is minutes; a large first backlog with per-batch LLM scoring is legitimately slow, so don't kill healthy runs. Bounded so a wedged process can't hold `RunLock` forever. Termination is safe - the lock is an open file handle Windows releases when the process dies. |
| Battery | May start on battery, not stopped on battery | Desktop machine; and even on a laptop, skipping an ingest to save power costs more than it saves. |
| Network | No network condition | The run does need the network, but a Task Scheduler condition would *silently skip*. Preflight instead reports an unreachable dependency with an exit code and a log line. |
| Wake to run | No | Not worth waking the machine for a backlog-based pipeline; the missed-run setting catches it up on resume. |
| Working directory | The published `app\` directory | Task Scheduler doesn't start a process in its own directory, and `appsettings.json` sits beside the executable. The launcher pins it as well, so both paths are covered. |

Nothing sensitive goes into the task: the action is a path plus the single
argument `--run-once`. Every secret still comes from the same per-user
user-secrets store and environment the interactive app uses - the published
assembly carries the `UserSecretsId`, and `Program.InsertUserSecretsBeforeEnvironmentVariables`
(Milestone F1) loads user-secrets in **every** environment, not just Development,
precisely so a Task Scheduler-launched process - which runs as Production - sees
them. No new secret store was introduced for scheduling.

Inspect what actually got registered:

```
Get-ScheduledTask -TaskName 'JobLens Scheduled Run' | Get-ScheduledTaskInfo
Start-ScheduledTask -TaskName 'JobLens Scheduled Run'   # run it now, on demand
```

### Logs

One pair of files per run, in `%LOCALAPPDATA%\JobLens\logs`:

- `joblens-<yyyyMMdd-HHmmss>.out.log` - everything JobLens wrote to standard
  output, which is where the .NET console logger sends the structured startup,
  preflight, and scheduled-run report lines. The last line is added by the
  launcher and records the process's final exit code.
- `joblens-<yyyyMMdd-HHmmss>.err.log` - standard error, **kept only if something
  was written to it**. An `.err.log` existing at all is therefore itself a signal.

Nothing is discarded. The launcher keeps the most recent 60 runs (about twenty
days at three a day) and deletes older pairs; change it with `-RetainLogCount`.
No new logging framework was added to the application for this - the deployment
captures the process's streams, which is the smallest thing that works.

The run report names postings, companies, scores, templates and draft outcomes,
but never a key, a token, a connection string, a group chat_jid, or a resume id.

### Exit codes

The launcher returns JobLens's own exit code unchanged, so Task Scheduler's
**Last Run Result** is JobLens's verdict:

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | Fatal - preflight found a broken environment, or an unexpected error. Nothing useful ran. |
| `2` | Another pipeline run held the shared lock, so this run did nothing. |
| `3` | Completed, degraded - real work happened and was kept, but something is wrong. See below. |
| `4` | Cancelled. |
| `64` | The **launcher** failed before JobLens started (typically: nothing published yet). Deliberately outside JobLens's range so the two can't be confused. |

Exit `3` means the run **succeeded and kept its results**, and is expected from
time to time. It is produced by: a degraded preflight (e.g. the bridge is down but
the SQLite source is still readable, so existing messages still ingest and the
backlog still scores), a Rezi authentication failure (scoring and matching are
preserved; only drafting is skipped), a scoring transport failure against
OmniRoute (the fail-soft partial result is preserved), the pipeline stopping
early, or recoverable tailoring failures. A stale-source warning on an otherwise
healthy preflight is reported but does **not** by itself degrade the run.

### Updating JobLens later

```
git pull
.\scripts\publish-joblens.ps1
```

That's the whole procedure. The task points at the stable deployment root, never
at a version-specific output directory, so it does not need to be re-registered -
including when the launcher itself changes, since publishing refreshes it too.
Re-register only if you want to change the times, the settings, or the deployment
path.

### Uninstalling the schedule

```
.\scripts\unregister-joblens-task.ps1 -WhatIf
.\scripts\unregister-joblens-task.ps1
```

Removes exactly that one task and stops all future automatic runs. It
deliberately leaves the published binaries, the logs, `run.lock`,
`rezi-token.dat`, user-secrets, the PostgreSQL database and container, and the
WhatsApp session and `messages.db` alone - none of that is owned by the
scheduler, and some of it is expensive or impossible to recreate. JobLens still
runs interactively exactly as before. Delete `%LOCALAPPDATA%\JobLens\app` and
`%LOCALAPPDATA%\JobLens\logs` by hand if you want the disk space back.

### Troubleshooting

Start with the newest `.out.log` in `%LOCALAPPDATA%\JobLens\logs`. Its first lines
are the build marker and one `Config source: ... loaded=` line per configuration
provider, and its final lines are the scheduled-run report and the launcher's
exit-code footer. Between those two you can usually tell what happened without
touching Task Scheduler at all.

| Symptom | What it means / what to do |
|---|---|
| **Last Run Result `1`, log says a preflight failure** | A required dependency or key is broken. The preflight failure lines name which. Nothing was ingested or scored. |
| **Last Run Result `2`** | Another run held `RunLock` - a still-running earlier scheduled run, or an interactive `/ingest`, `/run`, or `--run-once`. Normally self-correcting at the next trigger. If every run exits `2`, check for a stuck JobLens process; the lock is released when the process ends, so a stale `run.lock` file on disk is normal and is *not* the cause. |
| **Last Run Result `3`** | Completed with a problem, results kept. Find the degrading condition in the log: a preflight `Degraded` status, `reziAuthFailed=True`, a scoring transport failure, `stoppedEarly=True`, or `tailoringFailures>0`. |
| **Last Run Result `4`** | Cancelled - Ctrl+C on a manual run, or Task Scheduler hitting the 2-hour limit or ending the task. |
| **Last Run Result `64`** | The launcher couldn't start JobLens. Almost always: never published, or the deployment root was moved. Re-run `scripts\publish-joblens.ps1`. See `logs\launcher-errors.log`. |
| **PostgreSQL unavailable** | Preflight reports the readiness probe failing and the run is fatal (`1`). Start the container (`docker start joblens-pg`) - the task will not start it for you, on purpose. |
| **WhatsApp bridge unavailable** | The bridge TCP probe fails and the run is **degraded** (`3`), not fatal: `messages.db` is still readable, so already-synced messages still ingest and the backlog still scores. You just aren't receiving anything new. Restart the bridge (section 1 or `scripts\start-joblens.ps1`). |
| **"Latest configured-group source message is older than 12 hours"** | Warning only; the run stays successful. Either the group is genuinely quiet, or the bridge has been silently disconnected for a while - check the bridge window, and re-scan the QR if the ~20-day session expired. |
| **Rezi authentication expired or missing** | The run is degraded (`3`); matches are still scored and notified, drafts are skipped with outcome `SkippedAuth`. Fix with `dotnet run --project tools/ReziLogin` (section 7) **as the same Windows account the task runs as** - the token cache is DPAPI-protected per user and cannot be shared between accounts. |
| **Task runs as the wrong account** | `Get-ScheduledTask -TaskName 'JobLens Scheduled Run' \| Select-Object -ExpandProperty Principal` should show your account with `LogonType Interactive`. If it shows `SYSTEM` or another user, the run will look for a different `%LOCALAPPDATA%`, a different user-secrets store and an unreadable token cache. Re-run `scripts\register-joblens-task.ps1` while logged in as the right account. |
| **Missing configuration / user-secrets not found** | The `Config source: ...` lines near the top of the log tell you whether the user-secrets provider loaded. If it didn't, you're running as a different account, or the secrets were never set for this one - re-run the `dotnet user-secrets set` commands from section 5 as that account. Startup validation fails fast with a clear message naming the missing key; it never starts and fails later at first use. |
| **Path or working-directory problems** | JobLens resolves the run lock and token cache from `%LOCALAPPDATA%` and takes `messages.db` from configuration, so paths don't depend on where it was launched. `appsettings.json` does sit beside the executable, which is why both the task and the launcher pin the working directory to `app\`. If the config-source lines show `appsettings.json loaded=False`, the working directory is the thing to check. |
| **Nothing ran at all at 12:00 / 18:00 / 23:00** | The account was logged off (the task is Interactive by necessity - see Identity above), or the machine was asleep and hasn't resumed yet. The missed-run setting runs it once the session is back. Confirm the schedule with `Get-ScheduledTask -TaskName 'JobLens Scheduled Run' \| Get-ScheduledTaskInfo`. |
