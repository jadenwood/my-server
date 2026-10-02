# Realm community operations

How the Realm staff team is organised and how it handles conduct, appeals, creators and incidents. The player-facing rules stay in [`../rules.md`](../rules.md); these pages are for staff (and for players who want to know how decisions are made).

> **Not legal advice.** Community operating rules for a volunteer-run game server.

| Page | What it covers |
|---|---|
| [`staff-roles-and-permissions.md`](staff-roles-and-permissions.md) | Owner, Admin, Moderator and Event host, mapped to the real permission names in `plugins/*.cs`, what each permission unlocks, the gameplay advantage it gives, Oxide group setup, and conduct rules for staff |
| [`moderation-handbook.md`](moderation-handbook.md) | Where reports come from, the escalation ladder (note to permanent ban), evidence standards, keeping evidence, step-by-step handling |
| [`ban-appeals.md`](ban-appeals.md) | The full appeal process: template, reviewer rules, outcomes, late appeals, targets |
| [`creator-code-of-conduct.md`](creator-code-of-conduct.md) | Honesty about what Realm is, fair play on stream, respect for players, money and sponsorship limits |
| [`incident-response.md`](incident-response.md) | Exploits, item duplication, cheating, data and secret leaks: severity levels, containment, recovery, communication |

Related: privacy and data requests in [`../../legal/`](../../legal/README.md); server outages, DDoS and leaked secrets in [`../../../ops/disaster-recovery.md`](../../../ops/disaster-recovery.md).

## Keeping the permission matrix true

```
node docs/community/ops/tools/check-permissions.mjs   # exit 0 = every plugin permission has a row, and no unknown name
node --test docs/community/ops/tools/*.test.mjs
```

Run these after any plugin adds or renames a permission. Node 18 or newer, no packages.

## What is verified

- Permission names: read from the plugin source by the checker (13 permissions at the time of writing).
- Which commands each permission gates and which gameplay bypasses it gives: read from the `IsAdmin` call sites in each plugin and from `plugins/docs/*.md`.
- Oxide `oxide.group`, `oxide.usergroup`, `oxide.grant`, `oxide.revoke`, `oxide.show`, `oxide.unload`: present in the Oxide.ReignOfKings 2.0.3867 source (`src/ReignOfKingsCore.cs` lines 88-98) [SRC].
- **UNVERIFIED:** every command on a live server (no plugin has run in game yet, `README.md`), the Court's kick, ban and unban replies, and the time limits in these pages, which are starting proposals to adjust after the closed test.
