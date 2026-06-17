# LMS Analytics — ClickHouse Migration Summary

**To:** Wayne Greve & LMS Technical Team  
**From:** Manny  
**Date:** 15 June 2026  
**Subject:** ✅ LMS ClickHouse Staging Migration — Completed

---

## Overview

I'm pleased to confirm that the LMS Analytics data migration to ClickHouse on Google Cloud (staging environment) has been completed successfully today. This is a major milestone in the LMS Premium Profile and Analytics project.

All historical match data has been migrated from SQL Server into ClickHouse, providing the foundation for real-time ball-by-ball analytics, Premier Profile, LMS Pulse, and highlight clips.

---

## What Was Migrated

Migrated: **182,026 fixtures** (every match with live-scoring data from 2015 to 15 June 2026) · **35.7M ball events** · **792,750 highlight clips** · **2,029,106 partnerships**. Zero failures; 32 fixtures skipped as they contain no recorded deliveries.

Clips breakdown: 427,195 fours · 176,980 sixes · 186,912 wickets · 1,663 other — each linked to the exact delivery, batsman, bowler, keeper and fielder.

---

## Validation

- Every innings total reconciled against the official score: **99.83% match exactly**. The remaining 0.17% are matches where the live-scoring stream doesn't contain corrections the scorer applied to the totals — these are flagged automatically and the official totals remain the source of truth for results and player stats.
- Cross-check confirmed: SQL Server contains 182,058 fixtures; 182,026 are in ClickHouse and 32 are legitimately empty (no recorded deliveries) — every fixture with live-scoring data is accounted for.
- Scoring rules are implemented correctly, including LMS-specific behaviour (wide/no-ball escalation within an over, final-over home runs, steals) — verified ball-by-ball against production fixtures from 2015 through this month.
- Highlight clips classified (427k fours, 177k sixes, 187k wickets) and linked to the exact delivery, batsman, bowler, keeper and fielder — ready for the premium profile features.
- Spot-checks against live website scorecards across multiple seasons all match.
- Performance: scanning all 792k clips takes 0.01 seconds; ball-by-ball analytics queries return in milliseconds.

---

## What's Now in ClickHouse (Staging)

| Table | Description |
|---|---|
| lms.ball_events | 35.7M rows — every ball bowled, foundation for all analytics |
| lms.partnerships | 2,029,106 batting partnerships across all matches |
| lms.clips | 792,750 video highlight clips linked to exact ball |
| lms.h2h_stats | Pre-aggregated head-to-head batter vs bowler career stats |
| lms.player_batting_phase | Pre-aggregated batting stats by phase (Powerplay / Middle / Death) |
| lms.player_bowling_phase | Pre-aggregated bowling stats by phase |
| lms.team_phase | Pre-aggregated team scoring patterns by phase |
| lms.league_avg | League / regional / national averages for benchmarking |

> **Note:** Player Ratings and Rankings remain in SQL Server for now and will be migrated to ClickHouse in a later phase.

---

## Key Benefits Now Unlocked

- **Premier Profile data foundation is complete** — all ball-by-ball, partnership and clip data is in place and queryable
- **H2H, phase analysis and trend queries load in milliseconds** via pre-aggregated materialized views
- **LMS Pulse win predictor** has the ball-by-ball data it needs
- **Highlight clips** are classified and linked to the exact ball — ready for premium profile features
- **SQL Server offloaded** — 35.7M analytical rows moved out, reducing production CPU pressure

---

## What's Next

| Priority | Task | Status |
|---|---|---|
| 1 | Auto-sync new games to ClickHouse immediately on match completion | 🔲 To Do |
| 2 | Premier Profile verification — sample queries to confirm output matches expected values | 🔲 To Do |
| 3 | Player Ratings & Rankings migration to ClickHouse | 🔲 Later Phase |
| 4 | Production deployment | 🔲 Later Phase |

---

*For any questions or access to the staging ClickHouse instance, contact Manny at mylaimani78@gmail.com*
