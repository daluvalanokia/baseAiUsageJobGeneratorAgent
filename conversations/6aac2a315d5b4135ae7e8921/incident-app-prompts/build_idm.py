#!/usr/bin/env python3
"""Renders the Incident Detail and Mitigate (IDM) prompt-generation PDF."""
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.platypus import (SimpleDocTemplate, Paragraph, Spacer, Table,
                                TableStyle, PageBreak, KeepTogether, HRFlowable)

ACCENT = colors.HexColor("#7a2831")
LIGHT = colors.HexColor("#f3e8e9")
MUTED = colors.HexColor("#555555")

styles = getSampleStyleSheet()
H1 = ParagraphStyle("H1", parent=styles["Heading1"], fontSize=17, textColor=ACCENT,
                    spaceBefore=6, spaceAfter=8, leading=21)
H2 = ParagraphStyle("H2", parent=styles["Heading2"], fontSize=13, textColor=ACCENT,
                    spaceBefore=14, spaceAfter=6, leading=16)
H3 = ParagraphStyle("H3", parent=styles["Heading3"], fontSize=11, textColor=colors.HexColor("#2d2224"),
                    spaceBefore=10, spaceAfter=4)
BODY = ParagraphStyle("BODY", parent=styles["BodyText"], fontSize=9.2, leading=13.2, spaceAfter=5)
SMALL = ParagraphStyle("SMALL", parent=BODY, fontSize=8.2, leading=11, textColor=MUTED)
CELL = ParagraphStyle("CELL", parent=BODY, fontSize=7.9, leading=10.4, spaceAfter=0)
CELLB = ParagraphStyle("CELLB", parent=CELL, fontName="Helvetica-Bold")
MONO = ParagraphStyle("MONO", parent=CELL, fontName="Courier", fontSize=7.4, leading=10)

def P(t, s=BODY): return Paragraph(t, s)

def tbl(headers, rows, widths, mono_col=None):
    head = [Paragraph(h, CELLB) for h in headers]
    body = [[Paragraph(c, MONO if (mono_col is not None and i in mono_col) else CELL)
             for i, c in enumerate(r)] for r in rows]
    t = Table([head] + body, colWidths=widths, repeatRows=1)
    t.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), ACCENT),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, LIGHT]),
        ("GRID", (0, 0), (-1, -1), 0.4, colors.HexColor("#c9b3b6")),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 4), ("RIGHTPADDING", (0, 0), (-1, -1), 4),
        ("TOPPADDING", (0, 0), (-1, -1), 2.5), ("BOTTOMPADDING", (0, 0), (-1, -1), 2.5),
    ]))
    return t

def entity(title, note, rows):
    return KeepTogether([P(title, H3), P(note, SMALL),
        tbl(["Column", "Type / Values", "Description"], rows,
            widths=[38*mm, 40*mm, 104*mm], mono_col={0}), Spacer(1, 6)])

def promptbox(title, body):
    # chunk the prompt into sentence-boundary rows so the table can split across pages
    import re
    sentences = re.findall(r"[^.]+\.(?:\s|$)", body)
    chunks, cur = [], ""
    for sent in sentences:
        if len(cur) + len(sent) > 450 and cur:
            chunks.append(cur.strip()); cur = sent
        else:
            cur += sent
    if cur.strip(): chunks.append(cur.strip())
    t = Table([[Paragraph(c, CELL)] for c in chunks], colWidths=[182*mm])
    t.setStyle(TableStyle([
        ("BOX", (0, 0), (-1, -1), 0.6, colors.HexColor("#c9b3b6")),
        ("LINEBELOW", (0, 0), (-1, -2), 0.25, colors.HexColor("#e3d5d7")),
        ("BACKGROUND", (0, 0), (-1, -1), colors.HexColor("#fbf7f7")),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 6), ("RIGHTPADDING", (0, 0), (-1, -1), 6),
        ("TOPPADDING", (0, 0), (-1, -1), 4), ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
    ]))
    return [P(title, H3), t, Spacer(1, 8)]

story = []

# ---------------------------------------------------------------- cover
story.append(P("Incident Detail and Mitigate (IDM)", H1))
story.append(P("Validated App-Generation Prompts, Reasoning-API Integration and Design "
               "Specification — Highway Incident Capture, Vehicle Damage Comparison and "
               "Insurance Cost Evaluation", H2))
story.append(tbl(["Field", "Value"], [
    ["Business need", "Integrate video / 3D data capture at sensitive traffic locations (highways) "
        "via drone-type or stretchable-arm devices to keep officers safe, support highway-safety "
        "officers, and provide insurance-grade damage validation"],
    ["App name", "Incident Detail and Mitigate (IDM)"],
    ["Stack", "ASP.NET Core MVC · C# · Razor Pages/Views · EF Core · SQL Server · Reasoning APIs"],
    ["Deliverable", "Design spec + database models + controller contracts + validated generation prompts"],
    ["Date", "28 September 2026"],
], widths=[34*mm, 148*mm]))
story.append(Spacer(1, 8))
story.append(P(
    "IDM captures a complete incident scene from a safe standoff distance using a drone-type device "
    "or a stretchable/robotic arm with a fixed-length camera/3D sensor, then compares the captured "
    "geometry of each impacted vehicle against the original framework model of the same make/model "
    "(pre-recorded from a fixed distance by the same class of device). The reasoning layer computes "
    "part-level damage, produces an animated consolidated comparison, and delivers an initial cost "
    "evaluation and fix analysis usable by officers and insurance validators.", BODY))
story.append(P("Design integrity rules (apply to every prompt below)", H2))
for b in [
    "<b>Same-device, same-distance discipline.</b> Original (intact) vehicle geometry and incident "
    "geometry must be captured by the same device class from the same fixed standoff length in "
    "360° passes, so deltas are attributable to damage, not to capture parallax.",
    "<b>Original model is immutable.</b> The intact reference scan and the manufacturer part catalog "
    "are never overwritten; incident scans are separate versioned observations.",
    "<b>Gated claims.</b> Every damaged-part conclusion carries a confidence score and the sensor's "
    "stated accuracy; low-confidence parts route to manual officer/adjuster review instead of "
    "auto-costing — insurance-grade integrity.",
    "<b>Officer safety first.</b> All capture flows assume the officer remains outside the traffic "
    "lane; the device (drone or arm) performs the approach and the 360° sweep autonomously.",
    "<b>Audit trail.</b> Every evaluation step is persisted (raw video, scan session, per-part "
    "comparison, cost line items) with actor and timestamps for incident tracking.",
]:
    story.append(P(b, BODY))
story.append(PageBreak())

# ---------------------------------------------------------------- architecture
story.append(P("1. Solution Architecture", H1))
story.append(tbl(["Layer", "Components"], [
    ["Capture layer", "Drone-type device or stretchable robotic arm with fixed-length camera + 3D "
        "sensor (LiDAR/depth stereo); onboard 360° sweep controller; raw video + point-cloud/mesh "
        "streaming over HTTPS/MQTT to the ingestion API"],
    ["Ingestion layer", "ASP.NET Core API endpoints: device registration, session control, "
        "frame/scan upload, raw-video archival; idempotent by device + session + capture time"],
    ["Reference layer", "Vehicle inventory with original framework models: make/model/year, part "
        "catalog with part 3D meshes and costs, intact reference scans recorded at the same fixed "
        "distance"],
    ["Reasoning layer", "Reasoning APIs + geometry pipeline: align incident scan to reference scan "
        "(registration by make/model), per-part geometric delta, damage classification, confidence "
        "scoring, cost aggregation, fix-vs-replace analysis"],
    ["Application layer", "ASP.NET Core MVC: 8 menu modules (Inventory, Incident List, Incident, "
        "Incident Evaluation, Incident Cost Evaluation, Settings, Users, Device Configuration); "
        "Razor views; EF Core models; role-based access; reports"],
], widths=[30*mm, 152*mm]))
story.append(Spacer(1, 6))
story.append(P("1.1 Reasoning pipeline (original vs impacted vehicle)", H2))
story.append(P(
    "1) <b>Reference capture:</b> intact vehicle scanned 360° at fixed distance D (same device class) "
    "→ normalized mesh M0 stored as the original framework model. 2) <b>Incident capture:</b> at the "
    "scene the same device class scans the impacted vehicle 360° at the same standoff D → mesh M1. "
    "3) <b>Registration:</b> M1 aligned to M0 (ICP/anchor landmarks; make/model known). "
    "4) <b>Part mapping:</b> both meshes segmented by the part catalog's part meshes → per-part "
    "Hausdorff/surface-deviation Δ. 5) <b>Classification:</b> Δ beyond sensor uncertainty = DAMAGED "
    "(with severity band); Δ within uncertainty = INTACT. 6) <b>Reasoning:</b> for each DAMAGED part, "
    "the reasoning API proposes repair-vs-replace, labor estimate and cost line item, with "
    "confidence and caveats. 7) <b>Consolidation:</b> animated side-by-side comparison and a single "
    "consolidated cost evaluation; low-confidence items flagged for manual validation.", BODY))

# ---------------------------------------------------------------- database
story.append(P("2. Database Models (EF Core entities)", H1))
story.append(entity("2.1 VehicleInventory + VehicleParts — original framework models",
    "One row per make/model/year in inventory; the part catalog holds each part's mesh reference and "
    "cost used in comparisons. The reference scan is captured by the same device class at the fixed "
    "distance.", [
    ["VehicleId", "int PK", "Inventory identity."],
    ["Make / Model / Year", "string / string / int", "Vehicle identification."],
    ["ReferenceScanId", "string FK→ScanSessions, nullable", "Intact 360° scan of this vehicle."],
    ["ThumbnailUrl / Model3dUrl", "string", "3D view assets for the inventory page."],
    ["Status", "'active'|'archived'", "Inventory state."],
    ["PartId / VehicleId", "int PK / FK", "Part catalog key."],
    ["PartName / PartNumber", "string", "e.g. 'Front Bumper Cover', OEM number."],
    ["PartMeshUrl / Part3dRef", "string", "Part mesh used for segmentation and click-to-view."],
    ["OemCost / LaborHours / LaborRate", "decimal", "Baseline cost inputs for evaluation."],
    ["Availability", "'oem'|'aftermarket'|'salvage'", "Fix-analysis sourcing options."],
]))
story.append(entity("2.2 Incidents + IncidentVehicles + RawVideos",
    "The incident list and create-incident modules. IncidentVehicles links an incident to one or "
    "more inventory vehicles with a role.", [
    ["IncidentId", "int PK", "Incident identity (displayed in lists)."],
    ["IncidentName / IncidentLocation", "string", "Name and highway/location reference."],
    ["OccurredUtc / ReportedUtc", "datetime", "Timing."],
    ["ReportingOfficerId", "string FK→Users", "Officer in charge."],
    ["Status", "'open'|'capturing'|'evaluated'|'closed'", "Incident tracking state."],
    ["IncidentVehicleId", "int PK", "Join identity."],
    ["IncidentId / VehicleId", "int FKs", "Impacted vehicle vs original model."],
    ["Role", "'impacted'|'reference'|'other'", "Role in this incident."],
    ["Incident3dUrl", "string", "Consolidated incident 3D view."],
    ["VideoId / IncidentId", "int PK / FK", "Raw incident video registry."],
    ["VideoUrl / DeviceSessionId", "string / string FK", "Media and its capture session."],
    ["CapturedUtc / DurationSec", "datetime / int", "Provenance of footage."],
]))
story.append(entity("2.3 CaptureDevices + ScanSessions — device configuration & capture",
    "Device Configuration menu manages the drone/arm fleet; sessions record each 360° pass "
    "(reference or incident) with the sensor's stated accuracy.", [
    ["DeviceId", "int PK", "Device identity."],
    ["DeviceType", "'drone'|'stretchable_arm'|'robotic_arm'|'fixed_camera'", "Device class."],
    ["Model / SerialNumber", "string / unique", "Hardware identification."],
    ["StandoffLengthM", "decimal", "Fixed capture distance used for reference and incident passes."],
    ["Sensors", "'rgb'|'depth_stereo'|'lidar'|'thermal'", "Onboard sensor set."],
    ["StatedAccuracyMm", "decimal", "Sensor accuracy — gates damage claims."],
    ["SweepControl", "'autonomous_360'|'manual'|'programmed'", "Sweep mode."],
    ["Status / BatteryPercent / LastSignalUtc", "enum/int/datetime", "Fleet health."],
    ["SessionId", "int PK", "Capture session identity."],
    ["DeviceId / IncidentId", "int FKs, Incident nullable", "Reference scans have null incident."],
    ["SessionType", "'reference'|'incident'", "Original-model capture vs scene capture."],
    ["SweepStartUtc / SweepEndUtc", "datetime", "360° pass window."],
    ["MeshUrl / PointCloudUrl", "string", "Normalized scan outputs."],
    ["IngestionRunId", "string", "Audit provenance."],
]))
story.append(entity("2.4 PartComparisons + DamageEvaluations + CostEvaluations",
    "The reasoning layer's persisted results: per-part deltas, classifications, and the consolidated "
    "cost evaluation with fix analysis.", [
    ["ComparisonId", "int PK", "Comparison identity."],
    ["IncidentVehicleId", "int FK", "Which impacted vehicle."],
    ["PartId", "int FK", "Compared part."],
    ["SurfaceDeltaMm", "decimal", "Geometric deviation vs original part."],
    ["Classification", "'INTACT'|'DAMAGED_MINOR'|'DAMAGED_MAJOR'|'DESTROYED'|"
        "'WITHIN_UNCERTAINTY'", "Damage band, uncertainty-gated."],
    ["Confidence", "decimal(3,2)", "0.00–1.00 reasoning confidence."],
    ["ReasoningTraceUrl", "string", "Stored reasoning-API output for audit."],
    ["EvaluationId", "int PK", "Evaluation identity."],
    ["IncidentVehicleId / ComparisonId", "int FKs", "Scope."],
    ["EvaluatedUtc / ReasoningModel", "datetime / string", "When and which reasoning API."],
    ["Recommendation", "'repair'|'replace'|'inspect_manually'", "Fix analysis per part."],
    ["LaborEstimate / PartsCost / TotalCost", "decimal", "Initial cost comparison lines."],
    ["Caveats", "string", "Confidence caveats, sensor notes."],
    ["CostEvaluationId", "int PK", "Consolidated cost identity."],
    ["IncidentId", "int FK", "Incident-scoped totals."],
    ["ConsolidatedPartsCost / ConsolidatedLabor / GrandTotal", "decimal", "Totals for insurance."],
    ["InsuranceRef / ValidationStatus", "string / 'pending'|'validated'|'disputed'", "Insurance "
        "validation workflow."],
]))
story.append(entity("2.5 Users + Settings + Reports + AuditEvents",
    "Support tables for the Settings, Users and reports-generation menu items.", [
    ["UserId", "string PK", "Application user."],
    ["FullName / BadgeNumber / Role", "string", "'admin'|'officer'|'adjuster'|'viewer'."],
    ["SettingKey / SettingValue", "string PK", "e.g. DefaultStandoffLengthM, DamageThresholdMm, "
        "ReasoningApiEndpoint, Currency."],
    ["ReportId", "int PK", "Generated report registry."],
    ["ReportType / IncidentId", "'incident_summary'|'damage_evaluation'|'cost_breakdown'|'insurance_pack'", ""],
    ["GeneratedUtc / Format / FileUrl", "datetime / 'PDF'|'CSV'", "Report output."],
    ["AuditEventId / EntityType / EntityId", "string PK", "Actor, action, old/new values, timestamp "
        "for every mutation."],
]))
story.append(PageBreak())

# ---------------------------------------------------------------- UI
story.append(P("3. User Interface Specification (8 menu items)", H1))
story.append(tbl(["Menu / Page", "Specification", "Controller / Actions"], [
    ["1. Inventory", "Table of vehicles: Make, Model, Year, View-3D, Edit/Delete. 'Add Vehicle' "
        "form; 'Animate Available Vehicles' button plays 360° animated views; clicking a part shows "
        "part details and cost",
     "InventoryController: Index, Details, Create, Edit, Delete, Animate, PartDetails"],
    ["2. Incident List", "Table: Incident Id, Incident Name, Location, Incident 3D View, Raw "
        "Video, Edit/Delete; 'Add Incident' action",
     "IncidentsController: Index, Create, Edit, Delete, View3d, RawVideo"],
    ["3. Incident (create)", "Guided capture: select incident vehicle(s) from inventory, dispatch "
        "drone/stretchable-arm device, autonomous 360° sweep at fixed length, live capture status, "
        "damaged-part capture hints vs original model",
     "IncidentsController: Create, DispatchDevice, SessionStatus, ScanUpload, FinalizeCapture"],
    ["4. Incident Evaluation", "Enter Incident Id + Make/Model; side-by-side animated comparison of "
        "original vs impacted; table of affected parts with damage band, confidence and reasoning "
        "recommendation",
     "EvaluationController: Index, Compare, RunEvaluation, PartDetail"],
    ["5. Incident Cost Evaluation", "Incident-scoped cost breakdown table: parts, labor, "
        "repair-vs-replace, consolidated totals; initial incident evaluation details; export/report",
     "CostEvaluationController: Index, Details, Recalculate, Export"],
    ["6. Settings", "Global thresholds (damage threshold, standoff length), reasoning-API endpoint "
        "and model selection, currency, retention",
     "SettingsController: Index, Update"],
    ["7. Users", "User administration table: officers, adjusters, admins; role assignment",
     "UsersController: Index, Create, Edit, Delete, AssignRole"],
    ["8. Device Configuration", "Drone/arm fleet registry: type, standoff length, sensors, stated "
        "accuracy, sweep mode, health, maintenance",
     "DevicesController: Index, Create, Edit, Delete, TestConnection, Health"],
], widths=[28*mm, 88*mm, 66*mm]))
story.append(Spacer(1, 6))

story.append(P("4. Reasoning-API Integration Contract", H1))
for r in [
    "POST /api/reasoning/evaluate-damage — inputs: incidentVehicleId, referenceScanId, incidentScanId, "
    "partIds; aligns meshes, computes per-part deltas; returns classifications with confidence and a "
    "stored reasoning trace.",
    "POST /api/reasoning/estimate-cost — inputs: list of damaged partIds + classification + "
    "availability; returns repair-vs-replace recommendation, labor/parts estimates, caveats; "
    "assembles CostEvaluation.",
    "POST /api/reasoning/fix-analysis — inputs: evaluationId; returns fix analysis (repair plan, "
    "sourcing options, sequencing) and insurance-facing summary.",
    "All reasoning calls: timeouts, retry with idempotency keys, model/endpoint from Settings, full "
    "request/response persisted for audit. Low-confidence outputs set ValidationStatus='pending' "
    "instead of auto-costing.",
]:
    story.append(P("• " + r, MONO))

story.append(P("5. Validated App-Generation Prompts", H1))
story.append(P(
    "The master prompt plus module prompts. Each is copy-ready; together they cover the full build "
    "with the integrity rules of section 0.", SMALL))

story.extend(promptbox("5.1 Master generation prompt", 
    "You are a senior ASP.NET Core architect. Build 'Incident Detail and Mitigate (IDM)', an MVC "
    "application (ASP.NET Core 8+, C#, Razor views, EF Core, SQL Server) for highway incident "
    "capture and insurance-grade vehicle damage evaluation. Context: drones or stretchable/robotic "
    "arm devices with a fixed-length camera/3D sensor capture incidents 360° from a safe standoff so "
    "officers never enter traffic; intact vehicles of the same make/model are pre-scanned by the "
    "same device class at the same fixed distance to form immutable original framework models; the "
    "reasoning layer compares impacted vs original geometry per part, classifies damage beyond "
    "sensor uncertainty, and produces cost and fix analysis. Deliver: full project structure, EF "
    "Core entities and migrations (VehicleInventory, VehicleParts, Incidents, IncidentVehicles, "
    "RawVideos, CaptureDevices, ScanSessions, PartComparisons, DamageEvaluations, "
    "CostEvaluations, Users, Settings, Reports, AuditEvents), controllers with the actions listed in "
    "section 3, Razor views for the 8 menu items, the reasoning-API contracts of section 4 "
    "(timeout, retry, idempotency, persisted traces), role-based authorization (admin/officer/"
    "adjuster/viewer), seeded sample data, unit tests, and clean-compiling code. Enforce the "
    "integrity rules: immutable reference models, same-device/same-distance capture discipline, "
    "uncertainty-gated damage claims, low-confidence results routed to manual review, full audit "
    "trail."))
story.extend(promptbox("5.2 Data-layer prompt",
    "Generate the EF Core data layer for IDM: entities per section 2 with keys, indexes and "
    "constraints (unique serial numbers; composite (IncidentId, VehicleId, Role); immutable "
    "reference rows protected by convention and service rules), DbSeed for sample vehicles (5 "
    "makes/models with 8–12 catalog parts each, mesh URLs, OEM/aftermarket costs), Settings defaults "
    "(DefaultStandoffLengthM, DamageThresholdMm, ReasoningApiEndpoint, Currency), one migration "
    "script, and repositories with audit-event emission on every mutation."))
story.extend(promptbox("5.3 Capture-integration prompt",
    "Generate the device and capture module for IDM: CaptureDevices CRUD (drone, stretchable arm, "
    "robotic arm, fixed camera; standoff length, sensors, stated accuracy, sweep mode, health), "
    "ScanSessions lifecycle (reference vs incident), API endpoints for device registration, session "
    "start/status, scan and raw-video upload (idempotent by device+session+timestamp), mesh "
    "normalization pipeline invocation, and the guided 'Create Incident' Razor flow that dispatches "
    "the device, shows live capture status, and finalizes the 360° capture linked to the incident "
    "vehicle and its original model."))
story.extend(promptbox("5.4 Reasoning-comparison prompt",
    "Generate the reasoning module for IDM: /api/reasoning/evaluate-damage aligns the incident scan "
    "to the reference scan of the same vehicle (ICP registration with landmark anchors), segments "
    "both meshes by the part catalog, computes per-part surface deviation, classifies each part "
    "INTACT / DAMAGED_MINOR / DAMAGED_MAJOR / DESTROYED or WITHIN_UNCERTAINTY relative to the "
    "sensor's stated accuracy, assigns 0.00–1.00 confidence, and persists PartComparisons with a "
    "reasoning trace. Then /api/reasoning/estimate-cost turns DAMAGED classifications into "
    "repair-vs-replace recommendations with labor/parts estimates and caveats, and consolidates "
    "them into a CostEvaluation. Never auto-cost parts below the confidence threshold: mark them "
    "ValidationStatus='pending' for officer/adjuster review."))
story.extend(promptbox("5.5 UI prompt",
    "Generate the Razor views and client script for IDM's 8 menu items per section 3: Inventory "
    "table (make/model/year, View-3D, Edit/Delete, Add Vehicle, Animate Available Vehicles with 360° "
    "rotation, click-a-part → part details and cost); Incident List (id, name, location, incident 3D "
    "view, raw video, Edit/Delete); Create Incident guided flow with device dispatch and live status; "
    "Incident Evaluation (incident id + make/model selectors, side-by-side animated comparison, "
    "affected-parts table with damage band and confidence); Incident Cost Evaluation (cost "
    "breakdown, consolidated totals, export); Settings; Users; Device Configuration. Use Bootstrap, "
    "one shared 3D viewer component (Three.js) for meshes and animation, and role-guarded destructive "
    "actions with audit."))
story.extend(promptbox("5.6 Reports prompt",
    "Generate the reporting module for IDM: report definitions for incident_summary, "
    "damage_evaluation, cost_breakdown and insurance_pack; generation from CostEvaluations and "
    "PartComparisons; PDF/CSV export; a Reports page listing generated reports with filters; and an "
    "audit view of every evaluation decision (who, when, which reasoning model, confidence, "
    "caveats) to support insurance validation and dispute handling."))

story.append(Spacer(1, 10))
story.append(HRFlowable(width="100%", thickness=0.8, color=ACCENT))
story.append(Spacer(1, 8))
story.append(P("6. Implementation Milestones", H1))
for i, m in enumerate([
    "Data layer: entities, migrations, seed, audit (prompt 5.2).",
    "Device + capture module and Create-Incident flow (prompt 5.3).",
    "Reasoning comparison and cost estimation with traces (prompt 5.4).",
    "All 8 UI modules with the shared 3D viewer (prompt 5.5).",
    "Reports, exports and insurance validation workflow (prompt 5.6).",
    "End-to-end verification: reference scan → incident scan → comparison → animated consolidated "
    "cost evaluation; tests green; clean compile.",
], 1):
    story.append(P(f"{i}. {m}", BODY))

def footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 7.5)
    canvas.setFillColor(MUTED)
    canvas.drawString(18*mm, 12*mm, "Incident Detail and Mitigate (IDM) — Design & Generation Prompts")
    canvas.drawRightString(A4[0] - 18*mm, 12*mm, f"Page {doc.page}")
    canvas.restoreState()

doc = SimpleDocTemplate("incident-app-prompts/Incident_Detail_and_Mitigate_Prompts.pdf",
                        pagesize=A4, leftMargin=18*mm, rightMargin=18*mm,
                        topMargin=16*mm, bottomMargin=20*mm,
                        title="Incident Detail and Mitigate — Design & Generation Prompts",
                        author="SmartAgent")
doc.build(story, onFirstPage=footer, onLaterPages=footer)
print("PDF built")
