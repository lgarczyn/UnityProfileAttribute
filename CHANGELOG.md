# Changelog

## [1.0.0] - 2026-07-13

First release.

- `[Profile]` weaves a `ProfilerMarker` around a method body at compile time.
- `[Profile("name")]` for an explicit marker name, so overloads can share one row.
- `[Profile(deep: true)]` also wraps calls into the project's own assemblies.
- The marker is skipped when nothing is recording, via `ProfileState`.
- `async`, iterator, Burst and by-ref-returning methods are skipped with a warning.
- Release builds are untouched.
