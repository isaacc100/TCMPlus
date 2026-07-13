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
- Dashboard with live shift summaries, patient lifecycle activity, presenting-complaint breakdown, discharge throughput, and discharge-duration trends.
- PIN-protected LAN web display with live read-only Dashboard and positioned Map mirror pages, started from Settings → Displays.
- Map and Tables edit modes for station management, plus Setup for shift details and a six-digit, salted-hash shift PIN.
- Top-level Dashboard and application-settings placeholders, an F11 fullscreen mode, and a lock screen that requires the current shift PIN to return to the application.
- One active patient per station in the UI. Patients have a backend-only UID, sequential shift counter, optional presenting complaint, arrival time, station, and discharge time. Available map cards add patients; occupied counters can transfer patients between stations and confirm swaps.

The former Vite prototype is retained locally in `legacy-web/` and intentionally ignored by Git.
