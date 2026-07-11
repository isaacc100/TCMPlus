# TCM+

Desktop Treatment Centre Manager built with C#, Avalonia 12, and SQLite.

## Run

```powershell
dotnet restore TCMPlus.slnx
dotnet run --project src/TCMPlus.App
```

Each launch creates a new session database under the platform local application-data directory. Existing sessions are retained for a future open-session workflow.

## Current TCM scope

- Fixed-aspect Map view that scales with its window, uses grid-unit geometry, and supports move plus four-corner resize in edit mode.
- Tables view with the same station occupancy data and patient actions; patient UIDs remain backend-only.
- Map and Tables edit modes for station management, plus Setup for a six-digit, salted-hash shift PIN.
- One active patient per station in the UI; each patient stores only UID, added time, and current station. Discharged patients contribute to the session's patients-seen count.

The former Vite prototype is retained locally in `legacy-web/` and intentionally ignored by Git.
