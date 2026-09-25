using LMS.Migration.Core.Models;
using LMS.Migration.Core.Parsers;

namespace LMS.Migration.Core.Pulse
{
	/// by Mani /// LMS Pulse replay for ball_events (24 Sep 2026)
	/// <summary>
	/// Fills PulseAfterPct and PulseChangePct on every ball of a parsed fixture
	/// by replaying the balls in order through PulseCalculator - the same
	/// formulas the live scoring server uses.
	///
	/// Meaning of the two columns (the Live Insights screen relies on this):
	///   PulseAfterPct  = the BATTING team's win % after this ball
	///   PulseChangePct = PulseAfterPct minus the batting team's win % before
	///                    this ball. Positive = good for the batter.
	///                    For a bowler, the same ball counts the other way.
	///
	/// Starting points:
	///   ball one of innings one   : pre-match 50/50 (ratings not wired here yet)
	///   ball one of innings two   : the innings-break number, i.e.
	///                               100 - first innings final win %
	///
	/// Kept in line with the live scoring server (code review, 25 Sep 2026):
	///   1. balls are counted with BallEvent.CountsAsBall (LMS rule engine: a
	///      second or later wide/no-ball in a non-final over counts as a ball);
	///   2. scorer corrections are NOT applied. The saved game does not record
	///      when a correction was made, and ball_events has one row per real
	///      ball, so there is nowhere honest to put it; spreading it over earlier
	///      balls would credit it to the wrong batters (code review 25 Sep). Every
	///      row uses the ball stream's own score. The PulseHistory API on the
	///      scoring server shows a correction as its own end-of-innings entry;
	///   3. runs off a re-bowled wide/no-ball count towards momentum on the next
	///      delivery that counts as a ball, as PulseStateStore does live.
	/// Innings beyond two (none expected) are left at 0.
	/// </summary>
	public static class PulseReplayer
	{
		private const double PreMatchWinPct = 50.0;
		private const int DefaultMaxWickets = 8;

		public static void FillPulse( ParsedFixture parsed )
		{
			var firstInningsBalls = parsed.Balls.Where( ball => ball.InningsNumber == 1 ).ToList();
			var secondInningsBalls = parsed.Balls.Where( ball => ball.InningsNumber == 2 ).ToList();

			var firstInningsFinalPct = ReplayFirstInnings( firstInningsBalls, SquadSize( parsed.BattingFirstPlayerCount ) );

			if ( firstInningsBalls.Count == 0 || secondInningsBalls.Count == 0 ) return;

			var target = firstInningsBalls.Last().ScoreAtBall + 1;
			var inningsBreakChasePct = 100.0 - firstInningsFinalPct;

			ReplayChase( secondInningsBalls, target, inningsBreakChasePct, SquadSize( parsed.BowlingFirstPlayerCount ) );
		}

		/// <summary>Returns the batting side's win % after the last ball of the innings.</summary>
		private static double ReplayFirstInnings( List<BallEvent> balls, int maxWickets )
		{
			var previousPct = PreMatchWinPct;
			var legalBallsBowled = 0;

			foreach ( var ball in balls )
			{
				if ( ball.CountsAsBall ) legalBallsBowled++;

				var totalBalls = ball.TotalOvers * ball.BallsPerOver;
				if ( totalBalls <= 0 ) continue;

				var result = PulseCalculator.CalculateFirstInnings( new FirstInningsLiveData
				{
					Runs = ball.ScoreAtBall,
					WicketsLost = ball.WicketsAtBall,
					TotalBalls = totalBalls,
					RemainingBalls = Math.Max( 0, totalBalls - legalBallsBowled ),
					MaxWicketsAvailable = maxWickets,
					PreMatchBattingWinPct = PreMatchWinPct,
					ParScoreFromServer = 0
				} );

				SetPulse( ball, result.BattingTeamWinPct, previousPct );
				previousPct = result.BattingTeamWinPct;
			}

			return previousPct;
		}

		private static void ReplayChase( List<BallEvent> balls, int target, double inningsBreakChasePct, int maxWickets )
		{
			var previousPct = inningsBreakChasePct;
			var legalBallsBowled = 0;

			// Runs per counted ball for the momentum window. Runs off a re-bowled
			// wide/no-ball are carried onto the next delivery that counts as a ball,
			// the same as PulseStateStore.RecordBall on the live server.
			var runsPerLegalBall = new List<int>();
			var runsSinceLastLegalBall = 0;
			var scoreBefore = 0;

			PulseResult? previousResult = null;

			foreach ( var ball in balls )
			{
				runsSinceLastLegalBall += ball.ScoreAtBall - scoreBefore;
				scoreBefore = ball.ScoreAtBall;

				if ( ball.CountsAsBall )
				{
					legalBallsBowled++;
					runsPerLegalBall.Add( runsSinceLastLegalBall );
					runsSinceLastLegalBall = 0;
				}

				var totalBalls = ball.TotalOvers * ball.BallsPerOver;
				if ( totalBalls <= 0 ) continue;

				var remainingBalls = Math.Max( 0, totalBalls - legalBallsBowled );

				var result = PulseCalculator.CalculateChase( new ChaseLiveData
				{
					Target = target,
					RunsScored = ball.ScoreAtBall,
					WicketsLost = ball.WicketsAtBall,
					TotalBalls = totalBalls,
					RemainingBalls = remainingBalls,
					MaxWicketsAvailable = maxWickets,
					RecentRuns = RecentRuns( runsPerLegalBall, MomentumWindowFor( remainingBalls ) ),
					InningsBreakChaseWinPct = inningsBreakChasePct,
					PreviousRunsNeeded = previousResult?.RunsNeeded,
					PreviousBallsRemaining = previousResult?.BallsRemaining,
					PreviousChaseWinPct = previousResult?.BattingTeamWinPct
				} );

				SetPulse( ball, result.BattingTeamWinPct, previousPct );
				previousPct = result.BattingTeamWinPct;
				previousResult = result;
			}
		}

		private static void SetPulse( BallEvent ball, double afterPct, double beforePct )
		{
			ball.PulseAfterPct = (float)Math.Round( afterPct, 2 );
			ball.PulseChangePct = (float)Math.Round( afterPct - beforePct, 2 );
		}

		/// <summary>Spec 3.9 - same windows as PulseViewModelBuilder on the live server.</summary>
		private static int MomentumWindowFor( int remainingBalls )
		{
			if ( remainingBalls > 50 ) return 12;
			if ( remainingBalls > 25 ) return 10;
			if ( remainingBalls > 10 ) return 8;
			return 5;
		}

		private static int RecentRuns( List<int> runsPerLegalBall, int windowSize )
		{
			var take = Math.Min( windowSize, runsPerLegalBall.Count );
			return runsPerLegalBall.Skip( runsPerLegalBall.Count - take ).Sum();
		}

		private static int SquadSize( int playerCount )
		{
			return playerCount > 0 ? playerCount : DefaultMaxWickets;
		}
	}
}
