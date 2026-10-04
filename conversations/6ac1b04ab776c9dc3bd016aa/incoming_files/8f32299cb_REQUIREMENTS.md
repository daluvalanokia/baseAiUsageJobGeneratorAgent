# Claft PM Hub — Full Requirements Specification

**Document Type:** Software Requirements Specification (SRS)  
**Project:** Claft Family Safety & Location Tracking Platform  
**Scope:** 2-Year Agile Delivery Programme + Interactive PM Hub Tool  
**Date:** May 2026  
**Version:** 1.0

---

## 1. Executive Summary

This document details the complete requirements used to design and build the **Claft PM Hub** — an interactive, browser-based project management reference tool that visualises the 2-year agile delivery plan for the **Claft Family Safety & Location Tracking Platform**.

The hub covers two layers of requirements:

| Layer | Description |
|---|---|
| **PM Hub (this tool)** | The React SPA itself — tabs, data, interactivity, platform switching |
| **Claft Platform (documented)** | The underlying family safety application being planned across 52 sprints |

---

## 2. Background & Business Context

### 2.1 The Claft Application

Claft is a family safety platform providing:
- Real-time GPS location tracking for family members
- Configurable geofencing with Haversine-formula breach detection
- Multi-tenant cluster architecture (one isolated DB per cluster)
- Family member management, chat, call sessions, calendar events
- Device monitoring, phone outage detection, notification services
- Administrative dashboard with analytics
- Mobile application (Year 2)

### 2.2 Why This PM Hub Was Needed

The programme spans 52 sprints across 8 quarters, 8 squads, and 58 team members with full dual-database support (Oracle 19c and SQL Server 2019). A navigable, self-contained reference tool was required to document all agile artefacts — stories, tests, designs, DB scripts, issues, and releases — in a single interface accessible to all stakeholders.

---

## 3. Programme-Level Requirements

### 3.1 Delivery Timeline

| Requirement ID | Requirement |
|---|---|
| PRG-001 | The programme shall deliver across 52 sprints (2-week each), totalling 104 weeks / 2 years |
| PRG-002 | Sprints shall be grouped into 8 quarters: Q1–Q8 |
| PRG-003 | Q1 covers sprints 1–6 (weeks 1–12): Foundation & Authentication |
| PRG-004 | Q2 covers sprints 7–12 (weeks 13–24): Location Core |
| PRG-005 | Q3 covers sprints 13–19 (weeks 25–38): Family & Social |
| PRG-006 | Q4 covers sprints 20–26 (weeks 39–52): Devices & Polish — Year 1 Release |
| PRG-007 | Q5 covers sprints 27–32 (weeks 53–64): Mobile & API v2 |
| PRG-008 | Q6 covers sprints 33–38 (weeks 65–76): Real-Time & Scale |
| PRG-009 | Q7 covers sprints 39–44 (weeks 77–88): Advanced Features |
| PRG-010 | Q8 covers sprints 45–52 (weeks 89–104): Hardening & GA Release |

### 3.2 Team Structure

| Requirement ID | Requirement |
|---|---|
| TEAM-001 | 58 team members in total across all roles |
| TEAM-002 | 8 delivery squads (A–H), each with 4 developers = 32 developers |
| TEAM-003 | 6 QA Engineers (4 domain-aligned + 2 floating regression/performance) |
| TEAM-004 | 6 Business Analysts (1 per domain area) |
| TEAM-005 | 10 management and leadership roles (2 PM + 2 RM + 6 managers incl. Executive Sponsor) |
| TEAM-006 | 4 Implementation Engineers covering dev, staging, production Year 1 & Year 2 |
| TEAM-007 | Squad A: Auth, Users, Cluster Management |
| TEAM-008 | Squad B: Location Tracking, Haversine Engine |
| TEAM-009 | Squad C: Location History, Map, Visiting Locations |
| TEAM-010 | Squad D: Family Members, Dashboard Stats |
| TEAM-011 | Squad E: Chat, Call Sessions, SignalR |
| TEAM-012 | Squad F: Calendar, Frequent Locations, Devices |
| TEAM-013 | Squad G: Phone Outage, Config, Notifications |
| TEAM-014 | Squad H: Infrastructure, CI/CD, DB Migrations, Security |

### 3.3 Release Milestones

| Milestone | Sprint | Week | Go/No-Go Gate |
|---|---|---|---|
| Alpha | 6 | 12 | All Q1 stories accepted; Oracle + SQL Server migrations clean; 80% unit test coverage on auth module; health endpoint < 200ms |
| Beta 1 | 12 | 24 | Location ingestion tested on both DB platforms; Haversine accuracy verified; map view < 3s; Q2 regression passed |
| Beta 2 | 19 | 38 | Chat delivery tested; calendar CRUD verified; family scope isolation tested; no open Critical bugs |
| RC 1 / Year 1 Release | 26 | 52 | OWASP audit complete; full regression pass; deployment runbook signed off; training docs delivered; PM + QA Manager go/no-go |
| Mobile Beta | 32 | 64 | Mobile app submitted to store; API v2 backward compat verified; push notifications working; beta cohort onboarded |
| RC 2 | 44 | 88 | SignalR load test 1,000 concurrent connections; polygon geofence verified; analytics data accurate |
| GA Release | 52 | 104 | SOC 2 evidence collected; WCAG 2.1 AA verified; DR plan tested; UAT feedback resolved; hypercare plan active |

---

## 4. Claft Application Technical Requirements

### 4.1 Architecture

| Requirement ID | Requirement |
|---|---|
| ARCH-001 | The application shall use ASP.NET Core 8 MVC as the default web framework |
| ARCH-002 | The web layer shall use Razor Views (.cshtml) for server-rendered content |
| ARCH-003 | Cookie-based authentication shall be used for session management |
| ARCH-004 | SignalR Hubs shall be used for real-time communication (chat, live location) |
| ARCH-005 | Entity Framework Core 8 shall be the data access layer |
| ARCH-006 | A `DB_PROVIDER` environment variable shall switch between Oracle and SQL Server at runtime |
| ARCH-007 | `ClusterConnectionService` shall route each user to their isolated cluster database |
| ARCH-008 | All API endpoints shall respond identically regardless of which database provider is active |

### 4.2 Database Requirements

| Requirement ID | Requirement |
|---|---|
| DB-001 | Oracle 19c shall be the primary production database |
| DB-002 | SQL Server 2019 shall be the alternate/secondary database |
| DB-003 | All DDL scripts shall be provided for both Oracle and SQL Server |
| DB-004 | Rollback scripts (DROP TABLE) shall accompany every forward migration |
| DB-005 | DML example scripts (INSERT/SELECT) shall be produced for each sprint |
| DB-006 | The schema baseline (Sprint 1) shall create all 11 domain tables |
| DB-007 | `claft_users` shall contain at minimum: id, userid, username, password_hash, role, firstname, lastname, address1, city, state, zipcode, phone, email, gender, dob, family_type, home_lat, home_lng, device_name, meid, is_active |
| DB-008 | `claft_location_history` shall record: user_id, lat, lng, is_inside_geofence, accuracy, battery_level, recorded_at |
| DB-009 | Indexes shall exist on: userid, username, user_id, recorded_at columns |
| DB-010 | EF Core migration class names shall follow the pattern `NNN_Theme_Name` |
| DB-011 | A `DB_PROVIDER` env var shall be confirmed on all target environments per sprint implementation checklist |

### 4.3 Feature Requirements by Domain

#### Authentication & Security
| Requirement ID | Requirement |
|---|---|
| AUTH-001 | Users shall log in with username and password validated against the cluster database |
| AUTH-002 | Passwords shall be hashed with BCrypt (minimum cost factor 12) |
| AUTH-003 | Successful login shall issue a cookie with role and cluster ID claims |
| AUTH-004 | Unauthenticated requests to protected routes shall redirect to `/Account/Login` (302) |
| AUTH-005 | Roles shall include at minimum: `user`, `admin`, `superuser` |
| AUTH-006 | OWASP audit shall be completed before RC 1 release |

#### Location Tracking & Geofencing
| Requirement ID | Requirement |
|---|---|
| LOC-001 | The platform shall accept GPS location payloads (lat, lng, accuracy, battery_level) per user |
| LOC-002 | Geofence breach detection shall use the Haversine great-circle distance formula |
| LOC-003 | Default geofence radius shall be 1.0 mile, configurable via application config |
| LOC-004 | On geofence breach, a breach notification shall be sent to family members |
| LOC-005 | Location history shall be stored and queryable per user |
| LOC-006 | Visiting locations (frequent visit detection) shall be tracked from Sprint 11 onwards |
| LOC-007 | A map view shall render within 3 seconds |

#### Real-Time Communication
| Requirement ID | Requirement |
|---|---|
| RT-001 | SignalR shall be used to broadcast live location updates within a cluster group |
| RT-002 | Chat messages shall be delivered in real-time between family members |
| RT-003 | Call session signalling shall be handled via SignalR |
| RT-004 | SignalR shall support 1,000 concurrent connections (validated at RC 2) |
| RT-005 | Users shall be added to a cluster-specific SignalR group on connection |

#### Family & Social
| Requirement ID | Requirement |
|---|---|
| FAM-001 | Family member scoping shall isolate data between clusters |
| FAM-002 | The dashboard shall show total members, members currently inside geofence, and breaches today |
| FAM-003 | Calendar events shall support CRUD operations per family member |
| FAM-004 | Chat message history shall be persisted and retrievable |

#### Infrastructure & DevOps
| Requirement ID | Requirement |
|---|---|
| INFRA-001 | A CI/CD pipeline (GitHub Actions) shall trigger on every PR |
| INFRA-002 | The pipeline shall run `dotnet build` + `dotnet test` and publish results as a PR check |
| INFRA-003 | A Docker image shall be built on merge to main |
| INFRA-004 | A `/health` endpoint shall return 200 `{ status: 'healthy' }` when the DB is connected |
| INFRA-005 | The `/health` endpoint shall return 503 when the DB is unreachable |
| INFRA-006 | The `/health` endpoint shall respond in under 200ms under normal load |

---

## 5. PM Hub Application Requirements

### 5.1 Overview & Scope

The PM Hub is a standalone React single-page application that serves as the interactive reference for the entire 2-year Claft delivery programme. It requires no backend, all data is static and self-contained.

| Requirement ID | Requirement |
|---|---|
| HUB-001 | The hub shall be a standalone React + Vite SPA requiring no backend API |
| HUB-002 | All 52 sprint data points shall be embedded as static data |
| HUB-003 | The application shall be navigable via a persistent dark sidebar |
| HUB-004 | The application shall contain exactly 10 navigation tabs |
| HUB-005 | All content shall update reactively when the user changes sprint selection or technology platform |
| HUB-006 | The application shall be fully functional offline (no external API calls) |
| HUB-007 | The application layout shall use a sidebar + main content split filling 100% of the viewport |

### 5.2 Navigation Tabs — Requirements

#### Tab 1: Overview
| Requirement ID | Requirement |
|---|---|
| OV-001 | Display six KPI stat cards: Sprints (52), Weeks (104), Team Members (58), Story Points (total), User Stories (total), Test Cases (total) |
| OV-002 | Display a Quarter Schedule table mapping Q1–Q8 to sprint ranges, week ranges, and milestone badges |
| OV-003 | Display Squad Domains card listing all 8 squads with colour indicator and domain description |
| OV-004 | Display a Platform Architecture card showing 4 architecture layers (Web, Service, Data, Domain Tables) for the active technology platform |
| OV-005 | Display a Stack Details card showing "Best For" text and advantages for the active technology platform |
| OV-006 | Display the active platform's infrastructure badges |
| OV-007 | The subtitle shall dynamically reflect the active technology platform's backend and databases |

#### Tab 2: Gantt Chart
| Requirement ID | Requirement |
|---|---|
| GANTT-001 | Render an SVG Gantt chart covering all 52 sprints × 104 weeks |
| GANTT-002 | Each sprint bar shall be coloured by squad with multi-squad split colouring |
| GANTT-003 | Quarter backgrounds shall alternate between two shades for visual grouping |
| GANTT-004 | Quarter header labels (Q1–Q8) shall appear above the chart |
| GANTT-005 | Week markers shall appear every 4 weeks |
| GANTT-006 | Milestone sprints shall show a red diamond indicator with label |
| GANTT-007 | A colour legend for all 8 squads shall appear below the chart |
| GANTT-008 | The chart shall be horizontally scrollable for full 104-week visibility |

#### Tab 3: Sprints
| Requirement ID | Requirement |
|---|---|
| SPR-001 | A scrollable list of all 52 sprints shall be shown in the left panel |
| SPR-002 | Selecting a sprint shall display its full detail in the right panel |
| SPR-003 | Sprint detail shall have 7 sub-tabs: Overview, Stories, Tests, Issues, Code, DDD, DB Scripts |
| SPR-004 | Overview sub-tab shall show implementation checklist and HLD (module, purpose, components, external dependencies, NFRs) |
| SPR-005 | Stories sub-tab shall display all user stories in "As a / I want / so that" format with acceptance criteria, priority, points, and squad |
| SPR-006 | Tests sub-tab shall display unit tests (class, method, scenario, expected) and system tests (feature, precondition, steps, expected, environment) |
| SPR-007 | Issues sub-tab shall display all logged bugs with severity, component, description, steps, reporter, assignee, and status |
| SPR-008 | Code sub-tab shall display new files (green), modified files (orange), and the cumulative file tree |
| SPR-009 | DDD sub-tab shall display platform-aware pseudo-code, sequence diagram, data flow, error handling, and configuration dependencies |
| SPR-010 | DB Scripts sub-tab shall display platform-specific forward migration scripts (2 columns) and the migration apply command |
| SPR-011 | Milestone sprints shall show a red milestone badge in the list and header |

#### Tab 4: Teams
| Requirement ID | Requirement |
|---|---|
| TEAMS-001 | Display summary stat cards for each role category (Developers 32, QA 6, BA 6, Management 10, Implementation 4, Total 58) |
| TEAMS-002 | Display individual squad cards for all 8 squads showing member names |
| TEAMS-003 | Display a management/QA/BA/implementation members grid with name and role |

#### Tab 5: Requirements
| Requirement ID | Requirement |
|---|---|
| REQ-001 | Display all user stories across all 52 sprints in a filterable, searchable table |
| REQ-002 | Stories shall be filterable by priority: All, Must, Should, Could |
| REQ-003 | Stories shall be filterable by squad (A–H) |
| REQ-004 | Stories shall be searchable by title or story ID |
| REQ-005 | Table columns: ID, Sprint, Title, Role, Priority, Points, Squad |
| REQ-006 | Story count displayed in the section subtitle shall update based on active filters |

#### Tab 6: Design Docs
| Requirement ID | Requirement |
|---|---|
| DD-001 | A scrollable sprint list in the left panel allows sprint selection |
| DD-002 | Two sub-tabs shall be available: HLD and DDD + Pseudo-code |
| DD-003 | HLD view shall show module name, purpose, component breakdown, external dependencies, and NFRs |
| DD-004 | DDD view shall show platform-aware pseudo-code, sequence diagram (SVG), data flow, error handling, and configuration dependencies |
| DD-005 | Pseudo-code shall display a platform indicator badge (platform name + language) |

#### Tab 7: Database Scripts
| Requirement ID | Requirement |
|---|---|
| DBSCR-001 | A scrollable sprint list in the left panel allows sprint selection |
| DBSCR-002 | Two views shall be available: Forward Migration and Rollback |
| DBSCR-003 | Forward migration view shall show two platform-specific DB script panels side by side |
| DBSCR-004 | The platform's primary and secondary databases shall be identified in the header |
| DBSCR-005 | Each script panel shall use the platform's appropriate background and foreground colours |
| DBSCR-006 | The apply migration command shall be shown for the active technology platform |
| DBSCR-007 | The migration class name (from EF Core / Flyway / Alembic etc.) shall be displayed |

#### Tab 8: Test Cases
| Requirement ID | Requirement |
|---|---|
| TC-001 | Display all unit tests and system tests across all 52 sprints |
| TC-002 | Filterable by sprint and by type (All, Unit, System) |
| TC-003 | Unit test table columns: ID, Sprint, Class, Method, Scenario, Expected |
| TC-004 | System test table columns: ID, Sprint, Feature, Precondition, Steps, Expected, Environment |
| TC-005 | Total counts for unit and system tests shall be shown in the section subtitle |
| TC-006 | Performance limit: show up to 200 rows per table (pagination via filter) |

#### Tab 9: Issues
| Requirement ID | Requirement |
|---|---|
| ISS-001 | Display all defects/bugs across all 52 sprints in a filterable table |
| ISS-002 | Summary stat cards shall show Open, In Progress, Resolved, and Total counts |
| ISS-003 | Filterable by severity: All, Critical, Major, Minor, Trivial |
| ISS-004 | Filterable by status: All, Open, In Progress, Resolved |
| ISS-005 | Table columns: ID, Sprint, Severity, Component, Description, Reporter, Assignee, Status |
| ISS-006 | Severity badges shall be colour-coded: Critical=red, Major=orange, Minor=yellow, Trivial=slate |

#### Tab 10: Releases
| Requirement ID | Requirement |
|---|---|
| REL-001 | Display all 7 milestone releases on a vertical timeline |
| REL-002 | Each milestone shall show name, sprint, week, scope summary, and go/no-go criteria checklist |
| REL-003 | The GA Release milestone shall be visually distinguished (green indicator) |
| REL-004 | Sprint and Week badges shall be shown for each milestone |

---

## 6. Technology Platform Feature Requirements

### 6.1 Platform Dropdown

| Requirement ID | Requirement |
|---|---|
| PLAT-001 | A "Technology Platform" sub-menu shall appear under "Overview" in the sidebar |
| PLAT-002 | The sub-menu shall be collapsible/expandable by clicking "Overview" |
| PLAT-003 | A ▼/▲ chevron on the Overview button shall indicate the collapsed/expanded state |
| PLAT-004 | Each platform option shall show a colour-coded dot and its short name |
| PLAT-005 | The selected platform shall be visually highlighted with a coloured left border and dark background |
| PLAT-006 | The active platform's short name, database, and language shall be shown in the sidebar footer |
| PLAT-007 | Selecting a platform shall navigate to Overview and update all platform-aware content immediately |

### 6.2 The 7 Supported Technology Platforms

| # | Platform ID | Name | Backend Language | Primary DB |
|---|---|---|---|---|
| 1 | `microsoft` | Microsoft Enterprise Stack | C# / ASP.NET Core 8 | SQL Server 2019 |
| 2 | `java` | Java Enterprise Stack | Java / Spring Boot 3 | Oracle Database |
| 3 | `cloud-native` | Modern Cloud-Native Stack | TypeScript / NestJS | PostgreSQL 16 |
| 4 | `opensource` | Open Source Enterprise Stack | Python / Django 5 | PostgreSQL 16 |
| 5 | `highperf` | High-Performance Distributed Stack | Go 1.22 / Rust | Apache Cassandra 5 |
| 6 | `sap` | SAP-Centric Enterprise Stack | ABAP / Java | SAP HANA 2.0 |
| 7 | `hybrid` | Hybrid Enterprise Stack | Java + Node.js + Python | PostgreSQL + Redis + Kafka |

### 6.3 Per-Platform Configuration Requirements

Each platform configuration shall include:

| Requirement ID | Requirement |
|---|---|
| PLAT-CFG-001 | Unique ID, display name, short name, and brand colour |
| PLAT-CFG-002 | Operating system, backend framework, backend language, frontend technology |
| PLAT-CFG-003 | Primary database and secondary database labels |
| PLAT-CFG-004 | Infrastructure stack (list of tools/services) |
| PLAT-CFG-005 | 4-layer architecture definition: Web Layer, Service Layer, Data Layer, Domain Tables |
| PLAT-CFG-006 | "Best For" text describing ideal use cases |
| PLAT-CFG-007 | List of advantages (4 bullet points minimum) |
| PLAT-CFG-008 | `getPseudoCode(theme, sprintNum)` function returning language-appropriate pseudo-code |
| PLAT-CFG-009 | `getDbScript(theme, sprintNum, oracle, sqlserver)` function returning platform-adapted DB scripts |

### 6.4 Pseudo-Code Generation Requirements

| Requirement ID | Requirement |
|---|---|
| PC-001 | Pseudo-code shall be generated for at minimum the following theme categories: auth, user management, location/geofencing, chat/messaging, real-time/SignalR, analytics/dashboard, infrastructure/CI/CD |
| PC-002 | A generic fallback template shall be generated for any theme not explicitly mapped |
| PC-003 | The pseudo-code language shall match the platform's backend language |
| PC-004 | Code shall show realistic patterns for the platform (e.g., `@RestController` for Java, `@Injectable()` for NestJS, `METHOD/ENDMETHOD` for ABAP) |
| PC-005 | A platform indicator badge (platform name + language) shall appear in the pseudo-code panel header |

### 6.5 Database Script Adaptation Requirements

| Platform | Primary Script Type | Secondary Script Type | Apply Command |
|---|---|---|---|
| MS Enterprise | SQL Server 2019 | Oracle 19c | `dotnet ef database update` |
| Java Enterprise | Oracle (Flyway) | PostgreSQL (Flyway) | `mvn flyway:migrate` |
| Cloud-Native | PostgreSQL (Prisma) | MongoDB schema + Redis key patterns | `npx prisma migrate deploy` |
| Open Source | PostgreSQL (Alembic) | Django models reference | `alembic upgrade head` |
| High-Perf Go | Cassandra CQL | Kafka topic config + PostgreSQL | `cqlsh < schema.cql` |
| SAP Enterprise | SAP HANA Column DDL | ABAP Dictionary transport | ABAP Transport (STMS) |
| Hybrid | PostgreSQL (raw SQL) | Kafka Schema Registry + Redis patterns | `psql $DATABASE_URL -f *.sql` |

---

## 7. Data Model Requirements (PM Hub)

### 7.1 Sprint Data Structure

Each of the 52 sprint objects shall contain:

| Field | Type | Description |
|---|---|---|
| `number` | integer | Sprint number 1–52 |
| `theme` | string | Sprint theme/feature name |
| `quarter` | string | Quarter label (e.g., "Q1 – Foundation & Auth") |
| `squads` | string[] | Participating squad IDs (A–H) |
| `points` | integer | Total story points for the sprint |
| `milestone` | string (optional) | Milestone name if applicable |
| `stories` | Story[] | User stories for this sprint |
| `unitTests` | UnitTest[] | Unit test specifications |
| `systemTests` | SystemTest[] | System/integration test specifications |
| `issues` | Issue[] | Bugs and defects logged in this sprint |
| `dbScript` | DbScript | Oracle + SQL Server forward/rollback/DML scripts |
| `codeArtifacts` | CodeArtifact | New files, modified files, cumulative file tree |
| `hld` | HLD | High-Level Design (module, purpose, components, deps, NFRs) |
| `ddd` | DDD | Domain Design (pseudo-code, sequence diagram, data flow, error handling) |
| `implChecklist` | string[] | Pre-release checklist items |

### 7.2 Sprint Themes (All 52 Sprints)

| Q | Sprints | Theme Focus |
|---|---|---|
| Q1 | 1–6 | Project Bootstrap, DB Schema, Auth, User Management, Cluster Management, Config & Health |
| Q2 | 7–12 | Location Ingestion, Geofence, Live Locations API, Location History, Map View, Visiting Locations |
| Q3 | 13–19 | Family Members, Dashboard Stats, Chat Messaging, Call Sessions, Calendar Events, Devices, Phone Outage |
| Q4 | 20–26 | Frequent Locations, Notifications, DB Performance Tuning, Security Hardening, Admin Tools, Multi-Region Clusters, Year 1 Release |
| Q5 | 27–32 | Mobile App Skeleton, Mobile Auth, Mobile Location, Mobile Chat & Push, REST API v2, SignalR Foundation |
| Q6 | 33–38 | Real-Time Location Broadcast, Advanced Geofence, Cluster Analytics, DB Archival, Multi-Language i18n, CI/CD Hardening |
| Q7 | 39–44 | Offline Mode, Predictive Alerts, Family Groups v2, SOC 2 Compliance, Accessibility WCAG, Performance Tuning |
| Q8 | 45–52 | Export & Reporting, Admin Super Dashboard, DR Plan, Load Testing, UAT Fixes, Documentation, Staging Final, GA Release |

### 7.3 User Story Data Structure

| Field | Type | Description |
|---|---|---|
| `id` | string | Unique ID (e.g., CLF-001) |
| `title` | string | Short story title |
| `role` | string | As a [role] |
| `want` | string | I want [feature] |
| `benefit` | string | so that [benefit] |
| `ac` | string[] | Acceptance criteria (minimum 4 per story) |
| `points` | integer | Story points: 2, 3, 5, or 8 |
| `squad` | string | Responsible squad (A–H) |
| `priority` | enum | Must / Should / Could |

### 7.4 Test Data Structure

**Unit Test fields:** id, class (cls), method, scenario, expected  
**System Test fields:** id, feature, precondition, steps, expected, environment  
**Environment values:** `dev`, `staging`, `staging-oracle`, `staging-sqlserver`, `CI`

### 7.5 Issue / Defect Data Structure

| Field | Type |
|---|---|
| `id` | string (BUG-SNN-NNN) |
| `severity` | Critical / Major / Minor / Trivial |
| `component` | string |
| `description` | string |
| `steps` | string |
| `reporter` | string |
| `dev` | string (assignee) |
| `status` | Open / In Progress / Resolved |

### 7.6 Milestone Data Structure

| Field | Description |
|---|---|
| `name` | Milestone name |
| `sprint` | Sprint number it falls on |
| `week` | Week number it falls on |
| `scope` | Scope summary text |
| `goNoGo` | Array of go/no-go criteria strings |

---

## 8. Non-Functional Requirements

### 8.1 Performance

| Requirement ID | Requirement |
|---|---|
| NFR-P-001 | The PM Hub shall load and render fully in under 3 seconds on a standard broadband connection |
| NFR-P-002 | Switching between tabs shall be instantaneous (client-side only) |
| NFR-P-003 | Switching technology platforms shall re-render all affected content in under 100ms |
| NFR-P-004 | The Gantt SVG chart covering 104 weeks × 52 sprints shall render without jank |
| NFR-P-005 | The requirements backlog table (600+ stories) shall filter in real-time without perceptible delay |

### 8.2 Scalability (Underlying Claft Platform)

| Requirement ID | Requirement |
|---|---|
| NFR-S-001 | All API endpoints shall respond within 500ms |
| NFR-S-002 | All endpoints shall behave identically regardless of DB provider (Oracle vs SQL Server) |
| NFR-S-003 | The geofence service shall handle high-frequency location pings without DB bottlenecks |
| NFR-S-004 | SignalR shall support 1,000 concurrent connections (validated at RC 2) |

### 8.3 Security (Underlying Claft Platform)

| Requirement ID | Requirement |
|---|---|
| NFR-SEC-001 | Passwords shall be stored as BCrypt hashes, never plaintext |
| NFR-SEC-002 | All protected routes shall require authenticated session |
| NFR-SEC-003 | Role-based access control (RBAC) shall restrict sensitive operations to admin/superuser |
| NFR-SEC-004 | An OWASP security audit shall be completed before RC 1 |
| NFR-SEC-005 | SOC 2 evidence shall be collected before GA Release |
| NFR-SEC-006 | Data shall be isolated between clusters (multi-tenant security boundary) |

### 8.4 Reliability & Operations (Underlying Claft Platform)

| Requirement ID | Requirement |
|---|---|
| NFR-R-001 | A disaster recovery plan shall be tested before GA Release |
| NFR-R-002 | DB rollback scripts shall be provided and tested for every sprint |
| NFR-R-003 | Zero-downtime deployment strategies shall be documented |
| NFR-R-004 | The `/health` endpoint shall enable load balancer liveness probing |

### 8.5 Accessibility & Internationalisation (Underlying Claft Platform)

| Requirement ID | Requirement |
|---|---|
| NFR-A-001 | WCAG 2.1 AA accessibility compliance shall be verified before GA |
| NFR-A-002 | Multi-language (i18n) support shall be delivered in Q6 |

---

## 9. UI / UX Requirements (PM Hub)

### 9.1 Layout & Navigation

| Requirement ID | Requirement |
|---|---|
| UX-001 | The sidebar shall be dark (background `#0F172A`) with a fixed width of 196px |
| UX-002 | Active tab shall be highlighted with a blue accent border (`#2563EB`) and darker background |
| UX-003 | Inactive tab labels shall use muted colour (`#94A3B8`) |
| UX-004 | The main content area shall use a light background (`#F8FAFC`) |
| UX-005 | Cards throughout the app shall have white background, 1px border, and 8px border radius |
| UX-006 | All typography shall use the `Inter` system font stack |
| UX-007 | The layout shall fill 100vh with no page scrollbar on the outer container |

### 9.2 Component Design Standards

| Requirement ID | Requirement |
|---|---|
| UX-008 | Badge components shall support custom background colour and contrasting white text |
| UX-009 | Severity badges: Critical=red, Major=orange, Minor=amber, Trivial=slate |
| UX-010 | Priority badges: Must=red, Should=orange, Could=amber |
| UX-011 | Status badges: Open=red, In Progress=orange, Resolved=green |
| UX-012 | Stat components shall show a large bold number in accent colour with an uppercase label |
| UX-013 | Code blocks shall use dark background (`#0F172A`), monospace font, and light blue text |
| UX-014 | Section headers shall have a large bold title with an optional muted subtitle |

### 9.3 Responsive Behaviour

| Requirement ID | Requirement |
|---|---|
| UX-015 | The Gantt chart shall be horizontally scrollable within its container |
| UX-016 | Sprint detail sub-tabs shall wrap to additional rows on narrow viewports |
| UX-017 | All multi-column grids shall use CSS Grid with defined column templates |

---

## 10. Technology Stack Requirements (PM Hub Tool)

### 10.1 Frontend Framework

| Requirement ID | Requirement |
|---|---|
| TECH-001 | The PM Hub shall be built with React 18+ (functional components + hooks) |
| TECH-002 | TypeScript shall be used throughout (strict mode) |
| TECH-003 | Vite shall be the build tool and dev server |
| TECH-004 | The app shall be a standalone Vite artifact running on port from the `PORT` environment variable |
| TECH-005 | The dev server shall bind to `0.0.0.0` to support proxy-based preview environments |

### 10.2 State Management

| Requirement ID | Requirement |
|---|---|
| TECH-006 | React `useState` and `useMemo` shall be used for local and derived state |
| TECH-007 | React Context (`createContext` / `useContext`) shall propagate the active technology platform to all tabs |
| TECH-008 | No external state management library (Redux, Zustand, etc.) is required |

### 10.3 Data Layer

| Requirement ID | Requirement |
|---|---|
| TECH-009 | All sprint data (52 sprints) shall be statically generated in `data.ts` |
| TECH-010 | Sprints 1–2 shall be fully hand-authored with complete, specific data |
| TECH-011 | Sprints 3–52 shall be generated by the `generateSprints()` function with theme-appropriate content |
| TECH-012 | Platform configuration data shall live in `platformData.ts` as a separate module |
| TECH-013 | TypeScript interfaces in `types.ts` shall define Sprint, Story, UnitTest, SystemTest, Issue, DbScript, CodeArtifact, HLD, DDD, TeamMember, Squad, and Milestone |
| TECH-014 | Platform interfaces in `platformData.ts` shall define PlatformArch, DbScriptPair, and TechPlatformConfig |

### 10.4 Styling

| Requirement ID | Requirement |
|---|---|
| TECH-015 | All styling shall use inline React style objects (no Tailwind class dependencies in component logic) |
| TECH-016 | The design system colours shall be defined as constants: NAV_BG `#0F172A`, ACCENT `#2563EB`, CONTENT_BG `#F8FAFC`, BORDER `#E2E8F0`, MUTED `#64748B` |
| TECH-017 | SVG shall be used for the Gantt chart and sequence diagrams (rendered inline in React) |

### 10.5 File Structure

```
artifacts/claft-pm-hub/
├── src/
│   ├── App.tsx              # Root — renders <ClaftPMHub />
│   ├── ClaftPMHub.tsx       # Main component (~1,150 lines)
│   ├── data.ts              # All sprint data (52 sprints, ~1,182 lines)
│   ├── types.ts             # TypeScript interfaces
│   ├── platformData.ts      # 7 technology platform configs (~1,649 lines)
│   ├── main.tsx             # React DOM entry point
│   └── index.css            # Minimal CSS with HSL colour variables
├── vite.config.ts
├── tsconfig.json
├── package.json
└── REQUIREMENTS.md          # This document
```

---

## 11. Traceability Summary

| Requirement Category | Count |
|---|---|
| Programme-level (timeline, team, milestones) | 24 |
| Claft application technical (architecture, DB, features) | 42 |
| PM Hub application (overview, tabs, navigation) | 68 |
| Technology platform feature (dropdown, 7 platforms, pseudo-code, DB) | 31 |
| Data model (sprint structure, stories, tests, issues) | 22 |
| Non-functional (performance, security, reliability, accessibility) | 21 |
| UI/UX (layout, components, responsive, design system) | 17 |
| Technology stack (framework, state, data, styling, files) | 17 |
| **Total Requirements** | **242** |

---

## 12. Assumptions & Constraints

| ID | Assumption / Constraint |
|---|---|
| AC-001 | The PM Hub is a documentation and planning reference tool — it does not connect to any live Claft system |
| AC-002 | All sprint data is representative and designed to be realistic, not pulled from a live project management system |
| AC-003 | The Technology Platform switcher adapts documentation language and DB scripts only — it does not change the underlying Claft sprint structure (stories, teams, timelines remain the same) |
| AC-004 | The dual-database requirement (Oracle + SQL Server) is a hard constraint inherited from the Claft platform specification |
| AC-005 | BCrypt cost factor ≥ 12 is a security baseline; higher values may be used in production |
| AC-006 | The Haversine geofence implementation assumes a spherical Earth model; this is accurate to within 0.3% for distances relevant to family location tracking |
| AC-007 | The PM Hub must work in modern browsers (Chrome, Firefox, Edge, Safari) without any polyfills |
| AC-008 | No authentication is required for the PM Hub itself — it is an internal reference tool |

---

*End of Requirements Specification*
