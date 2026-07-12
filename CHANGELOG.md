# Changelog

## 0.3.1-DEV

- Fixed a UI-thread deadlock when creating a new shift or initializing its database.

## 0.3.0

- Added encrypted `.tcm` session containers using AES-GCM and PBKDF2-derived session-password keys, with a non-sensitive recent-session catalogue.
- Added recent-shift loading from startup and the lock screen, with rename, delete confirmation, and encrypted export actions.
- Added persisted application discharge-route settings and a normal discharge-route picker; Quick entry now bypasses dialogs and records a null discharge route.
- Added per-shift grid-density settings and persisted compact, standard, and dense map rendering.
- Added nullable persisted discharge routes to patient records and lifecycle handling.
- Corrected top-bar icon content alignment and connected the Settings menu.

## 0.2.0

- Added optional presenting complaints, sequential non-identifying patient counters, discharge timestamps, and persistent patient lifecycle events.
- Added New patient dialogs, map-card patient creation, patient transfers, confirmed occupied-station swaps, and seven-by-seven minimum station geometry.
- Added the Dashboard with patient activity, availability and arrival summaries, complaint breakdown, discharge throughput, and discharge-duration charts.
- Replaced side-panel notices with timed top-centre banners for operational feedback and errors.
- Fixed the Add station and application-lock dialogs so their controls remain within their windows.
- Replaced coloured top-bar emoji controls with white Fluent lock and settings symbols.
- Replaced the browser prototype with the initial Avalonia 12 TCM desktop application.
- Added per-launch SQLite sessions, station map editing, station tables, setup PIN hashing, and version rules.
- Moved station management from Setup into Map and Tables edit modes.
- Added grid-unit station geometry, a responsive fixed-aspect map, four-corner resizing, patient discharge tracking, and Manager-level navigation.
- Added an F11 fullscreen toggle, a live Manager-bar clock, and relative patient arrival times.
- Added a named shift-start screen with mandatory six-digit PIN creation, session folders based on the shift name, and a placeholder existing-shift action.
- Added a screen-blurring application lock with six-box PIN unlock, plus Dashboard and application-settings placeholders in the top navigation.
