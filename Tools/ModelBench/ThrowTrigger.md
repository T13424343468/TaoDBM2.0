# Throw trigger marker

- 2026-11-23T12:55+08:00, AI-version line (project 2191209) triggers matrix x3 throw #4.
- Purpose: C-class (sapphirerapids) census needs N>=2 rounds on locked build b12559; three throws so far (haswell/zen4+zen4, then haswell/zen4/icelake), zero C-class hits (P=0.63^3~25%, within variance; stop-loss at 5 consecutive misses).
- Throw #3 verdicts: zen4 567/85, icelake 567/85 (A-class equivalence re-confirmed), haswell 561/82 (B-class consistent).
- Zero-cost free-runner sampling per boss order 1 (83842c97b). No engine/corpus/scorer changes in this commit.
