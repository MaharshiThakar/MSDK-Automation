# Agent Instructions — Unity Movement

A Unity package (UPM, installable via git URL) that exposes Body, Eye, and Face Tracking via OpenXR for Quest. Sample scenes live under `Samples~/`.

## Source-of-truth files (read these first, do not duplicate their contents in this file)

For setup, build steps, SDK versions, and project layout, read:

- `README.md` — official setup, requirements, Unity scene wiring
- `package.json` — UPM package id, version, dependencies (Meta XR Core / Interaction SDK)
- `CHANGELOG.md` — per-version changes
- `Samples~/` — sample scene assets (imported via Package Manager → Samples)
- `Runtime/`, `Editor/`, `Shared/` — package source
- `LICENSE.md` and `NOTICE` — license terms

## Quest / Horizon-specific notes

- This is a **UPM package**, not a standalone Unity project — install via `Package Manager → Add package from git URL` pointing at this repo, then import scenes from the Samples tab. There is no `ProjectSettings/` to open.
- Sample scenes require layer indices **10**, **11**, and a layer named **HiddenMesh** to exist in the host project, or `RecalculateNormals` will silently misbehave.
- OVRManager must have Body / Face / Eye Tracking enabled in both Quest Features and Permission Requests On Startup, with Tracking Origin = Floor Level and Body Tracking Fidelity = High / Joint Set = Full Body.
- After importing samples, the `SceneSelectMenu` only works if the imported scenes are added to Build Settings.

# Meta Quest tooling

This is a Meta Quest / Horizon OS sample. The bespoke intro above is the source of truth for what this project is and how it's built — use it (and the files it points at) instead of restating facts from memory.

When the user asks anything about Quest device behavior, build / deploy / debug / capture flows, on-device performance, or Horizon OS APIs, reach for these tools instead of generic Unity answers:

- **`hzdb`** — Quest-aware ADB wrapper (device list, install / launch / stop, logs, screenshots, Perfetto traces, on-device docs search). Already wired up as an MCP server via `.mcp.json`, `.vscode/mcp.json`, and `.cursor/mcp.json`. Also runnable directly: `npx -y @meta-quest/hzdb <subcommand>`.
- **Meta Quest Agentic Tools** — the full skill set, including Unity-specific skills: <https://github.com/meta-quest/agentic-tools>. Install per your client (Claude Code: `/plugin install meta-vr@meta-quest`; Gemini CLI: `gemini extensions install https://github.com/meta-quest/agentic-tools`; Cursor / VS Code: install the **Meta Horizon** extension from the Marketplace).

A few behavior expectations:

- **Read this repo's files first.** Before answering anything project-specific, read `README.md` and whichever source-of-truth files the intro above points at. Don't restate their contents in chat — quote or link instead.
- **Use `hzdb` for device-side work.** Anything that touches an attached Quest (install, launch, logs, screenshot, capture, manifest inspection) goes through `hzdb`, not raw `adb`.
- **Check live Horizon OS docs before answering API questions.** `hzdb docs search "..."` queries the live docs; training data on Horizon OS APIs goes stale fast.
- **Don't fabricate SDK / engine versions.** If a version isn't visible in this repo's files, say so rather than guessing.
