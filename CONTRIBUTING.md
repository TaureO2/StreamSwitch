# Contributing to StreamSwitch

StreamSwitch is developed by Oxigeno. Contributions should keep the application straightforward, readable, and usable in English.

## Development workflow

1. Fork the repository and create a branch for your change.
2. Build with `build.cmd` on Windows.
3. Keep the source compatible with the bundled .NET Framework C# compiler. Avoid language features it cannot compile.
4. Run the simulated tests described in the README. For interface changes, run the UI checks and inspect the window at the minimum supported size.
5. Open a pull request explaining the problem, the resulting behavior, and how you tested it.

Changes to connection handling must preserve request cooldowns, timeouts, cancellation, and credential handling. Use fake transports and dummy values for tests; never add real tokens or account details.

## Bug reports

Include your Windows version, the build or commit, reproduction steps, expected behavior, and the exact non-sensitive error message. Crop or redact account names and private details in screenshots. Never attach tokens or preference files.

## Scope

Prefer focused fixes and clear settings. Discuss new dependencies or major connection changes before implementation. Keep third-party notices intact when using existing artwork. Be respectful and keep discussions relevant to the project.
