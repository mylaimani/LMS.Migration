# Team Batting Analysis — Developer Guide

How to build the "Team Batting Analysis" card from the Team Phase API.

## 1. The call

```
GET https://stagingappapi.lastmanstands.com/api/lms/TeamProfile/{teamId}?lastGames=10
```

> Cloudflare note: staging is behind Cloudflare bot protection. Server-to-
> server calls may receive a "Just a moment..." challenge page instead of
> JSON. Infra needs a WAF skip rule for /api/* (or an API token header
> exempted from challenges) before non-browser clients can use this.

`lastGames=10` gives the UI behaviour ("last 10 games played by <team>").
The other filters (seasonId, leagueId, year, fromDate, toDate) are for
other views — do NOT combine them with lastGames unless the design asks.

> Note: `lastGames` is being added; until deployed, calling with only
> teamId returns the team's ENTIRE history — do not ship the page
> against that.

## 2. UI element → data mapping

The response contains one object per phase. "EARLY OVERS (OVERS 1–6)"
= the `"Powerplay"` phase object. (Middle = 7–15, Death = 16–20 for the
later sections of the page.)

Using the Powerplay object `p` and `games` = number of games actually
found (≤ lastGames — new teams may have fewer):

| UI element | Formula |
|---|---|
| Donut centre "72 runs" | `round(p.runs / games)` — average runs per game in this phase |
| Dot % | `p.dots / p.balls × 100` |
| 1s % | `p.ones / p.balls × 100` |
| 2s % | `p.twos / p.balls × 100` |
| 3s % | `p.threes / p.balls × 100` |
| 4s % | `p.fours / p.balls × 100` |
| 6s % | `p.sixes / p.balls × 100` |
| AVG RUNS SCORED | same as donut centre |
| (global avg 58) | from the global-averages endpoint (league_avg), same phase |
| DOT BALL % | `p.dots / p.balls × 100` |
| (global avg 32%) | global endpoint, same phase |

Percentages: round to whole numbers; make them sum to 100 by adjusting
the largest bucket if rounding drifts.

## 3. Key contributors section

Per-striker aggregation over the SAME set of fixtures and the same
phase, top 3 by runs:

| UI element | Formula |
|---|---|
| runs (e.g. "94 runs") | striker's phase runs across the last 10 games. **LMS rule: wides/no-balls faced are credited to the striker** — the API already includes them in `runs`; do not re-add |
| avg/game ("11.8 avg/game") | `runs / games` (games = the 10, not games the player appeared in — confirm with product if disputed) |
| SR ("SR 218") | `runs / ballsFaced × 100` (balls faced includes wides faced — LMS convention, consistent with the website) |
| Team avg SR ("182") | all strikers of the team combined, same fixtures + phase |
| Delta ("+36") | `playerSR − teamAvgSR`, green if ≥ 0, amber/red if negative |

## 4. Insight sentence

The italic summary ("Patel is your powerplay dangerman…") is template
logic, not stored data. Suggested rules:

- Highest-SR contributor ≥ team avg + 25 → "dangerman" sentence
- Lowest-SR contributor ≤ team avg − 20 → "anchor — target with dots"
- Dot % below global average → "they rotate strike well early"

Keep templates in the frontend or a config — they will be tuned.

## 5. Gotchas

1. **Phases are 1-indexed overs**: Powerplay = overs 1–6. The over
   numbering in raw data is 1-based (first over = 1).
2. **Games with fewer than 10 played**: show "last N games" with the
   real N; never divide by the requested 10.
3. **No-result/abandoned games** still contain balls and count in
   ball-by-ball analysis (this card is about batting patterns, not
   points).
4. **Do not compute global averages client-side** — request them from
   the averages endpoint (they come from a pre-aggregated table and are
   effectively free).
5. **Caching**: the data changes only when the team plays. Cache per
   (teamId, lastGames) for ~1 hour or bust on new fixture.

## 6. What the API team still owes this page

- [ ] `lastGames` parameter (in progress)
- [ ] `games` count in the response (needed for per-game averages)
- [ ] `contributors` array per phase (top strikers with runs/balls)
- [ ] global averages endpoint reference in the response or docs
