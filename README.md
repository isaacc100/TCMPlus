# TCM+

Desktop Treatment Centre Manager built with C#, Avalonia 12, and SQLite.

## Run

```powershell
dotnet restore TCMPlus.slnx
dotnet run --project src/TCMPlus.App
```

The app starts by naming the shift and setting its required six-digit PIN. It then creates a session database in a safely named shift folder under the platform local application-data directory. Existing sessions are retained for the forthcoming open-session workflow.

## Updates

Installed releases check the public GitHub Releases feed for an update at the Start Shift screen and can be checked manually from Settings. TCM+ always asks before downloading, installing, and restarting; it never applies an update while a host or terminal session is active. The first updater-enabled release must be installed manually because earlier ZIP distributions cannot self-update. See [release instructions](docs/RELEASING.md).

## Current TCM scope

- Fixed-aspect Map view that scales with its window, uses grid-unit geometry, and supports move plus four-corner resize in edit mode.
- Tables view with the same station occupancy data and patient actions; patient UIDs remain backend-only.
- Per-shift mobile teams remain outside the positioned map while supporting callsigns, notes, optional deployment locations, one patient, and station handovers from the Map and Tables manager views.
- Dashboard with live shift summaries, patient lifecycle activity, presenting-complaint breakdown, discharge throughput, and discharge-duration trends.
- PIN-protected LAN web display with live read-only Dashboard and positioned Map mirror pages, started from Settings → Displays.
- Secure app-to-app LAN terminals: one authoritative TCM+ host owns the shift database while other desktop instances discover it automatically and connect after the host operator enters the terminal's six-digit approval code.
- Remote operational commands are serialized and idempotent, with encrypted offline queuing, host-side revalidation, explicit conflict rejection, and a non-clinical audit trail.
- Map and Tables edit modes for station management, plus Setup for shift details and a six-digit, salted-hash shift PIN.
- Top-level Dashboard and application-settings placeholders, an F11 fullscreen mode, and a lock screen that requires the current shift PIN to return to the application.
- One active patient per station or mobile team in the UI. Patients have a backend-only UID, sequential shift counter, optional presenting complaint, arrival time, current assignment, and discharge time. Available map cards add patients; occupied counters can transfer patients between stations or deployed teams, while station-to-station swaps retain confirmation.

The former Vite prototype is retained locally in `legacy-web/` and intentionally ignored by Git.
