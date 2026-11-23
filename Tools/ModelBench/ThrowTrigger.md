# Throw trigger marker

- 2026-11-23T17:00+08:00, AI-version line (project 2191209) triggers matrix x3 throw #6.
- Purpose: C-class (sapphirerapids) census needs N>=2 rounds on locked build b12559; five throws, zero hits - stop-loss reached, decision package (revised: observed C frequency ~9% over 22 observations, expected ~8 throws) with boss, A/B pending. Throw #6 continues as zero-cost fallback per option C.
- Throw #5 verdicts: zen4 567/85, haswell 561/82 x2 (all consistent with class baselines).
- Frequency correction note: 3e6f928a0 is sapphirerapids but on drifted build b12541 - does NOT count for the locked-build series; valid C-class N=1 remains c9b3f3489 only.
- Zero-cost free-runner sampling per boss order 1 (83842c97b). No engine/corpus/scorer changes in this commit.
