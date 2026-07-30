# Releasing TCM+

Push an annotated tag matching the authoritative `TCMVersion` in `Directory.Build.props`, for example `v0.11.0-DEV` or `v0.11.0`. The release workflow validates the match, runs tests, publishes a self-contained Windows x64 build, packages it with Velopack, and creates the GitHub Release.

Development versions publish as prereleases on `win-x64-dev`; production versions publish on `win-x64-stable`. Future packages must use the same `{platform}-{architecture}-{dev|stable}` channel convention, such as `osx-arm64-stable` or `linux-x64-dev`.

The first Velopack installer must be installed manually. Earlier ZIP-based releases, including `0.10.0-DEV`, do not contain an updater. Later installed releases check GitHub Releases and only download, install, and restart after the user confirms.

Releases are unsigned until signing credentials are available. The workflow reserves these secret names for that work: `WINDOWS_SIGN_CERT_BASE64`, `WINDOWS_SIGN_CERT_PASSWORD`, `APPLE_SIGNING_CERT_BASE64`, `APPLE_SIGNING_CERT_PASSWORD`, `APPLE_ID`, `APPLE_TEAM_ID`, and `APPLE_APP_PASSWORD`.
