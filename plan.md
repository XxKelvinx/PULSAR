# Pulsar V.2 Plan

## Goal

Build Pulsar V.2 as an offline, music-focused codec fork on top of the Opus/CELT foundation without throwing away the current Opus quality floor.

Primary success condition:

- Keep the current Opus/CELT baseline as the minimum quality bar.
- Only keep new logic when it is neutral or better on normal material and clearly better on difficult material or at lower bitrate.

Non-goal for the first phases:

- Do not try to beat transparent 192 kbps Opus everywhere in average listening.
- Do not rewrite CELT quantization or entropy coding first.
- Do not introduce a global file-wide dependency that breaks frame independence unless that becomes a deliberate format change later.

## Core Principle

The safest path is not to replace Opus intelligence, but to steer it better for offline encoding.

That means:

- Keep the current encoder as the reference path.
- Add an offline planner above it.
- Feed the encoder better state choices and better per-segment budgets.
- Fall back to baseline whenever the new planner is risky or not clearly useful.

## How To Judge Better Or Worse

At high bitrates, improvement does not usually mean "everything sounds more transparent." It usually means one of these:

- Same quality at lower bitrate.
- Fewer audible artifacts on killer samples.
- Less quality variance over time.
- Better handling of transients, stereo instability, sibilants, and dense passages.
- Fewer bad outliers while staying equal on average material.

Practical success criteria:

- Match current Opus quality at 192 kbps while using fewer bits on average.
- Or outperform current Opus on hard clips at the same bitrate.
- Or stay equal at 192 kbps and improve 128 to 160 kbps materially.

## Baseline Rules

These rules are mandatory.

- Preserve an untouched baseline encode path.
- Every new feature must be switchable on and off independently.
- If a feature is worse on the standard test set, disable it.
- If a feature only helps a tiny niche while regressing normal material, disable it.
- If a feature only improves metrics but not listening results, treat it as low priority.

## Real Levers Worth Testing

These are the highest-value levers that can plausibly produce real gains without destabilizing the codec core.

### 1. Offline Segment Planner

Build a whole-track or multi-second planning pass that scores each frame or segment for difficulty.

Possible outputs:

- target bitrate class
- frame duration choice
- stereo mode preference
- bandwidth target
- transient class
- conservative or aggressive allocation mode

Why this matters:

- Current libopus is smart, but mostly local and online.
- Offline planning can smooth decisions and prepare for difficult passages before they happen.

### 2. Viterbi Or Dynamic Programming For Discrete States

Use Viterbi only for low-cardinality state decisions.

Best candidates:

- frame duration
- stereo mode
- bandwidth mode
- transient vs non-transient policy
- conservative vs aggressive allocation class

Avoid using Viterbi for:

- raw per-band bit allocation
- full quantizer state search
- deep pulse allocation across all bands

Reason:

- The state space explodes.
- It becomes fragile and overfit quickly.

### 3. Virtual Reservoir On Segment Windows

Use a virtual bit reservoir over 1 to 4 second windows rather than a true MP3-style reservoir across the whole file.

The intent:

- Spend more where the content is difficult.
- Save bits where the content is easy.
- Keep every encoded unit self-contained enough to avoid a format mess.

Guardrails:

- minimum frame budgets
- maximum segment overspend
- limited debt carryover
- recovery constraint after difficult passages

### 4. Local Quality Floors

Every frame should have minimum protection for important failure modes.

Suggested floors:

- transient floor
- tonal stability floor
- stereo coherence floor
- high-frequency stability floor

Why this matters:

- Global planners tend to starve later or less obviously important frames.
- Floors stop the optimizer from creating hidden regressions.

### 5. Hard Rollback Against Baseline

For each frame or segment, allow a guarded fallback to baseline behavior when the planned path is unsafe.

Rollback triggers can include:

- planner confidence too low
- feature disagreement too high
- transient risk too high
- stereo instability too high
- low headroom after floors

## Secondary Levers Worth Exploring Later

These may matter, but should not be phase 1 work.

### 6. Better Difficulty Estimation

Extend the current analysis with additional difficulty features.

Candidates:

- spectral flux
- attack density
- stereo decorrelation rate
- tonal instability
- sibilance risk
- HF fragility
- masking deficit estimate
- pre-echo risk estimate

### 7. Better Stereo Policy

Offline encoding can often do a better job than online heuristics at deciding:

- when to keep full stereo
- when to reduce stereo width safely
- when intensity-like behavior is acceptable
- when stereo changes should be delayed for stability

### 8. Smarter Bandwidth Policy

Bandwidth selection should be planned over time, not just frame by frame.

Improvements could include:

- less flicker between neighboring bandwidth decisions
- temporary HF protection before or during difficult passages
- avoiding unnecessary bandwidth drops on short difficult frames

### 9. Better Transient Management

Not necessarily a new transform or new block switching system at first.

Initial gains may come from:

- preemptive frame duration changes
- transient-specific floors
- temporary reservoir bias toward attacks
- conservative stereo behavior during attacks

### 10. Region-Aware Encoding

Treat tracks as a sequence of regions instead of isolated frames.

Region types:

- intro and sparse section
- sustained tonal section
- dense chorus
- attack cluster
- stereo-atmospheric section

Each region can get a policy profile rather than per-frame chaos.

## Architectural Ideas

These are the architecture pieces that can make the project actually manageable.

### A. Analyzer Module

Purpose:

- Run a full-track or streaming pre-analysis pass.
- Produce frame and segment descriptors.

Suggested outputs per frame:

- tonality
- activity
- music probability
- bandwidth estimate
- stereo width estimate
- spectral flux
- transient risk
- HF risk
- difficulty score

Suggested outputs per segment:

- region type
- segment difficulty
- budget suggestion
- mode stability preference

### B. Planner Module

Purpose:

- Convert analyzer outputs into a stable path over time.

Responsibilities:

- Viterbi or DP over discrete state sets
- bitrate budget scheduling
- reservoir debt tracking
- enforcement of floors and hysteresis

Planner output should be explicit and inspectable.

For example, one plan item per frame:

- frame index
- segment id
- chosen frame size
- chosen bandwidth
- stereo policy
- transient policy
- target budget
- fallback allowed or not

### C. Guardrails Module

Purpose:

- Decide whether planner output is safe.

Responsibilities:

- compare planner target with baseline constraints
- reject unsafe low-budget frames
- clamp unstable mode changes
- trigger fallback to baseline

### D. Encoder Adapter Module

Purpose:

- Feed planner decisions into the existing encoder with minimal code disturbance.

Key requirement:

- Keep the current Opus/CELT encode path intact as much as possible.

This should be a narrow interface layer, not a deep rewrite.

### E. Evaluation Harness

Purpose:

- Make regressions obvious quickly.

Responsibilities:

- batch encode test corpus
- decode outputs
- capture bitrate and frame statistics
- log mode decisions
- compare against baseline
- export segments for listening tests

## Small Practical Goals

These should be the first milestones because they are measurable and low-risk.

### Goal 1. Baseline Reproducibility

- Build and run the current upstream baseline reliably.
- Freeze a known-good baseline configuration.
- Make repeatable encode scripts for the test corpus.

### Goal 2. Analysis Logging

- Dump current analysis features from the encoder for each frame.
- Save them as CSV or JSON.
- Inspect where current decisions fluctuate.

### Goal 3. Difficulty Heatmap

- Generate a difficulty timeline for a track.
- Identify likely killer regions before touching allocation.

### Goal 4. Virtual Reservoir Prototype

- Implement a simple segment-level budget scheduler.
- Do not modify deep CELT allocation yet.
- Only bias top-level target budgets.

### Goal 5. Viterbi Prototype For One Lever

- Choose one discrete lever first.
- Recommended first lever: bandwidth or frame duration.
- Test whether smoothing decisions improves hard material without regressions.

### Goal 6. Guardrail And Rollback

- Add explicit rollback logic.
- Ensure every experimental path can lose and fall back safely.

## Medium-Term Goals

### Goal 7. Joint Planning Of Bandwidth And Stereo

- Move from one-state planning to two-state planning.
- Keep the state count small and the transition penalties simple.

### Goal 8. Region Profiles

- Assign a policy profile per segment or region.
- Use those profiles to reduce unnecessary mode flicker.

### Goal 9. CELT Target Guidance

- After the planner is stable, start guiding CELT choices more directly.
- Keep changes local and measurable.
- Avoid rewriting the whole allocator.

## High-Risk Ideas For Much Later

These may be interesting, but they are dangerous before the planner and evaluation stack are mature.

- deep rewrites of CELT allocation logic
- new quantization laws
- new entropy models
- cross-frame coding dependencies
- true file-wide bit reservoir with hard inter-frame dependency
- replacing Opus tonality analysis wholesale

## Suggested Evaluation Protocol

Every experiment should be evaluated on:

- easy music
- difficult music
- killer samples
- several bitrates, especially 128, 160, 192 kbps

Listen for:

- pre-echo
- smeared attacks
- stereo collapse
- hissy or unstable HF
- pumping or temporal instability
- narrowness or image drift

Pass criteria:

- no obvious regression on the standard set
- clear improvement on at least one meaningful class of hard material
- neutral to positive results on repeated listening

Fail criteria:

- feature only helps metrics
- feature creates even small regressions on common material
- feature makes behavior unstable or hard to reason about

## What Not To Forget

- Opus is already strong. Random complexity will not beat it.
- The first real gains will likely come from planning and stability, not exotic math.
- At high bitrate, reducing outliers matters more than improving averages.
- The project should optimize for controllable progress, not heroic rewrites.

## Recommended First Implementation Order

1. Freeze baseline and test corpus.
2. Add logging for existing analysis and encoder decisions.
3. Build offline analyzer output files.
4. Add a simple segment difficulty model.
5. Implement a tiny virtual reservoir.
6. Add Viterbi for one discrete state family.
7. Add guardrails and rollback.
8. Run listening tests and kill anything that regresses.
9. Only then touch deeper CELT guidance.

## Summary

Pulsar V.2 should start as an offline planning layer over a strong Opus/CELT baseline.

The first real levers are:

- offline analysis
- discrete-state path planning
- segment-level virtual reservoir
- local quality floors
- hard rollback to baseline

If these layers cannot produce gains safely, deeper codec surgery is unlikely to pay off early.

If they do produce gains, then deeper CELT-target guidance becomes worth exploring.