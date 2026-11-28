# Contributing

This project follows a unified development workflow shared across the
maintainer's repositories (adopted from aegis-app/aegis-bot).

## Development workflow

1. **Open an issue first.** Every change starts with an issue (use the
   issue templates). Describe the purpose, the planned work, and the
   definition of done.
2. **Create a branch from `main`** named `<type>/<issue-number>-<slug>`,
   e.g. `feat/12-test-command`, `fix/9-summary-resend`, `docs/3-readme`.
3. **Implement and test.** Run the project's build and test commands
   locally; both must pass.
4. **Open a pull request.** Fill in the PR template and link the issue
   with `Closes #<number>`.
5. **Merge with "Rebase and merge"** to keep the history of `main`
   linear, then delete the branch.

## Commit messages

Commit messages use **Conventional Commits with English subjects**:

```
feat: add master data counts display
fix: persist the posted date to stop re-sending after restarts
docs: update documentation for v1.1.1
```

Types in use: `feat`, `fix`, `docs`, `test`, `chore`, `refactor`.

## Releases

Releases are cut from `main`: bump the version, tag `vX.Y.Z`, and publish
a GitHub Release with notes only (no binaries are attached), unless the
project documents otherwise.

For this project the release build is a one-command build (`build.bat`
on Windows, `./build.sh` on Linux) run by the maintainer; the GitHub
Release itself carries notes only.

## Build and test (this repository)

- Requirements: .NET SDK 8.
- Test (logic-layer fixture tests, runs on any OS):
  `dotnet run --project src/AegisTest/AegisTest.csproj -c Release`
- One-command build (tests + single-file exe publish):
  `build.bat` (Windows) / `./build.sh` (Linux cross-build).
- CI runs on `windows-latest`: it executes the same test project and
  publishes the exe as a workflow artifact for verification.

## Notes

- Never commit secrets. The A2S token and other user settings live in
  `%AppData%\AEGIS\settings.json` on the user's machine, never in this
  repository.
- The logic layer (`Data.*.cs`, `A2sStream.cs`, `Settings.cs`) must stay
  GUI-independent so the fixture tests can exercise it on any OS.
