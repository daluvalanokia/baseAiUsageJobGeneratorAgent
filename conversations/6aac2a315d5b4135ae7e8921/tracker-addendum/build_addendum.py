#!/usr/bin/env python3
"""Renders the StructureGuard EyeWatch tracker-network addendum PDF."""
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.enums import TA_LEFT
from reportlab.platypus import (SimpleDocTemplate, Paragraph, Spacer, Table,
                                TableStyle, PageBreak, KeepTogether, HRFlowable)

ACCENT = colors.HexColor("#0f4c81")
LIGHT = colors.HexColor("#e8eef5")
MUTED = colors.HexColor("#555555")

styles = getSampleStyleSheet()
H1 = ParagraphStyle("H1", parent=styles["Heading1"], fontSize=17, textColor=ACCENT,
                    spaceBefore=6, spaceAfter=8, leading=21)
H2 = ParagraphStyle("H2", parent=styles["Heading2"], fontSize=13, textColor=ACCENT,
                    spaceBefore=14, spaceAfter=6, leading=16)
H3 = ParagraphStyle("H3", parent=styles["Heading3"], fontSize=11, textColor=colors.HexColor("#23303d"),
                    spaceBefore=10, spaceAfter=4)
BODY = ParagraphStyle("BODY", parent=styles["BodyText"], fontSize=9.2, leading=13.2,
                      alignment=TA_LEFT, spaceAfter=5)
SMALL = ParagraphStyle("SMALL", parent=BODY, fontSize=8.2, leading=11, textColor=MUTED)
CELL = ParagraphStyle("CELL", parent=BODY, fontSize=7.9, leading=10.4, spaceAfter=0)
CELLB = ParagraphStyle("CELLB", parent=CELL, fontName="Helvetica-Bold")
MONO = ParagraphStyle("MONO", parent=CELL, fontName="Courier", fontSize=7.4, leading=10)

def P(t, s=BODY): return Paragraph(t, s)

def tbl(headers, rows, widths=None, mono_col=None):
    head = [Paragraph(h, CELLB) for h in headers]
    body = []
    for r in rows:
        body.append([Paragraph(c, MONO if (mono_col is not None and i in mono_col) else CELL)
                     for i, c in enumerate(r)])
    t = Table([head] + body, colWidths=widths, repeatRows=1)
    t.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), ACCENT),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, LIGHT]),
        ("GRID", (0, 0), (-1, -1), 0.4, colors.HexColor("#b9c6d3")),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 4),
        ("RIGHTPADDING", (0, 0), (-1, -1), 4),
        ("TOPPADDING", (0, 0), (-1, -1), 2.5),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 2.5),
    ]))
    return t

def entity(title, note, rows):
    els = [P(title, H3), P(note, SMALL)]
    els.append(tbl(["Column", "Type / Values", "Description"],
                   rows, widths=[38*mm, 40*mm, 104*mm], mono_col={0}))
    els.append(Spacer(1, 6))
    return KeepTogether(els)

story = []

# ---------------------------------------------------------------- cover
story.append(P("StructureGuard EyeWatch — Design Addendum", H1))
story.append(P("Uniform Tracker Network for 3D Structure Enhancement, Movement Recording "
               "and External-Factor (Wind / Barometric / Seismic) Movement Correlation", H2))
story.append(tbl(["Field", "Value"], [
    ["Application", "https://aisstructureguardeyewatch.ai.studio"],
    ["Source repository", "github.com/daluvalanokia/aisstructureguardeyewatch"],
    ["Parent design", "StructureWatch Guard — Building Structure Geometry &amp; Change-Monitoring "
                      "Enhancement; Subsurface Design (vertical digital-twin via vertex duplication)"],
    ["Addendum status", "Proposed — extends the existing database, daemon, API and UI designs"],
    ["Date", "25 September 2026"],
], widths=[34*mm, 148*mm]))
story.append(Spacer(1, 8))
story.append(P(
    "This addendum introduces a physical tracking layer for the existing structure-inventory model. "
    "Small autonomous trackers (TOPFLYtech TLW2-12B 4G LTE Asset Tracker or a similar/compatible "
    "device class, including a barometric/wind-pressure-capable variant) are mounted at uniformly "
    "placed anchor points on a structure — sides, vertices and height stations — and periodically or "
    "on movement transmit high-precision coordinates and altitude to a registered central server, "
    "which records and feeds the data into the application. The readings enrich the 3D structure "
    "model and animation, record real movement events, and allow the effect of movement to be "
    "correlated with external factors such as wind pressure, barometric pressure and seismic activity. "
    "All new entities reference the existing CityStructureId / StructureVertexId keys so the addendum "
    "fits the existing design database without changes to it.", BODY))

story.append(P("Scope discipline inherited from the parent design", H2))
for b in [
    "<b>Provenance and uncertainty.</b> Every reading stores its source, trigger type and stated "
    "accuracy. A consumer 4G LTE asset tracker of the TLW2-12B class reports GPS-grade accuracy "
    "(metres). Where millimetre structural movement evidence is required, the same schema accepts "
    "survey-grade GNSS/RTK devices of the same form factor — the accuracy column drives the alert "
    "gating, exactly as the parent design gates satellite-imagery alerts by source accuracy.",
    "<b>Change-classification discipline.</b> Coordinate differences are classified as "
    "TRACKED_MOVEMENT (real displacement) vs SENSOR_ARTIFACT (GPS drift, multipath, battery brown-out) "
    "before any StructuralEvent is raised, mirroring the PHYSICAL_CHANGE vs DATA_REFINEMENT rule.",
    "<b>Immutable baselines.</b> The nominal (as-mounted) position of each anchor point is never "
    "overwritten; every reading is a separate observation, consistent with the Original-vs-Updated "
    "geometry versioning of the parent design.",
    "<b>Extensibility.</b> Environmental factors use an open reading-type catalogue so new external "
    "factors (temperature, tidal, traffic-induced vibration) can be added without schema changes.",
]:
    story.append(P(b, BODY))
story.append(PageBreak())

# ---------------------------------------------------------------- part A
story.append(P("Part A — Tracker Infrastructure Addendum", H1))

story.append(P("A.1 Concept", H2))
story.append(P(
    "Each monitored structure receives a set of uniformly placed trackers. A tracker is mounted at "
    "an anchor point identified by structure side (N/E/S/W or custom facade), vertex (existing "
    "StructureVertexId where the footprint perimeter defines it) and height station (a nominal "
    "mounting height above ground). The mount point stores its nominal coordinates and altitude at "
    "installation; trackers record on movement (built-in motion sensor) or on a default interval "
    "(e.g. every 30/60/120 minutes), and deliver position + altitude + status to the central server, "
    "which ingests the feed into the application database.", BODY))
story.append(P(
    "Because mount points reuse the structure's vertex axes, the tracker readings can be projected "
    "onto the same vertical digital-twin columns used by the subsurface design: surface position, "
    "foundation interface and subsurface intervals all share StructureVertexId as the common spatial "
    "axis, enabling full 3D enhancement and 4D animation of the structure above and below ground.", BODY))

story.append(P("A.2 Data flow", H2))
story.append(P(
    "Tracker device (GPS/GNSS + barometric/wind sensor) → 4G LTE (or equivalent) → "
    "<b>CentralServer</b> ingestion endpoint (device authenticated by tracker serial) → ingestion "
    "run recorded (<b>IngestionRuns</b>, reused) → readings written (<b>TrackerReadings</b>, "
    "<b>EnvironmentalReadings</b>) → change evaluation (displacement vs nominal, thresholds, "
    "uncertainty budget) → <b>StructuralEvents</b> / <b>ValidationCases</b> (reused) → UI dashboards "
    "and 3D/4D viewer. Device health (battery, signal, status) updates <b>TrackerDevices</b> and "
    "maintenance scheduling updates <b>DeviceMaintenance</b>.", BODY))

# entities
story.append(P("A.3 New database entities", H2))
story.append(entity("A.3.1 CentralServers — central server registry",
    "One row per ingestion server. The server 'can record and feed data to the application'.", [
    ["CentralServerId", "string PK", "Stable server identity; never changes."],
    ["ServerName / Location", "string", "Human-readable name and physical location/address."],
    ["Latitude / Longitude", "decimal(9,6)", "Server site coordinates (WGS84)."],
    ["EndpointUrl", "string", "Ingestion endpoint the trackers deliver to."],
    ["Protocol", "'HTTPS'|'MQTT'|'TCP'", "Transport used by the device fleet."],
    ["Status", "'online'|'offline'|'maintenance'", "Current server state."],
    ["CapacityTrackerCount", "int", "Maximum concurrent devices supported."],
    ["LastHeartbeatUtc", "datetime", "Last successful health ping."],
    ["Notes", "string", "Free-text operational notes."],
]))
story.append(entity("A.3.2 DeviceModels — device / capability catalogue",
    "Defines devices and capabilities: sensor range, charge duration, self-charge capability, device "
    "status signal and sensing abilities. Seeded with the TOPFLYtech TLW2-12B 4G LTE Asset Tracker "
    "and a barometric/wind-pressure-capable compatible model.", [
    ["DeviceModelId", "string PK", "Catalogue identity."],
    ["ModelName / Manufacturer", "string", "e.g. 'TOPFLYtech TLW2-12B 4G LTE Asset Tracker'."],
    ["Connectivity", "string", "'4G LTE' (also LTE-M / NB-IoT class devices accepted)."],
    ["GnssCapability", "'GPS'|'GPS+GNSS'|'RTK'", "Positioning class of the device."],
    ["StatedAccuracyM", "decimal", "Manufacturer-stated horizontal accuracy."],
    ["AltitudeCapability", "'GNSS'|'BAROMETRIC'|'NONE'", "How altitude is derived."],
    ["SensorRangeM", "decimal", "Rated sensor/measurement range in structure vicinity."],
    ["BatteryChargeDurationH", "int", "Rated charge duration in hours (standby/interval profile)."],
    ["SelfChargeCapability", "boolean", "True if self-charging (solar / vibration harvesting)."],
    ["StatusSignalType", "'RSSI'|'CSQ'|'SNR'", "Reported device status signal type."],
    ["BarometricPressureCapable", "boolean", "Part B capability flag."],
    ["WindPressureCapable", "boolean", "Part B capability flag (anemometric variant)."],
    ["MotionTriggerCapable", "boolean", "Can record on movement, not only on interval."],
    ["OperatingTempC / IngressRating", "string", "Environmental rating, e.g. IP65."],
    ["FirmwareNotes", "string", "Supported protocols, firmware constraints."],
]))
story.append(entity("A.3.3 TrackerDevices — physical tracker units",
    "One row per deployed device. Serial number is unique; association to a structure is a separate "
    "assignment so devices can be relocated.", [
    ["TrackerDeviceId", "string PK", "Device identity."],
    ["SerialNumber", "string unique", "Manufacturer serial / IMEI."],
    ["DeviceModelId", "string FK→DeviceModels", "Capability catalogue entry."],
    ["Status", "'active'|'inactive'|'maintenance'|'retired'|'deployed'", "Device status."],
    ["BatteryLevelPercent", "int", "Latest reported charge."],
    ["LastSignalUtc / LastReadingUtc", "datetime", "Liveness of signal and of data."],
    ["StatusSignalValue", "string", "Latest status signal reading (e.g. RSSI dBm)."],
    ["FirmwareVersion", "string", "On-device firmware."],
    ["PurchaseDate / InstallDate", "date", "Lifecycle dates."],
    ["Notes", "string", "Free text."],
]))
story.append(entity("A.3.4 StructureMountPoints — uniform side / vertex / height anchors",
    "Uniformly placed attachment points on the structure, aligned to the existing StructureVertexId "
    "axis where one exists. Nominal coordinates are immutable.", [
    ["MountPointId", "string PK", "Anchor identity."],
    ["CityStructureId", "string FK→CityStructures", "Owning structure."],
    ["StructureVertexId", "string FK→StructureVertices, nullable", "Perimeter vertex axis, when the "
        "anchor coincides with a footprint vertex."],
    ["MountLabel", "string", "e.g. 'Facade-N-H32' or 'V12-Top'."],
    ["Side", "'N'|'E'|'S'|'W'|custom", "Structure side the anchor is placed on."],
    ["AnchorType", "'side'|'vertex'|'height_station'", "Kind of uniform placement."],
    ["HeightAboveGroundM", "decimal", "Nominal mounting height."],
    ["NominalLatitude / NominalLongitude", "decimal(9,6)", "As-mounted WGS84 position (immutable)."],
    ["NominalAltitudeM", "decimal(9,4)", "As-mounted altitude reference."],
    ["PlacementScheme", "string", "Uniform scheme reference, e.g. '10 m vertical grid, corner "
        "vertices mandatory'."],
    ["InstalledUtc", "datetime", "Installation time."],
    ["Notes", "string", "Orientation, mounting method details."],
]))
story.append(entity("A.3.5 DeviceAssignments — device association to structure",
    "Associates a tracker with a structure mount point; history is preserved so relocation never "
    "destroys prior readings' context.", [
    ["AssignmentId", "string PK", "Association identity."],
    ["TrackerDeviceId", "string FK→TrackerDevices", "The device."],
    ["CityStructureId", "string FK→CityStructures", "The structure."],
    ["MountPointId", "string FK→StructureMountPoints", "The uniform anchor point."],
    ["CentralServerId", "string FK→CentralServers, nullable", "Server the device delivers to."],
    ["Status", "'active'|'removed'|'relocated'|'planned'", "Assignment state."],
    ["MountedUtc / RemovedUtc", "datetime", "Assignment window."],
    ["ReportingMode", "'on_movement'|'interval'|'both'", "How the device records."],
    ["ReportingIntervalMinutes", "int", "Default interval when interval mode is used."],
    ["MountingMethod / OrientationNotes", "string", "Physical mounting details."],
]))
story.append(entity("A.3.6 TrackerReadings — high-precision telemetry",
    "One row per transmission. Trigger type distinguishes movement-triggered from interval "
    "recordings. Displacement is computed against the mount point's immutable nominal position.", [
    ["TrackerReadingId", "string PK", "Reading identity."],
    ["TrackerDeviceId / AssignmentId", "string FKs", "Device and its assignment at reading time."],
    ["CityStructureId / MountPointId", "string FKs", "Structure and anchor."],
    ["CapturedUtc", "datetime", "Device capture time (UTC)."],
    ["TriggerType", "'movement'|'interval'|'manual'|'alert'", "Why the reading was recorded."],
    ["Latitude / Longitude", "decimal(9,6)", "Reported WGS84 position."],
    ["AltitudeM", "decimal(9,4)", "Reported altitude (GNSS and/or barometric-corrected)."],
    ["AccuracyM", "decimal", "Stated accuracy of this fix — drives alert gating."],
    ["DisplacementHorizontalM", "decimal", "Δ vs nominal mount position (see formulas)."],
    ["DisplacementVerticalM", "decimal", "ΔZ vs nominal altitude."],
    ["DisplacementTotalM", "decimal", "3D magnitude."],
    ["SpeedMps / HeadingDeg", "decimal", "Derived motion attributes."],
    ["BatteryLevelPercent / StatusSignal", "int / string", "Device health at capture."],
    ["Classification", "'TRACKED_MOVEMENT'|'SENSOR_ARTIFACT'|'WITHIN_UNCERTAINTY'", "Change-classification "
        "discipline result."],
    ["IngestionRunId", "string FK→IngestionRuns", "Provenance of ingestion."],
]))
story.append(entity("A.3.7 DeviceMaintenance — maintenance details",
    "Maintenance lifecycle per device.", [
    ["MaintenanceId", "string PK", "Record identity."],
    ["TrackerDeviceId", "string FK→TrackerDevices", "Serviced device."],
    ["MaintenanceDate", "date", "Service date."],
    ["MaintenanceType", "'inspection'|'battery'|'repair'|'replacement'|'calibration'|'firmware'", "Kind."],
    ["PerformedBy", "string", "Technician / vendor."],
    ["Findings", "string", "Observations and actions taken."],
    ["NextMaintenanceDueUtc", "datetime", "Next scheduled service."],
    ["Cost", "decimal", "Optional cost tracking."],
    ["Notes", "string", "Free text."],
]))
story.append(PageBreak())

# ---------------------------------------------------------------- part B
story.append(P("Part B — Barometric / Wind-Pressure &amp; External-Factor Addendum", H1))
story.append(P(
    "Part B extends the same device class with a compatible tracker that additionally senses "
    "barometric pressure and wind pressure in the vicinity of the structure, and revises the database "
    "and UI so the effect of movement can be captured with respect to external factors — wind "
    "pressure, barometric pressure, and other identifiable factors such as seismic data.", BODY))

story.append(entity("B.1 EnvironmentalReadings — external-factor observations (extensible)",
    "Stores environmental observations near a structure: barometric pressure (hPa), wind pressure "
    "(Pa), wind speed (m/s), seismic intensity (e.g. PGA), or any future factor. The open "
    "ReadingType catalogue keeps the design extensible without schema changes.", [
    ["EnvironmentalReadingId", "string PK", "Reading identity."],
    ["CityStructureId", "string FK→CityStructures", "Structure in whose vicinity the reading applies."],
    ["TrackerDeviceId", "string FK→TrackerDevices, nullable", "Capturing device (when the same "
        "device tracks position and environment)."],
    ["MountPointId", "string FK→StructureMountPoints, nullable", "Anchor the device is mounted at."],
    ["CapturedUtc", "datetime", "Observation time (UTC)."],
    ["Source", "'tracker'|'barometric'|'seismic_feed'|'weather_feed'|'manual'", "Provenance."],
    ["ReadingType", "'barometric_pressure'|'wind_pressure'|'wind_speed'|'wind_direction'|'seismic'|"
        "'temperature'|<i>extensible</i>", "Open catalogue of external factors."],
    ["Value / Unit", "decimal / string", "e.g. 1013.2 hPa, 240 Pa, 18.4 m/s, 0.02 g PGA."],
    ["Latitude / Longitude", "decimal(9,6), nullable", "Vicinity position when sensor is off-structure."],
    ["SourceReference", "string", "External feed identifier (e.g. USGS event id) for audit."],
    ["IngestionRunId", "string FK→IngestionRuns", "Provenance."],
]))
story.append(entity("B.2 StructuralEvents — movement correlated with external factors",
    "Captures the effect of movement with respect to external factors: a displacement episode at one "
    "or more mount points, the contemporaneous environmental readings, and a correlation "
    "classification. The event never asserts causation without validation, matching the parent "
    "design's discipline.", [
    ["StructuralEventId", "string PK", "Event identity."],
    ["CityStructureId", "string FK→CityStructures", "Affected structure."],
    ["DetectedUtc / WindowStartUtc / WindowEndUtc", "datetime", "Detection time and correlation "
        "window."],
    ["EventType", "'wind_pressure_movement'|'seismic_movement'|'barometric_response'|'thermal'|"
        "'unexplained_movement'|<i>extensible</i>", "Open catalogue of event types."],
    ["AffectedMountPoints", "string (CSV of ids)", "Anchors with material displacement."],
    ["MaxDisplacementM", "decimal", "Largest displacement in window."],
    ["PeakEnvironmentalValue / Unit", "decimal / string", "e.g. peak wind pressure 380 Pa."],
    ["CorrelatedReadingIds", "string (CSV of ids)", "TrackerReadings involved."],
    ["CorrelatedEnvironmentalIds", "string (CSV of ids)", "EnvironmentalReadings involved."],
    ["Severity", "'low'|'moderate'|'high'|'critical'", "Materiality band."],
    ["CorrelationConfidence", "decimal(3,2)", "0.00–1.00 confidence of the movement-vs-factor link."],
    ["Classification", "'CORRELATED'|'SUSPECTED'|'UNCORRELATED'|'UNDER_VALIDATION'", "Correlation "
        "status; material or low-confidence events route to ValidationCases."],
    ["ValidationCaseId", "string FK→ValidationCases, nullable", "Manual review queue reference."],
    ["AnalysisNotes", "string", "Engineer-visible analysis narrative."],
]))

story.append(P("B.3 Movement-vs-factor correlation method", H2))
for b in [
    "<b>Windowing.</b> For each mount point, group TrackerReadings into movement episodes "
    "(displacement exceeding the combined uncertainty budget — the parent design's rule: alert only "
    "when movement exceeds threshold AND source accuracy).",
    "<b>Factor matching.</b> For each episode, fetch EnvironmentalReadings in the same window "
    "(default ±15 minutes) for the structure's vicinity, by ReadingType.",
    "<b>Correlation scoring.</b> Rank factors by temporal overlap, magnitude relative to typical "
    "range, and the expected physical response (wind pressure on the exposed facade side, seismic "
    "PGA vs building natural period, barometric step vs altitude solution). Produce "
    "CorrelationConfidence; only CORRELATED (high confidence) events skip manual validation.",
    "<b>No causation without validation.</b> SUSPECTED and low-confidence events create "
    "ValidationCases for engineering review, consistent with the parent design's manual 360° "
    "validation workflow.",
]:
    story.append(P(b, BODY))

story.append(P("B.4 Formulas (extend the parent design's change-detection formulae)", H2))
story.append(tbl(["Quantity", "Formula"], [
    ["Horizontal displacement", "DisplacementHorizontalM = sqrt((Lon−Lon_nominal)² + (Lat−Lat_nominal)²) "
     "in a metric projected CRS, per the parent design's CRS discipline"],
    ["Vertical displacement", "DisplacementVerticalM = AltitudeM − NominalAltitudeM (barometric-corrected)"],
    ["Total 3D displacement", "DisplacementTotalM = sqrt(H² + V²)"],
    ["Wind pressure (dynamic)", "q = ½ · ρ · v² (Pa), with ρ from barometric pressure and temperature "
     "when the device reports both"],
    ["Alert gate", "material iff DisplacementTotalM &gt; MovementAlertThreshold AND "
     "DisplacementTotalM &gt; AccuracyM · k (combined uncertainty budget)"],
    ["Correlation window", "[WindowStartUtc, WindowEndUtc] = episode ± WindPressure/Seismic "
     "correlation buffer (configurable, default 15 min)"],
], widths=[52*mm, 130*mm]))
story.append(Spacer(1, 6))

# ---------------------------------------------------------------- UI
story.append(P("Part C — User Interface Addendum (same database)", H1))
story.append(P(
    "All pages read and write the same database as the existing StructureGuard EyeWatch UI and obey "
    "its access model (admin for destructive actions; every change audited via AuditEvents).", BODY))
story.append(tbl(["Page", "Contents", "Operations"], [
    ["Tracker Infrastructure dashboard",
     "All tracker devices: status, battery, last signal, status signal, model capabilities, "
     "assigned structure/mount point",
     "Add / Edit / Delete device; view live liveness"],
    ["Central Servers",
     "Server registry: location, endpoint, protocol, capacity, heartbeat, status",
     "Add / Edit / Maintain / Delete"],
    ["Device Catalog",
     "DeviceModel catalogue incl. TOPFLYtech TLW2-12B: sensor range, charge duration, self-charge "
     "capability, status signal, barometric/wind capability",
     "Add / Edit / Delete; seed entries"],
    ["Device Association",
     "Device↔structure↔mount point assignments with reporting mode (on-movement / interval)",
     "Assign / Relocate / Remove / Maintain history"],
    ["Structure Mount Points",
     "Per-structure uniform side/vertex/height map with nominal coordinates + altitude",
     "Add / Edit / Delete; immutable nominal on edit"],
    ["Maintenance",
     "Per-device maintenance records, next-due dates, overdue highlighting",
     "Add / Edit / Maintain / Delete"],
    ["Telemetry Viewer",
     "Recent TrackerReadings per structure/device: position, altitude, displacement vs nominal, "
     "trigger type, classification",
     "Filter / export; view ingestion runs"],
    ["Environmental Viewer",
     "Barometric / wind pressure / seismic readings per structure with correlated StructuralEvents "
     "and severity",
     "Filter by ReadingType; open validation case"],
    ["3D/4D Structure Viewer (existing, enhanced)",
     "Tracker coordinates and altitude drive 3D structure enhancement; 4D timeline scrubs recorded "
     "movements (Δ at each vertex/mount axis) with wind/seismic overlays",
     "Play / scrub / select epoch"],
], widths=[34*mm, 92*mm, 56*mm]))
story.append(Spacer(1, 6))

story.append(P("Part D — API &amp; Daemon Integration", H1))
story.append(P("New REST routes (alongside the existing structure/geometry routes):", BODY))
for r in [
    "GET/POST/PUT/DELETE /api/trackers, /api/device-models, /api/central-servers, /api/mount-points, "
    "/api/assignments, /api/maintenance — full CRUD for the registry pages.",
    "POST /api/ingest/tracker-reading — authenticated ingestion endpoint used by the central server "
    "(idempotent by device serial + capture time; writes TrackerReadings, updates device health).",
    "POST /api/ingest/environmental-reading — ingestion for environmental sensors and external feeds "
    "(USGS seismic, weather feeds).",
    "GET /api/structures/:id/trackers — devices, mount points and latest readings for a structure.",
    "GET /api/structures/:id/movement-timeline — 4D animation feed: per-epoch displacement at each "
    "mount axis, with environmental overlays.",
    "POST /api/structures/:id/correlate — on-demand movement-vs-factor correlation run.",
    "GET /api/structures/:id/events — StructuralEvent history with severity and validation state.",
]:
    story.append(P("• " + r, MONO))
story.append(Spacer(1, 4))
story.append(P(
    "The existing background daemon gains a tracker pass: on each scheduled run it reads fresh "
    "TrackerReadings, computes displacement vs nominal mount positions, applies the uncertainty "
    "gate, classifies changes, correlates material episodes with EnvironmentalReadings, raises "
    "StructuralEvents, routes material/low-confidence cases to ValidationCases, and writes "
    "IngestionRuns + AuditEvents. Configuration mirrors the parent design (thresholds, intervals, "
    "retention, per-run limits).", BODY))

story.append(P("Part E — Compliance, Security &amp; Extensibility", H1))
for b in [
    "<b>Fits the existing design.</b> All new entities hang off CityStructureId / StructureVertexId; "
    "no existing table changes; the vertex-duplication vertical digital-twin is reused as the spatial "
    "axis for mount points.",
    "<b>Audit.</b> Registry mutations (add/edit/delete of devices, servers, assignments, "
    "maintenance) write AuditEvents with old/new values and actor, as the parent design requires.",
    "<b>Access model.</b> CRUD pages require an authenticated user; destructive operations are "
    "admin-only; row-level scoping matches the existing application.",
    "<b>Extensibility.</b> ReadingType / EventType / MaintenanceType are open catalogues; new "
    "external factors (temperature, tide, vibration) require no schema change. New device classes "
    "(RTK, anemometric, self-charging solar) enter through DeviceModels.",
    "<b>Honest accuracy.</b> Every alert is gated by the reading's stated accuracy and the "
    "uncertainty budget; the system never claims millimetre structural movement from a "
    "metre-accurate asset tracker, consistent with the parent design's imagery discipline.",
    "<b>Retention.</b> Raw readings retained per configuration (RetainRawEvidenceDays); aggregate "
    "displacement series retained for trend analysis.",
]:
    story.append(P(b, BODY))

story.append(P("Part F — Implementation Milestones", H1))
for i, m in enumerate([
    "Define entity schemas for CentralServers, DeviceModels, TrackerDevices, StructureMountPoints, "
    "DeviceAssignments, TrackerReadings, DeviceMaintenance, EnvironmentalReadings, StructuralEvents.",
    "Seed DeviceModels with the TOPFLYtech TLW2-12B 4G LTE Asset Tracker and a "
    "barometric/wind-pressure-capable compatible model.",
    "Build ingestion endpoints (tracker + environmental) with idempotency, auth and audit.",
    "Extend the daemon: displacement evaluation, uncertainty gating, classification, "
    "movement-vs-factor correlation, event raising.",
    "Build the CRUD UI pages on the same database with admin-guarded destructive actions.",
    "Enhance the 3D/4D viewer: tracker-driven structure enhancement and movement animation with "
    "wind/seismic overlays.",
    "Tests: ingestion idempotency, displacement math, correlation windows, CRUD permissions, UI "
    "wiring; verify and compile the whole application cleanly.",
], 1):
    story.append(P(f"{i}. {m}", BODY))

story.append(Spacer(1, 10))
story.append(HRFlowable(width="100%", thickness=0.8, color=ACCENT))
story.append(Spacer(1, 8))
story.append(P("AI-assisted implementation prompt", H2))
story.append(P(
    "You are the senior architect/developer enhancing StructureGuard EyeWatch "
    "(https://aisstructureguardeyewatch.ai.studio, source github.com/daluvalanokia/"
    "aisstructureguardeyewatch). Build the Uniform Tracker Network &amp; Environmental Movement "
    "Correlation addendum exactly as specified in this document: (1) the CentralServers, DeviceModels "
    "(seed TOPFLYtech TLW2-12B 4G LTE Asset Tracker + barometric/wind-pressure variant), "
    "TrackerDevices, StructureMountPoints (uniform side/vertex/height anchors with immutable "
    "nominal coordinates and altitude), DeviceAssignments, TrackerReadings (movement/interval "
    "triggered, high-precision coordinates and altitude, displacement vs nominal), DeviceMaintenance, "
    "EnvironmentalReadings (barometric/wind pressure, seismic, extensible types) and StructuralEvents "
    "(movement correlated with external factors) entities, all referencing the existing "
    "CityStructureId/StructureVertexId design; (2) ingestion endpoints that the central server "
    "delivers device data to, with idempotency, auth and audit; (3) daemon extension that gates "
    "alerts by stated accuracy, classifies TRACKED_MOVEMENT vs SENSOR_ARTIFACT, and correlates "
    "movement episodes with environmental readings without asserting causation; (4) UI pages on the "
    "same database for device infrastructure, central servers, device catalog, device-structure "
    "association, mount points, maintenance and telemetry/environmental viewers with full "
    "add/edit/maintain/delete; (5) 3D/4D viewer enhancement using tracker coordinates and altitude "
    "for structure enhancement and animation of recorded movements with wind/seismic overlays. Keep "
    "every accuracy claim gated by the source's stated accuracy. Provide the complete "
    "implementation plan, schema, API contracts, UI wireframes, configuration, tests and deployment "
    "instructions, in compliance with all existing design decisions.", BODY))

def footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 7.5)
    canvas.setFillColor(MUTED)
    canvas.drawString(18*mm, 12*mm, "StructureGuard EyeWatch — Uniform Tracker Network Addendum · "
                                    "aisstructureguardeyewatch")
    canvas.drawRightString(A4[0] - 18*mm, 12*mm, f"Page {doc.page}")
    canvas.restoreState()

doc = SimpleDocTemplate("tracker-addendum/StructureGuard_EyeWatch_Tracker_Addendum.pdf",
                        pagesize=A4, leftMargin=18*mm, rightMargin=18*mm,
                        topMargin=16*mm, bottomMargin=20*mm,
                        title="StructureGuard EyeWatch — Uniform Tracker Network Addendum",
                        author="SmartAgent")
doc.build(story, onFirstPage=footer, onLaterPages=footer)
print("PDF built")
