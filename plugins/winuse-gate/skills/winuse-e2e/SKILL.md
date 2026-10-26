---
name: winuse-e2e
description: Use when you changed the UI of a Windows desktop app and need to verify it in the running app with winuse, or when winuse-gate blocked your turn end or your git commit.
---

# Verify UI changes with winuse

You verify your own UI changes in the running app. The user does not run smoke tests for you.

## What counts

winuse-gate accepts a change once a winuse `assert_text`, `assert_exists`, `assert_window` or `assert_file` call passes (`passed: true`), or a `scenario_run` passes, after your last edit of the changed UI files. Clicking, typing, `act`, `check` and screenshots are not evidence on their own: look, then assert.

## Steps

1. Read `.claude/winuse-gate.json` at the repository root. Its `launch` field says how to start the app and find its window.
2. Start or reuse the app (`process_run`, `wait_for`, `list_windows`). If the app must be rebuilt to pick up your change, rebuild it first.
3. Check what you changed, and only that. Keep it proportional:
   - A text or label fix: one `assert_text` on that element.
   - A new control or dialog: open it and `assert_exists` the parts you added.
   - A new flow: drive the flow, then assert its result (text, window, or output file).
4. If an assertion fails, fix the code and verify again. Do not report the work as done while it fails.
5. If the check is worth repeating, save it with `scenario_save` where the repository keeps its scenarios.

## What you may ask the user

- Never ask the user to run, check or smoke-test the functional behavior. That is your job.
- You may ask for subjective review (layout, spacing, look and feel) after your assertions pass. Attach a winuse `screenshot` of the result so the user can judge without opening the app.

## When verification is impossible

Only when the change truly cannot be verified with winuse (for example, it needs hardware, an account or a desktop session you do not have), tell the user so explicitly and why. If you must commit such a change, add `winuse-gate: skip <reason>` to the commit message so the reason stays in the history.
