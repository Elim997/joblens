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
- One API key: a Google AI Studio key (free tier, no card). It covers both the
  chat model and embeddings, so there is no separate embeddings signup.

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
- One job group's `chat_jid` is `120363427094606388@g.us`. Put every group you
  want ingested in `JobLens:GroupChatJids` (a list - see step 5).
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
Secrets and machine-specific values, never committed (this repo may go public):
```
cd src/JobLens.Api
dotnet user-secrets init
dotnet user-secrets set "Gemini:ApiKey" "your_google_ai_studio_key"
dotnet user-secrets set "Postgres:ConnectionString" "Host=localhost;Port=5432;Database=joblens;Username=postgres;Password=postgres"
dotnet user-secrets set "JobLens:MessagesDbPath" "C:/path/to/whatsapp-mcp/whatsapp-bridge/store/messages.db"
dotnet user-secrets set "JobLens:GroupChatJids:0" "120363427094606388@g.us"
dotnet user-secrets set "JobLens:GroupChatJids:1" "<second_group_chat_jid>"
dotnet user-secrets set "Rezi:BaseResumes:0:Name" "QA Automation Developer"
dotnet user-secrets set "Rezi:BaseResumes:0:Id" "<qa_automation_developer_resume_id>"
dotnet user-secrets set "Rezi:BaseResumes:1:Name" "Junior Backend Engineer"
dotnet user-secrets set "Rezi:BaseResumes:1:Id" "<junior_backend_engineer_resume_id>"
dotnet user-secrets set "Rezi:BaseResumes:2:Name" "Full Stack Developer"
dotnet user-secrets set "Rezi:BaseResumes:2:Id" "<full_stack_developer_resume_id>"
dotnet user-secrets set "Rezi:ForEditResumeId" "<for_edit_resume_id>"
```
Use forward slashes in the path even on Windows, and give the full absolute path.
`GroupChatJids` is a list, so each group gets its own indexed key
(`:0`, `:1`, ...); it stays in user-secrets rather than appsettings.json
because a chat_jid identifies a real WhatsApp group.

`Rezi:BaseResumes` is likewise a list in user-secrets, not appsettings - the IDs
identify resumes in one specific Rezi account. `IResumeTailor` (Phase 2) reads
these three bases and picks the best-fit one per posting; it never writes to
them. `ForEditResumeId` isn't read until Phase 3's write step. Find your own
resume IDs with `list_resumes` via `dotnet run --project tools/ReziLogin`
(prints resume names next to their ids) after completing the re-login in
section 7 below.

Committed `appsettings.json` holds only non-identifying config, the target
category list:
```json
"JobLens": {
  "TargetCategories": [ "Software", "QA" ]
}
```
There is no sender allowlist. The source filters by `chat_jid IN (...)` and
skips media-only rows; job vs promo is decided by content structure.

Register pgvector with Npgsql in `Program.cs`:
```csharp
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connString);
dataSourceBuilder.UseVector();
var dataSource = dataSourceBuilder.Build();
```

Register Gemini through the OpenAI client pointed at its compatibility endpoint,
exposed as the Microsoft.Extensions.AI abstractions:
```csharp
var gemini = new OpenAIClient(
    new ApiKeyCredential(geminiKey),
    new OpenAIClientOptions { Endpoint = new Uri("https://generativelanguage.googleapis.com/v1beta/openai/") });

IChatClient chat = gemini.GetChatClient("gemini-flash-latest").AsIChatClient();
IEmbeddingGenerator<string, Embedding<float>> embeddings =
    gemini.GetEmbeddingClient("gemini-embedding-001").AsIEmbeddingGenerator();
```
Confirm the OpenAI .NET SDK behaves against the compatibility endpoint. If you
hit friction, a community Gemini .NET package (e.g. Mscc.GenerativeAI) is a
fallback that also implements `IChatClient`.

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
  5. relevance scoring (vector prefilter, then Claude)
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
