# No-Think A/B Retest Spec (feeding monitor line, 2026-11-19)

Purpose: quantify chain-vs-nochain delta (accuracy + latency) on identical 100 questions,
to support the 10s acceptance decision. Requires cloud real machine, idle window
(regular-version line cloud tests are high-frequency; 22:14 CPU-contention pollution must not repeat).

## Groups
- A (baseline): existing 22:14 full-run outputs (branch model-bench-latest, 4b81a8a), scored with score.py v2 (this branch).
- B: same 100 questions (ab_manifest.json), only changes:
  1. disable thinking chain - Qwen3 template kwarg enable_thinking=false (or /no_think in prompt);
     verify actual mechanism on the cloud machine before launch, do not assume.
  2. raise n_predict so answers complete (468/930 = 50.3% of A run truncated mid-thinking).
  Everything else identical (model, temp, corpus rev, runner).

## Build fingerprint (mandatory, 2026-11-20 AI-version line directive)
- Every rethrow request and every score sheet MUST carry the build fingerprint:
  the verbatim output of `llama-cli --version` for the exact binary used.
- Runners record it automatically as `BUILD_FINGERPRINT <tag> <verbatim>` in run.log
  before the first question of the round.
- A B1 round on a different llama.cpp build than B0 is NOT comparable (cross-build
  kernel float jitter, 362/930 answer-level delta, verdict 2026-11-20): the score sheet
  must flag the build change explicitly, and the round is void for A/B delta and 99%
  acceptance purposes.
- Accounts without a fingerprint are treated as non-comparable; they are not valid
  evidence for any acceptance decision.

## Manifest
ab_manifest.json: selection_45 (model-selection set) + trap_30 (all TRAP) + trunc_stratified_25.

## Scoring
score.py v2 (this branch): real-answer-region extraction, TRUNCATED classified separately,
lenient + strict dual gauge. Baseline A on 930: lenient 338/930=36.3%, strict 114/930=12.3%,
truncated 468. Report B on the same 100 with per-question wall time.
