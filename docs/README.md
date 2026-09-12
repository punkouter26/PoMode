# docs

Background and decisions that are not derivable from the code. Nothing here should restate what
`CLAUDE.md` already says, and nothing here should describe code that does not exist.

## Removed: `ai-cost-audit.md`

It compared cloud stem-separation providers (Replicate, LALAL.AI) and a "copilot", and recommended
keeping an execution order of `Local → fake → Replicate → LALAL`. It was also the one document
`CLAUDE.md`'s first working rule tells a reader to open before planning anything.

None of it was ever built. There has never been a Cloud-tier executor in this repository, "copilot"
appears nowhere in the source, and `ExecutionTier.Cloud` — the enum value that whole ordering hung
on — has now been deleted as well. A cost audit of spend that cannot occur is worse than no document,
because it sends the next reader looking for a tier to extend rather than for the one that exists.

The costs that are real are all local and none of them bill: CPU time for HTDemucs (stem separation)
and Basic Pitch (melody), and whatever model Ollama has installed for the written interpretation. If
a paid tier is ever added, the audit belongs here again — written against code that is in the repo.
