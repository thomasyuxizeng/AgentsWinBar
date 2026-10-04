# 001-incident-notifications: Notify when a provider goes down

**Status:** queued
**Base branch:** the branch that contains provider status badges (`codex/provider-status-badges` or whatever it was merged into)
**Branch:** `agent/001-incident-notifications`

## Goal

When a provider newly reports a **major** or **critical** incident, the user gets one Windows notification, so they
find out without having the flyout open. It is the optional item left out of the status badges feature.

## Context

- Status support already exists: `ProviderStatus` and its indicator enum in `Models/UsageData.cs`, parsed from the
  CLI's `status` object. Missing status (Gemini) and unknown indicators must never alert.
- Usage alerts already exist: `Services/UsageAlertEvaluator.cs` evaluates providers each refresh and
  `ViewModels/MainViewModel.NotifyAlerts` shows toasts through `IToastNotifier`, gated by the
  `AlertsEnabled` setting ("Usage alerts" in Settings). Reuse that path and that setting; do not add a new toggle.
- Alerts fire once per usage window (see the evaluator's reset-bucket logic). Incidents need their own "once per
  incident" rule: alert when the severity goes from not-major to major/critical, stay quiet while it continues,
  and re-arm after the provider has been back to operational/minor.
- Developer-mode (sample) data must not produce alerts that leak into real state, as for history.

## Scope

- **Do:** the pure decision logic as a testable helper or an extension of the evaluator; wiring into the existing
  notification path; one toast per incident start with the provider name and the status description; a link or
  action to open the status page only if the toast service already supports actions (otherwise skip it).
- **Don't:** a notification for recovery, for minor or maintenance, or for unknown/missing status. No new settings.
  No change to the badges or banners.

## Acceptance criteria

- [ ] Going operational -> major (or critical) alerts once; staying major does not re-alert on later refreshes.
- [ ] major -> critical does not alert again; critical -> operational -> major alerts again.
- [ ] minor, maintenance, unknown and missing status never alert.
- [ ] The first refresh after app start, when a provider is already in an incident, alerts once (not on every start-up of
      a long incident if state is persisted; decide and document the choice in the result file).
- [ ] Respects `AlertsEnabled`.
- [ ] Unit tests cover the transitions above; the full suite passes.
- [ ] `CHANGELOG.md` updated under `[Unreleased]`; the README's Alerts section mentions the new alert.

## Verification the agent cannot do

- Seeing the actual toast on screen (needs the signed app and a provider that reports an incident).
  The Settings "Send test notification" button shows that toasts work at all.
