# Synthetic protocol tests

Run on Windows using PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
```

No package installation, Codex installation, account, API key, or network connection is required. The script compiles the production source together with a test entry point and a separate fake JSON-RPC server using the .NET Framework C# compiler. The fake executable is passed explicitly to `NativeClient`; the real Codex home is never used.

Each scenario uses a separate synthetic home under one unique temporary directory. The script checks the exact directory and its marker before removing it. To retain a JSON summary outside the temporary directory, add `-ReportPath` with a file path whose parent directory already exists.

Coverage includes:

- Pagination over 235 unique histories, duplicate rows, and a repeated cursor.
- Cancellation before a request or after a list response, without further pages or mutations.
- Rejection of malformed lists, missing IDs, unsupported metadata, and invalid cursors.
- Root user histories, metadata fallback, subagent exclusion, and missing records.
- Rejection of methods that could start model turns before they reach the server.
- Archive and restore, unchanged fixture bytes, and native list verification fallback.
- Active writers, explicit errors, malformed mutation responses, unknown final state, and server disconnects without automatic retries.

These are isolated protocol and client tests. They do not establish compatibility with a new Codex release, exercise its real storage implementation, or test the Windows GUI. A release still needs a manual check with the supported Codex version using disposable histories.
