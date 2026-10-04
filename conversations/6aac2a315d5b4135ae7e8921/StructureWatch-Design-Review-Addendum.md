# StructureWatch Guard — Design Review & Enhancement Addendum

Companion to "Building Structure Geometry & Change-Monitoring Enhancement".
Verdict: design is sound and production-oriented. Items below are corrections
(A), algorithm enhancements (B), API/daemon hardening (C), ops/security (D),
testing (E), and a revised AI implementation prompt (F).

---

## A. Schema corrections and additions

### A1. BUG — unusable unique constraint (§7)
The doc states: unique constraint on `(CityStructureId, StructureExternalId, Provider)`.
`CityStructureId` is already the PK, so this constraint adds nothing and does not
prevent the same external building being imported twice.

**Fix:** unique constraint on `(CityId, Provider, StructureExternalId)` on
`CityStructures`. This is what actually maps one provider building to one row
per city. Keep `(CityId, StructureName)` as a *soft* dedup hint only.

### A2. Missing `ComparisonRuns` table (§6)
`StructureChangeEvents.ComparisonRunId` has no parent table. `IngestionRuns`
covers imports, not comparisons. Add:

```
ComparisonRuns
  ComparisonRunId PK
  CityId FK NULL
  DaemonOrManual (Daemon/Manual)
  StartedUtc, CompletedUtc, Status
  MetricCrs (SRID + name used for deltas this run)
  ConfigSnapshotJson (all thresholds & k used at run time)
  StructuresCompared, VerticesCompared
  ChangesDetected, ChangesSuppressedByUncertainty
  ErrorCount, LogReference
```

Run-config snapshots make every stored delta reproducible and auditable later,
after thresholds are changed.

### A3. Missing city discovery geometry (§6)
Discovery takes a bounding box, but `Cities` stores only a center point. Add:

```
Cities + DiscoveryBoundaryGeography (polygon, optional)
Cities + DiscoveryMinLat/MinLon/MaxLat/MaxLon (bbox, required for discovery)
```

### A4. Height provenance is missing — the 100 m gate rests on shaky data
OSM `height=` tags are sparse; floors are often missing too. A structure whose
height is unknown cannot be reliably excluded or included by a 100 m threshold.

```
CityStructures + HeightSource (OsmTag/Elevation/FloorsEstimate/Official/Customer)
CityStructures + HeightConfidence (0–1)
CityStructures + HeightMeasuredUtc
Status enum + "Candidate" (height uncertain; included provisionally, badged in UI)
```

Qualification rule: `HeightM ≥ threshold` when confidence ≥ configurable
minimum; else mark Candidate. Fallback estimator: floors × 3.0 m, confidence 0.3.

### A5. Vertex correspondence across versions — persist the match (§10)
"Geometry-nearest matching when the polygon node count changes" is the hardest
part of the pipeline and currently leaves no trace. When OSM remaps a building
from 5 nodes to 8, per-vertex deltas are meaningless noise. Record the match:

```
VertexCorrespondences
  CorrespondenceId PK
  ComparisonRunId FK
  OriginalVertexId FK, UpdatedVertexId FK (both nullable)
  MatchMethod (ExactSequence/Nearest/HomologousEdge/None)
  MatchConfidence
```

Then classification can branch honestly: matched pairs get deltas;
unmatched/unstable pairs produce a "Topology Change" classification, not fake
millimetre numbers.

### A6. Change-event idempotency
A re-run of the same comparison must not duplicate events. Natural key on
`StructureChangeEvents`:

```
UNIQUE (ComparisonRunId, StructureVertexId, ChangeClassification)  — or —
UNIQUE (StructureVertexId, OriginalVersionId, UpdatedVersionId)
```

Prefer the second: it makes the daemon idempotent across re-scheduling, not
just within one run.

### A7. Baseline transitions need a workflow, not just a rule (§16)
"Record all baseline replacements as an explicit reviewed version transition"
needs a home. Since Original stays immutable forever, use promotion:

```
StructureGeometryVersions + IsReference BIT (the currently-authoritative geometry)
BaselineTransitions
  TransitionId PK, CityStructureId FK
  FromVersionId FK, ToVersionId FK
  Reason (AcceptedSourceUpdate/RebaseAfterTopologyChange/SurveyCorrection)
  ReviewedBy, ReviewedUtc, ReviewerNotes
```

Policy: Original is never mutated and never loses `VersionType = Original`.
Validation decision "Accept as source update" creates a BaselineTransition and
sets `IsReference = true` on the accepted version. All comparisons run
Reference → Latest Updated. This resolves the ambiguity of "compare original vs
updated" after two accepted updates.

### A8. Validation queue hygiene
Add to `ValidationCases`: `Priority`, `DueByUtc` (SLA), `QueuePosition` ordering
key, and `DetectionAgeDays` as a computed/report column. Stale-source cases
(`MaximumSourceAgeDays`) should auto-close with a "Source Refreshed" disposition
when fresh data arrives and the delta falls within uncertainty.

### A9. Geometry hash must be defined (§10)
"Verify geometry hashes" is meaningless unless the hash is specified. Define:

> SHA-256 over the normalized vertex sequence: metric CRS, coordinates rounded
> to 1 mm, closing duplicate point removed, ordered by VertexSequence, one
> line per vertex formatted `seq;x;y;z`, empty Z as `*`.

This makes the hash stable across CRS re-projection round-trips and immune to
closing-point and precision noise. Store the hash *algorithm version* alongside
the hash.

### A10. SRID discipline for `geometry` columns
`CoordinateGeometry`/projected columns must be created with an explicit SRID
and all rows in a comparison must share it; SQL Server spatial indexes are
per-SRID and silently return empty on SRID mismatch. Add a CHECK-documented
convention: WGS84 geography = SRID 4326; metric geometry = the UTM zone SRID
derived from the city centroid (store it on `Cities.MetricSrid`), not per-row
guesses.

### A11. Index plan (add to §6)
- `CityStructures`: `(CityId, Status) INCLUDE (HeightM, CurrentVersionId)`
- `CityStructures.CenterGeography`: spatial index
- `StructureVertices`: `(GeometryVersionId, VertexSequence)` (also backs the §7 unique constraint)
- `StructureChangeEvents`: `(CityStructureId, DetectedUtc DESC)`; consider
  monthly partitioning — this is the one table with unbounded growth
- `ValidationCases`: `(Status, Priority, DueByUtc)`

### A12. Retention vs immutability conflict (§12)
`RetainGeometryVersions` must explicitly never delete Original versions, the
current Reference version, or the latest Updated version. State that retention
prunes only superseded Updated versions that are not referenced by an open
ValidationCase or BaselineTransition.

---

## B. Comparison-algorithm enhancements

### B1. Define the uncertainty budget (§11 currently hand-waves it)
```
U = sqrt( A_original² + A_updated² + A_registration² )
```
where `A_*` are the stored HorizontalAccuracyM values and
`A_registration` (default 0.5 m for OSM-vs-OSM, configurable per provider pair)
covers re-registration between captures.

Alert rule: material only when
```
Δ > max( HorizontalAlertThreshold, k × U ),  k default 3
```
Everything else is "Within Source Uncertainty". Store `U`, `k`, and the
classification inputs on each event — reviewers need to see *why* an alert fired.

### B2. Rigid-body shift test — the biggest false-positive killer
Map revisions and imagery re-registration move **all vertices of a polygon by
nearly the same vector**. Real structural movement does not. Before per-vertex
classification:

1. Compute mean displacement vector `V̄` over matched, high-confidence pairs.
2. Residuals: `rᵢ = Δᵢ − V̄`.
3. If `|V̄|` is large but `max|rᵢ| ≤ max(threshold, k × U)` → classification
   **Georeferencing Shift**, no structural alert; recommend a BaselineTransition
   (AcceptedSourceUpdate: "map re-registration").
4. If residuals exceed the budget anywhere → per-vertex classification as
   designed, plus an optional Helmert (similarity) transform estimate for the
   trend view.

This one test eliminates most "the whole building moved 0.7 m" phantom alerts
that OSM re-surveys produce.

### B3. Topology-change branch (uses A5)
If vertex counts differ, or < 80 % (configurable) of vertices match with
confidence ≥ minimum → skip per-vertex deltas entirely; emit one
**Topology Change** classification with a re-baseline recommendation. Never
report millimetre deltas across a remapped polygon.

### B4. Vertical deltas: default OFF for non-survey sources
USGS 3DEP is terrain (bare-earth DTM); DSM-derived tops are building *height*
evidence with metre-class accuracy. Neither can produce defensible mm vertical
movement of a structure. Rule: vertical alerting is enabled only when both
versions' `ProviderType = Survey/Monitoring`. For map/satellite sources compute
ΔZ for the record, but classify vertical movement as "Not Assessable" and
suppress vertical alerts. Keep `VerticalAlertThresholdMm` for survey sources.

### B5. Trend / velocity across versions (§11 mentions trends, schema doesn't support reporting them)
With multiple Updated versions, fit per-vertex linear regression of position vs
`CapturedUtc` → velocity in mm/year with an uncertainty band. Surface in the
structure inspector as a sparkline; open a ValidationCase when the velocity
band's lower bound exceeds the threshold sustained across N runs (configurable,
default 3). This catches slow drift that no single-pair comparison would.

### B6. Classification taxonomy (gives the queue a controlled vocabulary)
`No Change` · `Within Source Uncertainty` · `Georeferencing Shift` ·
`Topology Change` · `Vertex Redistribution` (matched, sub-threshold, non-rigid) ·
`Material Movement (Candidate)` · `Source Stale` · `Not Assessable (vertical)`.
Map statuses/filters in §4.1 to this taxonomy.

---

## C. API and daemon hardening

### C1. Long-running endpoints must be asynchronous
`POST /api/structures/discover` and `POST /api/jobs/run-now` can run minutes.
Return **202 Accepted + jobId** with a `Location: /api/jobs/{jobId}` header;
poll `GET /api/jobs/status`. Support an `Idempot-Key` header (stored on
IngestionRuns) so a retried request never double-imports.

### C2. Concurrency control
"Compare Now" (manual) can race the daemon on the same structure. Take a
per-structure application lock (e.g., `sp_getapplock` on
`structure:{id}`) for the comparison transaction; the loser returns 409 with
"comparison already in progress", which the UI shows non-fatally.

### C3. Daemon liveness, made checkable
Add a `DaemonHeartbeats` row (or single-row table): `LastHeartbeatUtc`,
`LastRunStartedUtc`, `Version`. `GET /api/jobs/status` computes health as
`LastHeartbeatUtc within 2 × configured interval`. The doc says "alert on daemon
inactivity" — this gives the alert something concrete to read.

### C4. Overpass etiquette and incremental discovery
Full-city building imports are heavy and rate-limited. Enhancements:
partition discovery bboxes to ≤ ~0.05°; use Overpass `[out:json][timeout:90]`
with a per-city cursor (last discovery time) and the `newer` filter to fetch
only changed OSM objects on subsequent runs; cache raw responses for the
provider's stated freshness; back off on 429/504 with jittered retry.

### C5. Migration ownership
Two deployables share one database. State explicitly: the **web application
owns EF Core migrations**; the daemon ships as a migration bundle consumer and
never generates migrations. Otherwise schema drift between the two is inevitable.

---

## D. Security and operations

- **EvidenceUri hygiene:** never persist signed URLs (they expire and leak
  access). Store stable provider object references; mint short-lived links at
  read time. Strip query tokens on ingest.
- **Audit integrity:** keep `AuditEvents` append-only (no UPDATE/DELETE grants
  to the app role). Optional: hash-chain rows (`Hash = SHA256(prevHash || row)`)
  for tamper evidence.
- **Observability:** structured logs as designed, plus OpenTelemetry metrics:
  `run_duration`, `structures_compared`, `changes_by_classification`,
  `provider_latency`, `provider_error_rate`, `queue_depth`, `heartbeat_age`.
- **Roles:** as designed; add policy that only `Administrator` can alter
  thresholds and execute BaselineTransitions; `Reviewer` decides validation
  cases; `Data Operator` runs discovery/comparison; `ReadOnly` reads.

---

## E. Testing enhancements (beyond "unit tests")

1. **Golden displacement fixtures:** synthetic building polygons with injected
   shifts (10 mm, 50 cm, 2 m), run through the real comparison pipeline;
   assert exact classification per B6 taxonomy.
2. **Rigid-shift fixture:** translate all vertices by 1.5 m → must classify
   Georeferencing Shift, zero Material Movement events.
3. **Topology-change fixture:** 5-node → 8-node remap of the same footprint →
   Topology Change, no per-vertex deltas, re-baseline recommendation.
4. **CRS round-trip:** WGS84 → UTM → WGS84 round-trip within 1 mm on
   randomized city coordinates (catches SRID and axis-order bugs).
5. **Uncertainty gate:** source accuracy 5 m, injected 2 m shift → suppressed
   (Within Source Uncertainty); same shift with survey source (accuracy 5 cm)
   → Material Movement Candidate.
6. **Idempotency:** run the same comparison twice → identical event count.
7. **Normalization invariants (property tests):** closing point never
   double-counted; ≥ 3 distinct vertices enforced; vertex sequence contiguous.

---

## F. Revised AI implementation prompt (replaces §13 — additions in requirements 19–27)

> You are the senior architect/developer enhancing the existing StructureWatch
> Guard application (https://aisstructureguardwatch.ai.studio), with the related
> SatViewGuard application (https://satviewguard.base44.app). Build a
> production-oriented ASP.NET Core C# MVC enhancement named Building Structure
> Geometry Monitoring.
>
> OBJECTIVE — as §13, plus: persist vertex-correspondence decisions and
> comparison-run configuration so every stored delta is reproducible.
>
> REQUIREMENTS 1–18 — unchanged from the original design document.
>
> 19. Add a ComparisonRuns table (run metadata, metric CRS used, and a JSON
>     snapshot of every threshold and uncertainty factor used during the run).
> 20. Fix the structure dedup unique constraint to (CityId, Provider,
>     StructureExternalId).
> 21. Persist vertex correspondences (match method, confidence) between the
>     reference and updated geometries; never compute deltas across unmatched
>     vertices.
> 22. Implement the uncertainty budget U = sqrt(A_orig² + A_upd² + A_reg²)
>     and gate alerts on Δ > max(threshold, k·U), k configurable (default 3).
>     Store U and k on each change event.
> 23. Implement the rigid-body shift test: a uniform displacement of all matched
>     vertices within residual tolerance classifies as Georeferencing Shift, not
>     structural movement, and offers a reviewed baseline transition.
> 24. If vertex count differs or match confidence is below the configured
>     minimum, classify as Topology Change and recommend re-baselining; do not
>     emit per-vertex deltas.
> 25. Disable vertical movement alerting for non-survey sources; classify
>     vertical movement as Not Assessable when either version comes from
>     map/satellite/elevation data.
> 26. Add height provenance (HeightSource, HeightConfidence) and a Candidate
>     status for structures whose height evidence is below the confidence
>     minimum; qualification at the height threshold must respect it.
> 27. Implement baseline transitions as explicit, reviewed, audited records
>     (FromVersion, ToVersion, Reason, Reviewer); the immutable Original is
>     never mutated and comparisons always use the designated Reference version.
>
> DELIVERABLE — as §13, plus: the classification taxonomy with all eight
> classes, the golden-fixture test set from the design addendum, and the
> daemon liveness/heartbeat contract used by /api/jobs/status.

---

## G. Acceptance criteria (additions to §18)

- The same comparison executed twice produces identical events (idempotency).
- A synthetic rigid translation of the whole polygon never raises a structural
  movement alert; it raises a Georeferencing Shift with a one-click reviewed
  baseline transition.
- A remapped polygon (different node count) produces a Topology Change and no
  per-vertex deltas.
- Every change event stores its uncertainty budget U, k, thresholds, and the
  comparison run that produced it.
- Vertical movement is never alerted from map/satellite/elevation sources.
- A structure with low-confidence height appears as Candidate, not silently
  excluded or included.
- The daemon heartbeat is visible in /api/jobs/status and drives the
  inactivity alert.
