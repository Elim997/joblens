# CLAUDE.md: JobLens (WhatsApp / Telegram job-feed agent, portfolio project)

## What this is

JobLens is a .NET agent and RAG pipeline that ingests job postings from a closed WhatsApp
group, filters and scores them against my profile, notifies me of matches, and
can tailor my CV via Rezi. Built as a portfolio piece to show LLM agent /
pipeline and RAG experience for junior backend / full-stack / QA-automation
roles. Telegram is a planned future source. Keep scope tight and finishable.

## The ingestion reality (read before building the source)

- There is no official WhatsApp API for reading messages from a group you are
  in. The Business Cloud API only handles your own business number's webhooks.
- The working route is an unofficial WhatsApp Web bridge (Baileys or whatsmeow),
  e.g. lharries/whatsapp-mcp. You log in by scanning a QR from your personal
  account; it mirrors messages into a local SQLite DB and can expose MCP tools.
- Run the bridge as a separate process. The .NET pipeline reads its local SQLite
  store (simplest) or calls its MCP tools (more agentic, shows MCP experience).
- Telegram, when added, is cleaner: official Bot API, no ToS gray area. Both
  sources sit behind one interface so the switch is trivial.

## Honest scope note on "RAG"

The core loop is an extraction and scoring agent pipeline, not classic RAG. That
is fine; agent/pipeline experience is itself in demand. RAG is a deliberate
component here: embed each posting and my skills profile into pgvector, use
vector similarity as the cheap first-pass match, and expose a semantic query
over the archive of past postings. Build that part on purpose so the project
legitimately demonstrates both.

## Stack (pinned, do not substitute without asking)

- Runtime: ASP.NET Core minimal API or worker service on .NET 10.
- Embeddings provider: Google Gemini on the free tier, via its
  OpenAI-compatible endpoint, consumed with the OpenAI .NET SDK and exposed
  through Microsoft.Extensions.AI `IEmbeddingGenerator`. Endpoint:
  https://generativelanguage.googleapis.com/v1beta/openai/ . Auth is your
  Google AI Studio key sent as a Bearer token. **Gemini is embeddings-only** -
  it never sees a chat/scoring/tailoring prompt.
  - Embeddings model: `gemini-embedding-001`, 1536 dimensions.
  - Caveat: Google may train on free-tier prompts. Fine for public job posts.
- Chat/reasoning provider: **OmniRoute**, an external prerequisite process
  (not part of this repo) reachable at `Llm:BaseUrl`. All relevance scoring
  and resume tailoring go through it via two provider-neutral roles,
  `IScoringChatClient` (model = `Llm:ScoringModel`, normally `coding-fallback`)
  and `ITailoringChatClient` (model = `Llm:TailoringModel`, a separately
  pinned model - never `coding-fallback`; `Program.ValidateRequiredConfig`
  refuses to start otherwise). No unqualified `IChatClient` is registered in
  DI. JobLens does not start, stop, or manage OmniRoute's process, manage the
  provider OAuth sessions (Claude/Codex) behind it, or implement any
  provider-specific quota/fallback logic - all of that is OmniRoute's job.
  See README.md's provider-architecture section for exactly what's verified
  end to end vs. still intended-only about OmniRoute's fallback chain, and
  for `Llm:TailoringModel`'s current provisional-default status.
  - Provider-agnostic on purpose: every chat/reasoning call goes through
    `IScoringChatClient`/`ITailoringChatClient`, and every embedding call
    through `IEmbeddingGenerator`, so swapping either provider is a DI/config
    change, not a rewrite.
- Vector store: PostgreSQL + pgvector (Pgvector NuGet + Npgsql).
- Message source: a separate WhatsApp Web bridge process (unofficial, see
  above), READ-ONLY. The pipeline reads its local SQLite message store and never
  sends. There is no send code path anywhere in this project.
- Tests / eval: xUnit.

## Architecture (each stage = one interface, one implementation, wired in DI)

- `IJobFeedSource`: pull new messages from the source, filtered to the target
  groups' chat_jids (a list - multiple WhatsApp groups can feed the same
  pipeline), skipping media-only rows. WhatsApp now, Telegram later.
- `IPostingParser`: raw message -> structured `JobPosting` (title, category,
  location, seniority, stack, link). The feed uses a fixed format with a Category
  field, so this is parsed deterministically; messages that fail the fixed-layout
  structure check are skipped.
- category pre-filter: drop postings whose Category is not in my target set.
  Cheap, no LLM, runs before anything expensive.
- `IEmbedder` + `IDatastore`: embed surviving postings, upsert into pgvector.
- `IRelevanceScorer`: vector similarity to my profile for a cheap rank, then
  the model scores the top-k with reasoning.
- `INotifier`: send me the matches (email / Telegram / console).
- `IResumeTailor` (optional): call Rezi on high-scoring matches.

Flow: IJobFeedSource -> IPostingParser -> category filter -> IEmbedder ->
IDatastore -> IRelevanceScorer -> INotifier -> (optional) IResumeTailor.
The semantic archive query reuses IEmbedder + IDatastore.

## Source message format (from the real messages.db)

Bridge SQLite has `chats` and `messages` tables. The `messages` columns that
matter: `chat_jid`, `sender`, `content`, `timestamp`, `is_from_me`, `media_type`.
`JobLens:GroupChatJids` (user-secrets, identifying) is a list of chat_jids;
the actual values stay in local configuration, and more groups can feed the same
pipeline. The SQLite query filters `WHERE chat_jid IN (...)`.

Filter by `chat_jid`, NOT by sender. In the real data ~650 posts share one sender
id (the group's own number), so sender does not separate jobs from promos. Skip
rows where `content` is empty or `media_type` is set (image-only promo flyers and
job images, no parseable text). Job vs promo is decided by the content structure
check below, not by who sent it.

Job-bot posts follow a fixed layout, so parse deterministically. Messages that
fail the structure check are skipped rather than sent to an LLM:

- Bold line 1: `*Title* [optional ref number] / Company`. Title is wrapped in
  WhatsApp `*` bold markup; strip the asterisks, then split on " / ".
- Near the top of the message: italic `Location | Category`. It is normally line
  2, but recurring bot variants insert one bold label or two plain metadata lines
  first, so inspect only lines 2–4 for this exact italic structure. Split on
  " | ". Category drives the pre-filter (seen: Software, QA; off-target values
  like Hardware, Research exist and should be dropped).
- Requirement bullet lines follow.
- Apply URL: the first link that is NOT a `referally.*` link (e.g. a company
  careers page or linkedin.com/jobs).
- Strip boilerplate: "Join our community" lines, any block linking
  referally.link, referally-jobos.lovable.app, or referally.setmore.com, and
  any trailing ad block starting with another `*bold headline*` line (e.g. a
  paid interview-prep pitch appended after the real posting, such as "*Book a
  prep session with Nicole*..."). These are ads and are sometimes appended
  inside a real job post, so truncate the description at the first such block
  (nothing from it or after it is kept), do not drop the message.

Promos (a paid interview-prep service, often Hebrew, sometimes image-only) and
boilerplate carry no Title/Company/Category, so the structure check drops them.
Real job posts also appear in Hebrew, so never filter on language.

## Build order (one commit per milestone, test before moving on)

1. Skeleton + config: API key, the group chat_jids, and the messages.db path, all
   from env or user-secrets. Nothing sensitive hardcoded.
2. IJobFeedSource: read new messages from the bridge store, filtered to the group
   chat_jids and skipping media-only rows. Verify you get the raw postings.
3. Parse + category filter: raw -> JobPosting, drop off-category. Test on real
   samples.
4. Embed + store in pgvector. Add a `query` command for semantic archive search.
   This is the RAG showcase.
5. Relevance scoring: vector prefilter against my profile, then the model scores the
   top-k with reasoning.
6. Notify on matches. Optional Rezi tailoring once the loop is stable.
7. Eval harness: label ~20 past postings relevant / not, measure precision and
   recall of the filter + scorer. This keeps the QA angle.

Do NOT wire Telegram, a web UI, or auto-Rezi until the WhatsApp-to-notify loop
runs end to end and the eval passes. Scope creep is the main way this dies
unfinished.

## Conventions

- C# strings use double quotes. For a literal quote use `\"` or a verbatim
  string (`@"..."`).
- Idiomatic .NET: DI, async/await, nullable reference types on.
- Secrets (the Gemini API key, the OmniRoute API key, the WhatsApp session and
  bridge token) live in env, user-secrets, or gitignored config. NEVER commit
  them - an OmniRoute key is still a real secret even though OmniRoute
  currently runs on localhost. The group chat_jids, messages.db path, and
  target-category list are config, not source.
- Prefer clear over clever. Someone will read this in an interview.

## Commands (fill in as they stabilize)

- Build: `dotnet build`
- Test: `dotnet test`
- Run: `dotnet run --project src/<Project>`
