# Throw trigger marker

- 2026-11-23T19:55+08:00, AI-version line (project 2191209) triggers matrix x3 throw #8.
- Purpose: C-class (sapphirerapids) census needs N>=2 rounds on locked build b12559; seven throws, zero hits (P~13.5% under corrected ~9% frequency, within variance) - boss decision (A/B) still pending, throw #8 continues as zero-cost fallback per option C.
- Throw #7 verdicts: haswell 567, zen4 561, haswell 561 (three-check PASS x3). Cross-class score overlap (haswell 567 / zen4 561) reproduced in consecutive throws - confirms score-based class mapping is noisy; runtime ggml-cpu dll line remains the authoritative class key.
- Frequency note: observed C frequency ~9% over 22+ observations; expected ~8 throws for N>=2. No engine/corpus/scorer changes in this commit.
