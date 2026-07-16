# MiruPlay Windows Engineering Principles

## User Experience

- Design the desktop client as a native Windows application using Fluent Design conventions. Reuse product concepts from Android, but do not reproduce Android or TV layouts pixel for pixel.
- Keep the WPF UI dependency-light. Prefer the shared resource dictionary, standard WPF controls, Segoe UI Variable, Fluent icons, and native Windows behavior before adding UI packages.
- Preserve keyboard focus, readable contrast, stable layouts, and AutomationProperties names when changing controls.

## WebDAV

- Accept both HTTP and HTTPS WebDAV endpoints. HTTP is a required use case and must not be restricted to loopback addresses.
- Keep URL hardening at the trust boundary: reject embedded credentials, query strings, fragments, and non-HTTP(S) schemes.
- Keep credentials in the DPAPI-backed credential store and never persist them in settings, logs, URLs, or command lines.
- Reuse stored credentials only for the same normalized WebDAV authority. HTTP credentials are cleartext on the network, so the UI should state that they are appropriate only on trusted networks.

## Branding

- The Android launcher resources under `C:\WorkSpace\Android\MiruPlay\app\src\main\res` are the canonical MiruPlay app icon source.
- Windows icon updates must preserve that artwork and provide a multi-size `.ico` used by the window, executable, installer, and shortcuts.

## Changes And Validation

- Fix shared behavior at its common entry point and keep diffs scoped; do not duplicate guards across callers or add speculative abstractions.
- Add or update the smallest test that proves non-trivial behavior. Before completion, run focused tests, the full test suite, and a Release build.
- Do not weaken credential isolation, bounded downloads, path traversal checks, or other security controls while changing protocol support or UI behavior.
