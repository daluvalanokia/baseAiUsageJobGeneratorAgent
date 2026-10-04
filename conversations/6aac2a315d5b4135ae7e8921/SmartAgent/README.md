# SmartAgent — scoped prompt generator (.NET 8)

SmartAgent inspects an application's source and returns **one consolidated,
precise prompt per run, limited to a prioritized scope** — tuned either toward
**improved functionality** or toward **fixing erratic behavior**.

## How a run works

```
source (a/b/c) → heuristic analysis → reasoning-site enrichment →
CONTINUITY MATCH (cross-run fingerprints, escalation, regressions) →
priority scoring → top-N scope → chained consolidated prompt
(AI provider, or built-in deterministic template when AI is offline)
→ feedback loop (CI reports fixed / wont_fix) → next run verifies
```

## Parallel prompt evaluator (scope/depth/width algorithm)

Paste ANY prompt (SmartAgent's own generated prompts or external ones) and the
evaluator automatically:

1. **Segments** it into independently evaluable work units — numbered items,
   finding references `[F001]`/fingerprints, bullets, markdown sections, or
   sentence groups for free prose (so nothing is unevaluable)
2. **Estimates depth** per unit (1–5): length, file spread and hard-domain
   signals (security, migration, concurrency, audit …) → per-unit cost
3. **Derives the width**: threads actually spawned = min(units, MaxThreads, CPU)
4. **Splits the work**: LPT (longest-processing-time-first) bin packing balances
   units into parallel lanes so no thread idles behind a heavy unit
5. **Runs the lanes concurrently** — one worker per lane, units within a lane
   sequential — with per-unit timeout, bounded retries and failure tolerance
   (a failed unit is recorded, never kills the run)
6. **Consolidates** all unit results into one report with a scope-satisfaction
   check: every planned unit must have a result (failures are accounted, gaps
   are not → `scopeSatisfied: false`)

- UI: `/Home/Evaluate`
- API: `POST /api/evaluate-prompt` `{ prompt, maxThreads?, retriesPerUnit? }`
  → plan (units, depth, lanes) + per-unit results + consolidated report

Deterministic by default (heuristic unit evaluator); swap `IWorkUnitEvaluator`
for an AI-backed implementation without touching the executor.

## CI/CD prompt pipeline (continuity)

Every run is part of a continuous loop per target:

- findings get stable **fingerprints** (target + file + rule + title hash),
  remembered across runs in a persisted registry (`data/continuity.json`)
- **new** findings appear once; **persisting** ones escalate (+15%/run, cap +45%)
- findings marked **fixed** via feedback are verified by the next run; if they
  reappear they become **REGRESSIONS** (1.5× boost, forced to the top of scope)
- findings marked **wont_fix** are suppressed from future prompts
- each generated prompt chains with the previous run: continuity summary,
  verification requirements, and next steps with the feedback endpoint
- a CI pipeline gates a build on `continuity.regressions == 0`

### HTTP API (for CI pipelines)

| Endpoint | Use |
|----------|-----|
| `POST /api/validate` | run a validation; body `{ sourceType: website\|github\|zipArchive, sourceRef, scopeLimit, focus, notes }` → runId, continuity diff, chained prompt, findings with fingerprints (zip source: sourceRef = base64 ZIP) |
| `GET /api/runs/{runId}` | stored run (prompt + findings + continuity) |
| `POST /api/runs/{runId}/feedback` | `{ feedback: [{ fingerprint, status: fixed\|wont_fix\|failed_verification, note }] }` — reports the outcome of applying the prompt |
| `GET /api/targets` | all targets with run counts and pending fix verifications |
| `GET /api/targets/{targetKey}/history` | full cross-run finding timeline for a target |
| `GET /api/health` | pipeline health |

Optional shared-secret guard: set `SmartAgent:ApiKey` in configuration and send
it as `X-Api-Key` on every call.

### Example: GitHub Actions step

```yaml
- name: SmartAgent validation
  run: |
    RESULT=$(curl -s -X POST $SMARTAGENT_URL/api/validate       -H "X-Api-Key: $SMARTAGENT_KEY" -H "Content-Type: application/json"       -d '{"sourceType":"github","sourceRef":"https://github.com/org/repo","scopeLimit":3,"focus":"stability"}')
    REGRESSIONS=$(echo "$RESULT" | jq '.continuity.regressions')
    echo "$RESULT" | jq -r '.prompt'
    if [ "$REGRESSIONS" != "0" ]; then echo "::error::regressions detected"; exit 1; fi
```

## Source inputs

| Mode | Input | What happens |
|------|-------|--------------|
| a. Source as ZIP | upload a `.zip` | Code/text files extracted (binaries, `node_modules`, `bin/obj` etc. skipped), capped at 400 files / 8 MB |
| b. Source from GitHub | repo URL (`https://github.com/owner/repo`, optional `/tree/branch`) | Default-branch tarball fetched and extracted |
| c. App from website URL | live URL | Same-site crawl (up to 12 pages), HTML captured for behavior analysis |

## Project layout

```
SmartAgent.sln
src/
  SmartAgent.Domain           models: SourceSnapshot, Finding, RunOptions, PromptRunResult
  SmartAgent.SourceProviders  ZIP / GitHub / Website providers (ISourceProvider)
  SmartAgent.Integrations     AI processing provider (OpenAI-compatible) + reasoning-site endpoints
  SmartAgent.Core             HeuristicAnalyzer, Prioritizer, PromptBuilder, SmartAgentEngine
  SmartAgent.Web              ASP.NET Core 8 MVC UI (upload form, result view, .txt download)
tests/
  SmartAgent.Tests            xUnit tests (prioritization, scoping, analyzer, prompt building, URL parsing)
```

## The scope contract

One run never tries to fix everything:

- `ScopeLimit` (1–8) — how many findings are consolidated into the run's prompt
- `Focus` — *Balanced*, *Functionality* (boost functionality improvements) or
  *Stability* (boost erratic-behavior findings)
- findings are de-duplicated, scored (`severity × category × focus boost ×
  reasoning nudge`), and only the top slice enters the prompt
- operator notes are carried through to the prompt

## Integrations

### AI processing provider (prompt consolidation)
OpenAI-compatible (`/chat/completions`) — works with OpenAI, Azure OpenAI,
OpenRouter, Ollama, etc. Configure in `appsettings.json`:

```json
"SmartAgent": {
  "Ai": {
    "BaseUrl": "https://api.openai.com/v1",
    "ApiKey": "<your key>",
    "Model": "gpt-4o-mini"
  }
}
```

If the provider is not configured or unreachable, SmartAgent falls back to a
deterministic template — a run never fails because AI is down.

### Smart logic / reasoning websites (enrichment)
Any number of endpoints, called in parallel with the current findings.
Response contract:

```json
{ "notes": [ { "findingId": "F001", "note": "known regression in 2.3", "severityAdjust": 1 } ] }
```

```json
"SmartAgent": {
  "ReasoningSites": [
    { "Name": "my-reasoning-service", "Endpoint": "https://logic.example.com/api/review", "ApiKey": "", "TimeoutSeconds": 10 }
  ]
}
```

Unreachable sites degrade to warnings — the run continues.

## SAAEL — Autonomous Agile Engineering Lifecycle (AI role agents + human gates)

The orchestrator layer turns the prompt pipeline into a human-in-the-loop engineering
lifecycle: **AI performs work; humans own decisions.**

```
idea → BA decomposition → sprint proposal (PM+PO approve)
     → change proposal (Dev Mgr accepts) → QA scenarios → build/CI analysis
     → QA gate → business validation → staging → UAT
     → release readiness → 3-role release authorization → production
     → telemetry → learning agent → next sprint
```

Role agents (deterministic, auditable): `BaAgent` (stories + ambiguity detection),
`PmAgent` (sprint proposal), `DeveloperAgent` (change proposal — never direct code),
`QaAgent` (6 scenario categories + defect replay), `CiAgent` (build-failure root cause),
`LearningAgent` (data-driven retrospectives).

Safety model:
- **Human gates** — BA, Dev Manager, Developer code review, QA Manager, Business User,
  PO, and a 3-role release authorization (PO + QA Manager + Implementation Coordinator)
  are required at every automation level.
- **Automation levels L1–L4** — technical steps auto-execute at L3+; high-impact release
  decisions never auto-execute; policy rollback (auto-rollback on >5% error rate) only at L4.
- **Governance audit trail** — every agent action records agent id, input/output, tokens,
  and human reviewer; traceability links requirement → story → code → test → release → telemetry.
- **Token budget** — agent work is metered; when the budget is exhausted, agents are
  demoted to recommendation-only and nothing auto-executes.
- **CSRF/antiforgery** on all UI actions; role enforcement on every approval.

### HTTP API

| Endpoint | Purpose |
|---|---|
| `POST /api/saael/idea` | business idea → stories + ambiguities + sprint proposal |
| `POST /api/saael/sprint-decision` | human PM+PO decision on the sprint proposal |
| `POST /api/saael/resolve-ambiguity` | human BA resolves an ambiguity (gate precondition) |
| `POST /api/saael/advance` | attempt the next lifecycle transition (returns gate state) |
| `POST /api/saael/approve` | record a human decision on an approval request |
| `POST /api/saael/proposal/{id}` | Developer Agent change proposal (proposal only) |
| `POST /api/saael/change-decision/{id}` | human Development Manager decision |
| `POST /api/saael/qa-scenarios/{id}` | QA Agent scenario generation |
| `GET  /api/saael/readiness/{id}` | release-readiness report (checks + outstanding approvals) |
| `POST /api/saael/ci-analyze` | build failure → root-cause analysis |
| `POST /api/saael/rollback` | human rollback, or policy rollback at L4 only |
| `GET  /api/saael/retrospective` | Learning Agent insights |
| `GET  /api/saael/story/{id}` | story state, approvals, trace, governance |
| `GET  /api/saael/token-usage` | token ledger per agent |

UI demo: `/Home/Saael` walks an idea through the full lifecycle with every human
decision recorded.

### Browser App Analysis (BROWSER-Agent)

The agent can open a browser-style fetch of any live page — **localhost included** —
analyze it against the focus areas named in your prompt, and convert every finding
into a backlog story with a chained prompt that feeds the CI/CD pipeline.

```bash
curl -X POST /api/saael/analyze-app \
  -d '{"url":"http://localhost:5199/","prompt":"focus on accessibility, security of navigation, and forms"}'
```

Safety model:
- **SSRF-safe URL guard** — http/https only; `javascript:`, `data:`, `file:`, `ftp:` and
  URLs with embedded credentials are rejected before any fetch.
- **Bounded fetch** — 10s timeout, 2 MB analysis cap, no cookie jar, no script execution.
- **Focus-driven analysis** — findings matching your prompt's focus areas are flagged (★);
  checks cover accessibility, security (inline handlers, mixed content, `javascript:` links,
  unsafe `target=_blank`), navigation (dead links, nav landmarks), forms (labelless inputs,
  missing validation), performance (script count, viewport) and SEO/content.
- **Stories, not actions** — findings become backlog stories (security findings are High
  risk, so human gates stay mandatory). Nothing is fixed without the full human-gated lifecycle.
- Chained prompts are ready to run through the continuity-gated CI/CD pipeline.

UI: `/Home/AnalyzeApp`.

### Automatic parallel prompt resolution (no opt-in)

**Every** prompt the engine builds — regardless of source or endpoint — is automatically
reviewed, split into independent work-unit threads, processed concurrently, and
consolidated into a single report before the run completes. There is no flag to set.

How it works per run:
1. **Review** — the built prompt is decomposed into work units (scope), each with a
   depth estimate (1–5) and relative cost.
2. **Split** — units are distributed across balanced parallel lanes; the thread width is
   `min(units, MaxThreads)` (default: CPU count, min 2; bound it per request with `maxThreads`).
3. **Process** — lanes run concurrently; per-unit timeout, one retry, failures reported,
   never swallowed.
4. **Consolidate** — results merge into one report: units, threads, failures, total effort,
   dominant risk, and a scope-satisfied check (every unit accounted for).

The prompt itself carries the outcome as an `# Execution` statement, so any coding agent
reading it knows how it was processed:
- multiple items → "resolved by the agent into N work items and processed in parallel
  on W thread(s) across V wave(s)"
- one task → "reviewed and resolved by the agent as a single task item".

Every validate response carries it:

```json
"parallel": { "autoParallel": true, "units": 12, "threads": 4, "unitsFailed": 0,
              "scopeSatisfied": true, "dominantRisk": "Low", "totalEffort": 232, "elapsedMs": 0 }
```

Live-verified against the repo itself: 26 findings → scoped prompt → 12 work units
across 4 threads, 0 failures, scope satisfied. Standalone use (`any prompt`, no source
needed) stays available at `POST /api/evaluate-prompt`.

## PM capability — multi-year program generation (`/Pm`)

The agent captures all resources involved in project development through the
standard RACI chart (Product Owner, Scrum Master, Solution Architect, Dev
Lead, Developers, QA, DevOps, DBA, UI/UX, BA, Data Engineer, Tech Writer
across the nine agile delivery phases), then processes every planning request
on **one spawned thread per project role** (ThreadGovernor lanes).

**Flow:** GitHub source (any .NET repo) or a prompt → agile requirement
capture (epics → user stories with acceptance criteria, points, priority,
risk) → multi-year program split (2-week sprints, quarters, releases) with
squad domains, RACI resource engagement and budget management.

```json
POST /api/pm/program
{
  "name": "Agile Delivery Programme", "gitHubUrl": "https://github.com/owner/repo",
  "years": 2, "teamSize": 24, "budgetCapPerYearUsd": 500000, "velocityPerDev": 10
}
```

- `POST /api/pm/program` — generate + persist a program plan (JSON)
- `GET /api/pm/programs` / `GET /api/pm/programs/{slug}` — list/load plans
- MVC PM Hub (user interaction): `/Pm` (plan form driving budget/team/years)
  → `/Pm/Hub?slug=...` renders KPIs, delivery phases, quarter schedule with
  releases, sprint-level task management, squads, the requirement backlog,
  the team RACI chart and budget rollup per role.

**Deep source review** (any GitHub repo): the analyzer resolves the real
module inventory — controllers, domain entities, services, Hubs, UI files,
database scripts — and each module's stories are grounded in its actual
classes, files and endpoints.

**Commit-history pattern review**: for GitHub sources the provider also
captures recent commits with their touched paths. Recurring
(module, layer) change patterns become Year-2 enhancement stories, so the
plan stretches across the horizon with change waves the repository itself
demonstrated ("Add AI-powered scripture discovery", "Fix gathering sequence
builder", ...).

**Architecture layer taxonomy**: every source path classifies into one of
five layers — Database, Data Adapter, Controllers, Frontend, Server — and
every story carries its layer. The backlog breaks each module into these
layers (server-side domain, data adapter & persistence, controllers &
actions, frontend screens, database scripts), so a sprint shows exactly
which frontend/server/database components change together.

**Phase-banded planning**: sprints resolve into four explicit phases:
1. *framework* — base framework code generation (scaffold, auth, tooling)
2. *build* — basic module functionality in vertical slices: one module's
   database + server + controllers + frontend changes land in the same
   sprint wave
3. *enhance* — field extensions (new fields end to end: DB column + server
   validation + form input) and history-detected change patterns
4. *release* — hardening, security review and the GA train

Budget model: each resource's engagement is RACI-weighted (R=1.0, A=0.5,
C=0.25, I=0.05 across phases) and priced at blended role rates per sprint;
annual caps flag overruns. Every 6th sprint is a 50%-capacity stabilization
sprint. Role-threaded deliverables and the ThreadGovernor consolidation
report (.dat) are archived with each program.

## DAT framework — ThreadGovernor, ObjectBank, SubjectBank

The DAT layer adds file-backed (.dat) primitives for multi-threaded task
orchestration and OO classification alongside the prompt pipeline.

### ThreadGovernor (`/api/dat/distribute`)

A memory-managed, priority-aware work-queue governor. It creates a bounded
pool of worker lanes, processes high-priority tasks first, measures per-task
and per-lane memory allocation, recycles lanes that become memory-heavy,
spawns replacement lanes while work remains, redistributes failed tasks, and
consolidates the run into a CSV-style .dat report.

```json
POST /api/dat/distribute
{
  "subject": "demo",            // consolidation file subject
  "taskCount": 24, "maxThreads": 8, "workMs": 10,
  "failFirstAttempts": 6,       // tasks that fail once (tests redistribution)
  "prioritize": true,           // earlier tasks get higher priority
  "memoryHogKb": 300,           // each task allocates this much (simulates heavy work)
  "laneMemoryBudgetKb": 500,     // per-lane allocation budget; crossing it recycles the lane
  "totalMemoryBudgetKb": 4096   // run-wide budget; spawning stops beyond it
}
```

**Priority processing.** Every task carries a `Priority` (higher first,
FIFO within a level). With `prioritize: true` the demo assigns descending
priorities so T0001 is processed first.

**Memory management.** Allocation is measured per task and per lane with
`GC.GetAllocatedBytesForCurrentThread()` and reported as `AllocatedBytes`
(run total and per task). Two budgets apply:

- `laneMemoryBudgetBytes` — a high-memory lane crossing its budget is
  GC-trimmed and recycled; the supervisor spawns a fresh replacement lane
  so throughput is unaffected. Recycles are counted as `LanesRecycled`.
- `totalMemoryBudgetBytes` — once the run-wide budget trips, the governor
  trims once and stops spawning new lanes; existing lanes keep draining.

**Adaptive spawning.** A supervisor grows the pool toward the backlog
(`backlog / TasksPerLane`, capped by `maxThreads` and the memory budget), so
lanes are spawned only when they speed processing up. Live check: 100 x 25ms
tasks ran on 12 grown lanes in 231ms vs ~2500ms sequential.

Every task is named after a SubjectBank formula (round-robin), evaluated with
random inputs. A task that throws is re-queued once and retried on another lane
(attempts=2); genuinely failing tasks end as `failed` with the error archived.
The consolidation report is written to `data/dat/consolidations/<subject>.dat`
(header comments with run totals incl. LanesSpawned / LanesRecycled /
AllocatedBytes, then one CSV row per task: TaskId, Subject, Priority, Status,
Attempts, ThreadId, Lane, ElapsedMs, AllocatedBytes, Output) and returned in
the response. `Threads` in the report is the peak number of active lanes.

### ObjectBank (`/api/dat/classes`, `/api/dat/classes/rebuild`)

OO classification stored as a pipe-delimited class catalog in
`data/dat/classbank.dat`. Each row is one class definition:

`ClassName | Namespace | Kind | Access | IsAbstract | IsSealed | IsStatic | BaseClass | Interfaces | Properties | Methods | Constructors | Events | SourceFile`

- `POST /api/dat/classes` upserts (append new, replace in place on update)
- `POST /api/dat/classes/rebuild?maxThreads=N` parses the catalog on a
  thread pool and materializes each class as a typed object graph (property
  types resolved against other banked classes, unresolved types reported as
  `missingTypes`), keyed by class name.

### DbSchemaBank (`/api/dat/schema`)

Database entities — tables, views, indexes, clustered indexes, clusters,
partitions, primary/foreign/unique keys — stored as a pipe-delimited catalog in
`data/dat/dbbank.dat` and resolved into key-based data models:

`Key|Kind|Schema|Name|Identifier|Columns|IndexColumns|IsClustered|PartitionScheme|Range|Parent|References`

- `GET /api/dat/schema` lists every banked entity grouped by kind
- `POST /api/dat/schema` upserts an entity (kind accepts table, view, index,
  clusteredindex, cluster, partition, primarykey/pk, foreignkey/fk, uniquekey/uk)
- `POST /api/dat/schema/rebuild` resolves the catalog into linked data models
  on threaded lanes: entities are sorted by identifier, split into contiguous
  ranges and resolved in parallel into a shared key-based hash dictionary.
  Keys, indexes and partitions attach to their parent models, foreign keys
  resolve to target model keys, and orphans (unbanked parents) are reported.
  Optional `fromIdentifier` / `toIdentifier` process only an identifier range.
  Tables/views resolve typed-defaulted columns (`Id:Int,Name:String`).

The bank seeds a sample vehicle-fleet schema (Vehicles, Drivers, PK/clustered
and non-clustered indexes, FK, unique key, cluster, partition, view) on first
use. `dbbank.dat` is excluded from subject scanning.

### SubjectBank (`/api/dat/subjects`, `/api/dat/evaluate`)

Subject-specific formula banks: one `.dat` file per subject
(`math.dat`, `physics.dat`, `chemistry.dat`) with pipe-delimited rows:

`FormulaName | Expression | Variables | Units`

The built-in `FormulaEvaluator` is a recursive-descent parser supporting
`+ - * / % ^`, parentheses, unary minus, and the functions `sin cos tan asin
acos atan sqrt abs log ln exp floor ceil round min max` plus `pi` and `e`
constants. Variable lookup is case-insensitive.

- `GET /api/dat/subjects/{subject}` lists formulas
- `POST /api/dat/subjects` upserts a formula (CSV escaping for commas in fields)
- `POST /api/dat/evaluate` evaluates a formula:

```json
POST /api/dat/evaluate
{ "subject": "physics", "formulaName": "KineticEnergy", "variables": { "m": 2, "v": 3 } }
// -> { "value": 9, "units": "Joules" }
```

`GET /api/dat/status` summarizes bank contents and the governor's latest run.
On first use the banks seed default subjects (math, physics, chemistry) and
`classbank.dat` is excluded from subject scanning.

## Build & run

```bash
dotnet build SmartAgent.sln -c Release
dotnet test SmartAgent.sln -c Release          # 19 tests
dotnet run --project src/SmartAgent.Web          # http://localhost:5000
```

.NET 8 SDK required. No database needed — run results are held in a bounded
in-memory store (last 50 runs).
