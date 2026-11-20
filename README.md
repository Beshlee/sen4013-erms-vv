# SEN4013 — Verification & Validation of ERMS

Course project for **SEN4013 Software Verification and Validation** (Fall 2026).
We verify and validate **ERMS (Employee Request Management System)**, a .NET 8 REST API,
by designing test cases with several testing approaches and measuring how much of its
business logic they cover.

## System under test

ERMS lets employees create requests (leave, expense, …), managers approve or reject them,
and admins manage users, departments and request types. It is a layered ASP.NET Core API:

```
src/
├── ERMS.Api/             # Controllers, middleware, DI composition root
├── ERMS.Application/     # Services + validators — the business rules we test
├── ERMS.Domain/          # Entities and enums (RequestStatus, UserRole, …)
└── ERMS.Infrastructure/  # EF Core, repositories, JWT, file storage
tests/
└── ERMS.Tests/           # xUnit + Moq test suite
```

A full description of the system (features, API, Docker setup) is in
[docs/erms-system-overview.md](docs/erms-system-overview.md).

**Origin.** ERMS was developed by Kaan during an internship in August 2026, with AI
assistance, in [Beshlee/erms-backend](https://github.com/Beshlee/erms-backend). This repository
keeps that git history. The **27 unit tests** dated August 2026 existed before this course
project; we use them as the regression baseline and extend them.

**Baseline coverage** (Application layer, 27 existing tests): ≈35% line, ≈30% branch.
`AttachmentService`, `UserService`, `CommentService`, `DepartmentService` and
`RequestTypeService` had no tests.

## Team and testing approaches

| Member | Testing approach | Main target |
|---|---|---|
| Kaan | Structural testing + regression | Test infrastructure, baseline analysis, before/after coverage |
| Nilsu | State transition / model-based | `RequestService` — Draft → Pending → Approved / Rejected / Cancelled |
| Ömer | Boundary value analysis + equivalence partitioning | `AttachmentService` upload rules, all validators |
| Hasan | Decision table / combinatorial | Authorization: role × ownership × request status |

## Getting started

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
The tests mock the database, so no SQL Server is needed to run them.

```bash
# Run the test suite
dotnet test tests/ERMS.Tests/ERMS.Tests.csproj

# Run with coverage (Cobertura XML under ./coverage)
dotnet test tests/ERMS.Tests/ERMS.Tests.csproj --collect:"XPlat Code Coverage" --results-directory coverage
```

To run the API itself (SQL Server LocalDB or Docker), see
[docs/erms-system-overview.md](docs/erms-system-overview.md).

## How we work

- `main` is protected: every change goes through a pull request with **one approval** from
  another team member. No direct pushes, no force pushes.
- Each member works on their own branch, e.g. `test/state-transition-nilsu`, and in their
  own test files. Shared test helpers are changed only by Kaan.
- Test names follow `Method_Condition_ExpectedResult` with Arrange–Act–Assert.
- Test IDs: `REG-xx` regression · `ST-xx` state transition · `BVA-xx` boundary value ·
  `DT-xx` decision table. Every test case is also logged in the team's shared test case table.
- A bug found by a test is logged as an incident (`INC-xx`) and fixed in its own pull request,
  titled `fix: INC-xx …`.
- [CI](.github/workflows/ci.yml) builds the API, runs the tests and publishes a coverage
  report on every push and pull request.

## Deliverables

| Part | Deliverable | Due |
|---|---|---|
| 1 | Review of automated V&V and testing tools (PDF) | 11 Dec 2026, 17:30 |
| 2 | Test cases and results (this repository) | 11 Dec 2026, 17:30 |
| 3 | Project report (PDF) | 11 Dec 2026, 17:30 |
| 4 | Video presentation, max. 5 min per member | 11 Dec 2026, 17:30 |
