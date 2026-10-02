# Realm legal and rights documents

> **Not legal advice.** These are a volunteer community project's own working documents. Nobody who wrote them is a lawyer. Anything marked **UNVERIFIED** has not been checked against the source it depends on. Get advice from a qualified lawyer before involving money, signing anything, or answering a legal complaint.

| Document | What it is for | Who uses it |
|---|---|---|
| [`eula-compliance-checklist.md`](eula-compliance-checklist.md) | What Realm does, the EULA questions to answer from the real text, and the checks before each launch phase | Owner |
| [`rights-holder-outreach-letter.md`](rights-holder-outreach-letter.md) | A polite template asking the rights holder for written permission (community servers, name, donations, videos) | Owner |
| [`privacy-notice.md`](privacy-notice.md) | What the servers, plugins, player app, Steward, portal, Discord bot and analytics collect, for how long, and the opt-outs. Publish it (portal, Discord) once the brackets are filled in | Players; Owner publishes |
| [`data-deletion-process.md`](data-deletion-process.md) | How players ask to see or delete their data, and the staff procedure | Players, staff |
| [`monetisation-guardrails.md`](monetisation-guardrails.md) | What is never allowed, what needs written permission, and what is fine now | Owner, staff, creators |
| [`tools/find-player-data.mjs`](tools/find-player-data.mjs) | Read-only search for one player's data across server files, for access and deletion requests | Staff |

Community operations (staff roles, moderation, appeals, creators, incidents) are in [`../community/ops/`](../community/ops/README.md).

## The status in one paragraph

Realm's design keeps the game client untouched and requires every player to own the game on Steam (verified from the repo). **The Reign of Kings EULA itself has not been read by anyone on the project** (`docs/community/launch-plan.md` §0), and it could not be fetched while these documents were written, so every EULA clause is still blank in the checklist. Until it is read and recorded, the rule stays: **no money and no paid perks of any kind**.

## Tools and tests

```
node docs/legal/tools/find-player-data.mjs --help
node --test docs/legal/tools/*.test.mjs
```

`find-player-data.mjs` needs Node 18 or newer and no packages. It only reads; it never writes, moves or deletes anything, and it prints file paths, JSON paths and line numbers, never values or other players' data.
