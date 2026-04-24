# Pulsar / Opus Lever Results

## Test protocol

- New staged search flow: 2-song gate, then 10-song fullset.
- Gate songs used in this run:
  - `04_geilo_dua_lipa_dance_the_night`
  - `05_muss_6arelyhuman_bloodbath`
- Fullset: 10 mixed songs from `Geilomatiko`, `Muss los`, and `Tokyo`.
- Score remains the same weighted blend used in the search scripts:
  - attack-frame MSE delta
  - overall frame MSE delta
  - LSD delta
  - transient flux MSE delta
  - SNR delta
- Negative score is better than baseline.

## Updated perceptual scoring

- The scoring function was retuned to be closer to how an engineer would listen.
- New score inputs now include:
  - attack-frame MSE delta
  - calm-frame MSE delta
  - perceptually weighted spectral error delta
  - stereo image error delta
  - transient flux MSE delta
  - LSD delta
  - SNR delta with lower weight than before
- Practical effect:
  - exposed spectral errors in the presence region are penalized more strongly
  - quiet-frame damage matters more than before
  - stereo image drift is now explicitly penalized
  - raw SNR matters less, because it does not track listening quality well on its own

## Lever verdicts so far

- `bandwidth_plan`: rejected.
  - Too little real benefit.
  - Not worth the extra control complexity.
- `channels_plan` / stereo reduction: rejected.
  - Clearly dangerous for side information.
  - Hurt reconstruction enough to drop from the active path.
- Original segment-only bitrate reshaping: weak but real.
  - It does move bits around in a plausible way.
  - By itself it is not strong enough to justify being the main lever.
  - Still worth keeping as a light secondary mix component.
- Transient / attack-focused bitrate planning: effective.
  - This is still the only lever family that repeatedly improves the important metrics instead of just moving them around.
  - It remains the main path.

## Best single-track result before multi-song validation

- Best previous single-track candidate: `transient_wide_attackmax`
- Previous single-track metrics:
  - `avg_kbps`: about `110.631`
  - `snr_delta`: about `+0.14819`
  - `flux_mse_delta`: about `-0.02843`
  - `overall_mean_delta`: about `-0.00018065`
  - `attack better / worse`: `208 / 39`
  - `overall better / worse`: `2574 / 2352`

## 2-song gate results

All tested transient candidates passed both gate songs in this run. That means the gate did not reject aggressively enough yet, but it still ranked the field.

Top gate averages:

| Candidate | Score | Attack delta | Overall delta | LSD delta | Flux delta |
| --- | ---: | ---: | ---: | ---: | ---: |
| `transient_wide_attackmax` | `-0.06634` | `-0.00089295` | `-0.00012730` | `-0.03058` | `-0.02119` |
| `transient_wide_balanced` | `-0.05522` | `-0.00080476` | `-0.00012335` | `-0.01489` | `-0.02062` |
| `transient_wide_attackmax_seg25` | `-0.05521` | `-0.00086458` | `-0.00011673` | `-0.01891` | `-0.01850` |

Promoted to the 10-song fullset:

- `transient_wide_attackmax`
- `transient_wide_balanced`
- `transient_wide_attackmax_seg25`

## Retuned gate and combination run

- Gate was tightened by replacing `05_muss_6arelyhuman_bloodbath` with `09_tokyo_creepy_nuts_otonoke`.
- New gate pair:
  - `04_geilo_dua_lipa_dance_the_night`
  - `09_tokyo_creepy_nuts_otonoke`
- New combination candidates were tested:
  - lighter global segment mixes: `seg05`, `seg10`
  - selective segment mixes on calm frames only: `seg05_calm`, `seg10_calm`
- Promotion logic was also tightened and then corrected:
  - first pass: only all-pass gate candidates were promoted
  - final pass: all-pass candidates are kept first, then the best gate-ranked candidates are added until the fullset finalist count is reached

## Retuned gate results

The tighter gate finally discriminates instead of letting everything through.

Top gate averages with `otonoke` included:

| Candidate | Score | Attack delta | Overall delta | LSD delta | Flux delta | Passed both gate songs |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| `transient_wide_attackmax_seg05` | `-0.04525` | `-0.00028047` | `-0.00005887` | `-0.00810` | `-0.01055` | `false` |
| `transient_wide_balanced_seg10_calm` | `-0.04522` | `-0.00025918` | `-0.00005789` | `-0.00868` | `-0.01031` | `true` |
| `transient_wide_attackmax` | `-0.04407` | `-0.00028294` | `-0.00005947` | `-0.00582` | `-0.01023` | `false` |

Promoted to the retuned 10-song fullset:

- `transient_wide_balanced_seg10_calm`
- `transient_wide_attackmax_seg05`
- `transient_wide_attackmax`

## Retuned 10-song fullset results

Top fullset averages after the tighter gate:

| Candidate | Score | Attack delta | Overall delta | LSD delta | Flux delta | Better frames | Worse frames |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `transient_wide_attackmax` | `-0.06277` | `-0.00059034` | `-0.00011954` | `+0.00302` | `-0.02110` | `49557` | `46541` |
| `transient_wide_attackmax_seg05` | `-0.06204` | `-0.00058994` | `-0.00011947` | `+0.00374` | `-0.02096` | `49605` | `46496` |
| `transient_wide_balanced_seg10_calm` | `-0.05624` | `-0.00053064` | `-0.00011110` | `+0.00526` | `-0.01943` | `48478` | `47625` |

## Updated interpretation

- Putting `otonoke` into the gate was the right move.
  - The gate now actually separates candidates instead of letting every variant pass.
- The best all-pass gate candidate is not the best fullset candidate.
  - `transient_wide_balanced_seg10_calm` was the only candidate that stayed negative on both gate songs.
  - But on the full 10-song set it still lost clearly to `transient_wide_attackmax`.
- The best new combination is `transient_wide_attackmax_seg05`.
  - It is the first combination that gets genuinely close to pure `attackmax` on the fullset.
  - It even wins slightly more total frames than pure `attackmax`.
  - But the average score is still worse, mainly because the net metric blend does not improve enough to overtake the baseline winner.
- Calm-only mixing is safer than the old heavy global `seg25` blend.
  - It avoids the large LSD penalty seen with `seg25`.
  - But the tested calm-only combination still does not beat pure `attackmax`.

## New lever family: first Viterbi planners

- First Viterbi-style bitrate planners were added and tested as a new lever family.
- Tested variants:
  - `viterbi_guarded`
  - `viterbi_guarded_seg05`
  - `viterbi_calm_tonal`
  - `viterbi_calm_tonal_seg05`
- Core idea:
  - choose discrete per-frame bitrate states with a transition penalty
  - reward higher states on attack-like frames
  - reward lower states on calm / tonal frames

## First Viterbi verdict

- `viterbi_guarded` was the best first Viterbi candidate.
- It was good enough to reach the retuned fullset as a promoted finalist.
- But it did not beat the transient winners.

Fullset result for `viterbi_guarded`:

| Candidate | Score | Attack delta | Calm delta | Perceptual spectral delta | Flux delta | Better frames | Worse frames |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `viterbi_guarded` | `+0.00028` | `+0.00000906` | `-0.00000242` | `+0.000129` | `-0.000047` | `43357` | `52743` |

Interpretation:

- First Viterbi is not a dead end, but it is not yet a winning lever.
- It behaves much closer to baseline than the transient winners.
- It preserves enough structure to survive the gate once, which means the state-search idea is not nonsense.
- But in its current form it is too conservative and loses too much on attack protection.
- The stronger transient planners are still clearly better.

## Second Viterbi family: winner-shaped

- A second Viterbi family was tested, explicitly shaped around the current transient winner.
- Main changes versus first Viterbi family:
  - higher state floor
  - stronger reward for high-bitrate attack states
  - stronger penalty for dropping bitrate on attack frames
  - smaller calm-side cuts
- Tested variants:
  - `viterbi_attackmax_shaped`
  - `viterbi_attackmax_shaped_seg05`
  - `viterbi_attackmax_shaped_calm_seg05`

Gate verdict:

- None of the winner-shaped Viterbi variants beat the heuristic leader.
- Best of the new family was `viterbi_attackmax_shaped` with gate score `-0.00088`.
- That is still far behind `transient_wide_attackmax` at gate score `-0.03272`.

Current conclusion on Viterbi:

- No tested Viterbi variant has beaten the heuristic top candidate.
- The second family was less conservative, but still not strong enough on attack preservation.
- Right now Viterbi remains an interesting secondary framework, not the best active lever family.

## 10-song fullset results

Top fullset averages:

| Candidate | Score | Attack delta | Overall delta | LSD delta | Flux delta | Better frames | Worse frames |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `transient_wide_attackmax` | `-0.06277` | `-0.00059034` | `-0.00011954` | `+0.00302` | `-0.02110` | `49557` | `46541` |
| `transient_wide_balanced` | `-0.05573` | `-0.00053462` | `-0.00011248` | `+0.00703` | `-0.01987` | `48446` | `47649` |
| `transient_wide_attackmax_seg25` | `-0.05395` | `-0.00058022` | `-0.00011716` | `+0.01554` | `-0.02052` | `49754` | `46346` |

## Interpretation

- Current best broad candidate is still `transient_wide_attackmax`.
  - It kept the best overall fullset score.
  - It also kept the best mean attack improvement.
- `transient_wide_balanced` is still a real improvement, but weaker than `attackmax`.
- The first true combination candidate that survived fullset, `transient_wide_attackmax_seg25`, did not beat pure `attackmax`.
  - It won slightly more total frames than baseline.
  - But its average LSD penalty was clearly worse.
  - The final weighted score stayed behind pure `attackmax`.
- So the current verdict is:
  - transient planning is the real lever
  - segment mixing can help a little in some places
  - the tested segment mix is not yet strong enough to beat the pure transient winner across the full dataset

## Important failure case

- `09_tokyo_creepy_nuts_otonoke` is the clearest current stress case.
- All three fullset finalists went positive on score there:
  - `transient_wide_attackmax`: `+0.00204`
  - `transient_wide_balanced`: `+0.01078`
  - `transient_wide_attackmax_seg25`: `+0.01856`
- That track is currently the best candidate for future gate tightening or retuning.

## Current conclusion

- Keep pushing the transient/attack planner as the main direction.
- Do not bring back bandwidth control or stereo downmixing.
- Treat segment logic only as a light secondary modifier, not as a primary driver.
- `transient_wide_attackmax` is still the best broad winner.
- `transient_wide_attackmax_seg05` is the best current combination and the closest follower worth further tuning.
- The next useful combination work should stay in the narrow range around `seg05`, with selective application rather than heavy global blending.
