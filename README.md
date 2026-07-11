# TCM+

Desktop Treatment Centre Manager built with C#, Avalonia 12, and SQLite.

## Run

```powershell
dotnet restore TCMPlus.slnx
dotnet run --project src/TCMPlus.App
```

The app starts by naming the shift and setting its required six-digit PIN. It then creates a session database in a safely named shift folder under the platform local application-data directory. Existing sessions are retained for the forthcoming open-session workflow.

## Current TCM scope

- Fixed-aspect Map view that scales with its window, uses grid-unit geometry, and supports move plus four-corner resize in edit mode.
- Tables view with the same station occupancy data and patient actions; patient UIDs remain backend-only.
- Map and Tables edit modes for station management, plus Setup for shift details and a six-digit, salted-hash shift PIN.
- Top-level Dashboard and application-settings placeholders, an F11 fullscreen mode, and a lock screen that requires the current shift PIN to return to the application.
- One active patient per station in the UI; each patient stores only UID, added time, and current station. Discharged patients contribute to the session's patients-seen count.

The former Vite prototype is retained locally in `legacy-web/` and intentionally ignored by Git.
