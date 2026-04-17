# Paste-ready prompt for the FreeSO-side agent

Copy everything below the line into the FreeSO session's opening message.

---

I'm pivoting embodied-LLM-Sim work from 3dl-dev/FreeSims to this FreeSO repo. The FreeSims agent (me, in another session) did a thorough review of why agents behave weakly on that stack and wrote a handoff for you. Before doing anything on FreeSO, read these two documents from the FreeSims repo in order:

1. `docs/freeso-handoff.md` — briefing addressed to you. Annotated per-commit apply/skip list, architectural choice to make (local IPC vs multiplayer-as-client vs server-side agents), findings that apply regardless, things not to copy blindly.
2. `docs/embodied-agent-design-review.md` — the deep findings (convention-API sterility, severed dialog feedback loop, passive perception, blocking intent loop) and a four-workstream iteration plan (A close feedback loop, B teach conventions what they do, C self-documenting perception, D unblock the agent).

Fetch the repo as a reference, not a dependency:

```
git remote add freesims https://github.com/3dl-dev/FreeSims.git
git fetch freesims master
git show freesims/master:docs/freeso-handoff.md | less
git show freesims/master:docs/embodied-agent-design-review.md | less
```

The most important single finding: **the dialog feedback loop is severed.** `VMIPCDriver.HandleVMDialog` emits perfectly good dialog frames (rejections, notifications); the Go sidecar only prints them to stdout; in `--campfire` mode stdout is gated off. Agents never hear the game tell them "no". Fix that first if you port anything.

Second most important: **convention descriptions in FreeSims are sterile by principle** ("Just be. Not every moment needs action."). Galtrader's API — which worked well with Sonnet — is richly instructive ("Trading in your current vessel for 50% value. Requires Docking Computer equipment."). The FreeSims CLAUDE.md design rule "no behavioral coaching in prompts" is the root cause; reconsider before copying.

Third: **the playtests ran on Opus 4.7 by accident** (SDK default; no `model=` pinned). Galtrader's success was on Sonnet. Don't put Opus on this without a reason; start with Sonnet.

After reading the docs, propose which of the FreeSims commits you'd cherry-pick vs. re-derive on FreeSO's codebase, and which of the two architectural paths (keep local via IPC vs. agent-as-network-client) you want to take. Don't write code until you've proposed a plan and I've approved it.
