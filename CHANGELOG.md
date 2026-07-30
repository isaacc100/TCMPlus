# Changelog

## 0.11.0-DEV

- Replaced manual terminal URLs, passwords, and certificate fingerprints with Bluetooth-style LAN discovery and host-approved six-digit pairing.
- Added protocol-v2 multicast, broadcast, host-code, IP-address, and host-name discovery while retaining protocol-v1 operational compatibility.
- Added ephemeral P-256 ECDH pairing with HKDF-SHA256 transcript binding, AES-GCM protected bootstrap credentials, post-approval certificate pinning, two-minute expiry, one-attempt verification, and per-source rate limits.
- Terminal approval now appears immediately on the authoritative host, terminal credentials remain in memory for the current app process only, and shift closure revokes pending and active terminal access.
- Moved persistent offline-queue protection to random per-host keys protected by Windows DPAPI, allowing queued request IDs to survive restarts without preserving host authorization.
- Pairing preferences remember only the terminal name and last host hint; pairing audits exclude verification codes, credentials, certificates, and patient data.
- Added consent-based automatic updates from GitHub Releases, including platform- and architecture-specific release channels, development prerelease support, and safe pre-shift update prompts.
- Added a tag-driven GitHub Actions release workflow that tests, packages, and publishes self-contained Windows Velopack installers and update feeds.
- Existing ZIP-based installations must install the first Velopack release manually; subsequent installed releases update in place.
- This is an unsigned development prerelease and may trigger a Windows publisher warning during its first manual installation.

## 0.10.0-DEV

- Added authoritative-host and desktop-terminal modes for secure multi-app operation over the LAN while keeping SQLite host-only.
- Added versioned HTTPS terminal contracts, certificate-fingerprint verification, shift-scoped hashed terminal credentials, short-lived access tokens, revocation, rate limiting, and protocol negotiation.
- Added serialized and idempotent remote operational commands with host priority, opaque patient references, outcome auditing, and explicit stale-state rejection.
- Added an encrypted offline command queue with ordered replay, reconciliation, and operator-visible rejection reasons.
- Kept terminal enrollment, shift and map administration, patient-record editing, certificate controls, and audit review on the authoritative host.
- Preserved the separate PIN-protected read-only browser display.

## 0.9.1-DEV

- Locked the bordered station map to its native 5:3 aspect ratio at every manager-window size.
- Added direct access to mobile-team management while editing the treatment centre.

## 0.9.0-DEV

- Added per-shift mobile teams with callsigns, notes, optional deployment locations, independently scrolling Map and Tables sections, and Setup management.
- Added one-patient mobile-team assignments with bidirectional station handovers, team discharge and stand-down workflows, and station-only occupancy reporting.

## 0.8.0-DEV

- Added persisted drag-and-drop station reordering to Tables edit mode.
- Added editable patient new/discharge times with lifecycle validation, optional discharge outcomes, and multi-patient presenting-complaint updates.
- Added confirmed patient deletion and confirmed soft deletion of available stations from both the Map and Tables.
- Added 30-second encrypted shift autosaves, abrupt-shutdown recovery from newer healthy working databases, and automatic repair of damaged recent-shift catalogues.
- Added a PIN-protected, read-only LAN web display with live Dashboard and positioned Map mirror views.
- Replaced the primary navigation Safe exit text button with a sign-out icon.
- Excluded blank discharge routes from the dashboard chart and closed the launch window after opening a saved session.
- Removed the Dashboard activity log, added a responsive discharge-route pie chart, and made both dashboard pie charts scale with the window.
- Added external-dashboard occupancy and 15-minute cumulative-arrival charts, plus primary-window fullscreen and safe-exit controls.
- Added a Manager Patients table with every patient record and row-level corrections for presenting complaints and discharged patient routes.
- Added a persisted 4–20px lock-screen blur slider in Settings → General; the default blur is now 10px.

## 0.4.4

- Fixed a lock-screen PIN crash by verifying against the cached active-shift PIN settings before decrypting the sealed database.

## 0.4.3

- Fixed shift start failing after encryption when SQLite connection pooling prevented removal of the decrypted workspace.

## 0.4.2

- Added startup detection for other running TCM+ instances, with explicit options to terminate them and continue or exit the newly started instance.

## 0.4.1

- Fixed the shift-start crash caused by encryption reading a database file held by SQLite's connection pool; startup errors now remain visible in the setup window.

## 0.4.0

- Replaced modal application settings with a top-level Settings area containing General, Operations, and Displays navigation.
- Added editable persistent discharge routes in Settings and Quick entry patient creation without a presenting-complaint dialog.
- Rebuilt the external Map mode as a live, read-only 5:3 positioned treatment-centre mirror with the same station cards, grid geometry, counter, timing, and occupied state.
- Made encrypted session sealing atomic and ensured new shifts produce a loadable `.tcm` file before appearing in recents; active workspaces seal when locked.
- Moved notifications into the top navigation with blue information, yellow warning, and red error status beans.
- Improved Shift setup hierarchy, PIN-change affordance, map-size warning, and Enter/backspace keyboard flows.

## 0.3.2

- Added a persisted external-display mode setting and an action to open a fullscreen Dashboard or read-only treatment-centre mirror on a connected second monitor.
- Added increase-only Compact, Standard, and Dense map-density controls to Shift setup.

## 0.3.1

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
