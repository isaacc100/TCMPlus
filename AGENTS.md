# TCM+ delivery rules

- Keep the authoritative application version in `Directory.Build.props` as `TCMVersion`.
- Development builds must use `X.Y.Z-DEV`; production builds must use `X.Y.Z`.
- Increment major for breaking changes, minor for new capabilities, and patch for fixes or internal maintenance.
- Update `CHANGELOG.md` whenever a versioned change is made. Move entries from DEV to a dated production release when the `-DEV` suffix is removed.
- Make focused Git commits after a clean build and relevant test run. Inspect `git status` before staging and stage only project files; never stage `legacy-web/` or the nested `treatment-center-manager-plus/` repository.
- Preserve the domain/persistence/UI boundaries: business rules belong outside Avalonia views, and SQLite access belongs in Infrastructure.
- Do not add patient information beyond UID, added time, and current station without an explicit requirement.
