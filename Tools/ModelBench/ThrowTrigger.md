# Throw trigger marker

- 2026-11-23T18:25+08:00, AI-version line (project 2191209) triggers matrix x3 throw #7.
- Purpose: C-class (sapphirerapids) census needs N>=2 rounds on locked build b12559; six throws, zero hits - boss decision (A/B) still pending, throw #7 continues as zero-cost fallback per option C.
- Throw #6 verdicts: haswell 567, haswell 561, zen4 561 (three-check PASS x3). Note: first cross-class score overlap observed (haswell hit 567, zen4 hit 561) - within the known 3-10 question drift band; score-based class mapping is noisy, runtime ggml-cpu dll line remains the authoritative class key.
- Frequency note: observed C frequency ~9% over 22+ observations; expected ~8 throws for N>=2. No engine/corpus/scorer changes in this commit.
