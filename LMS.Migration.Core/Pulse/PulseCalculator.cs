using System;

namespace LMS.Migration.Core.Pulse
{
	/// by Mani /// COPY of Spawtz.LiveScoring.Web/Pulse/PulseCalculator.cs (24 Sep 2026)
	/// The live scoring server and this worker MUST give the same numbers.
	/// If you change a formula there, copy this file across again - only the
	/// namespace line differs.
	/// <summary>
	/// Straight port of the two LMS Pulse calculators from the Flutter app:
	///   lib/features/first_innings_predictor/domain/first_innings_calculator.dart
	///   lib/features/chase_meter/domain/chase_meter_calculator.dart
	///
	/// Section numbers in the comments refer to the LMS Pulse spec document.
	/// LMS uses 5 ball overs, so TotalBalls = matchOvers x 5.
	///
	/// Do not "tidy up" the constants in here. Every number matches the Dart
	/// version on purpose so both apps show the same win percentage.
	/// </summary>
	public static class PulseCalculator
	{
		private const double DefaultLeagueRunsPerBall = 1.60;

		// ─────────────────────────────────────────────────────────────────────
		// First innings predictor - spec sections 2.3 to 2.18
		// ─────────────────────────────────────────────────────────────────────
		public static PulseResult CalculateFirstInnings( FirstInningsLiveData data )
		{
			var totalBalls = data.TotalBalls;
			var ballsBowled = data.BallsBowled;
			var runs = data.Runs;
			var maxWickets = data.MaxWicketsAvailable;
			var preMatchWinPct = data.PreMatchBattingWinPct;

			var parScore = data.ParScoreFromServer > 0
				? data.ParScoreFromServer
				: DefaultLeagueRunsPerBall * totalBalls;

			double wicketsRemaining = data.WicketsRemaining;
			double ballsRemaining = Math.Max( 0, totalBalls - ballsBowled );

			// 2.3 Start of innings override - before ball one use the pre match number.
			if ( ballsBowled == 0 )
			{
				return BuildFirstInningsResult( data, preMatchWinPct, parScore, parScore );
			}

			var projectedScore = ProjectFirstInningsScore(
				runs, totalBalls, ballsBowled, ballsRemaining, wicketsRemaining, maxWickets );

			var performanceWinPct = PerformanceWinPct( projectedScore, parScore );

			// 2.9 Pre match weighting fades out as balls are bowled.
			var preMatchWeight = Math.Max( 0.0, 0.75 - ( 0.65 * ( (double)ballsBowled / totalBalls ) ) );
			var performanceWeight = 1.0 - preMatchWeight;

			// 2.10 Raw first innings win %.
			var rawWinPct = ( preMatchWinPct * preMatchWeight ) + ( performanceWinPct * performanceWeight );

			// 2.11 Display cap.
			var displayCap = FirstInningsDisplayCap( totalBalls, ballsBowled );

			var finalWinPct = Math.Min( rawWinPct, displayCap );

			return BuildFirstInningsResult( data, finalWinPct, parScore, projectedScore );
		}

		/// <summary>2.5 to 2.7 - base projection, resource floor, then wicket cap last.</summary>
		private static double ProjectFirstInningsScore(
			int runs, int totalBalls, int ballsBowled,
			double ballsRemaining, double wicketsRemaining, int maxWickets )
		{
			var currentRunsPerBall = (double)runs / ballsBowled;
			var baseProjection = currentRunsPerBall * totalBalls;

			// 2.5 Wicket resource modelling.
			var wicketMultiplier = Math.Pow( wicketsRemaining / maxWickets, 1.4 );
			var wicketImpact = Math.Pow( ballsRemaining / totalBalls, 0.5 );
			var wicketAdjusted = baseProjection * ( 0.70 + ( 0.42 * wicketMultiplier * wicketImpact ) );

			var rawProjected = Math.Max( baseProjection, wicketAdjusted );

			// 2.6 Resource floor.
			var resourceRunsPerBall = 0.60 + ( 0.10 * wicketsRemaining );
			var timeDecay = Math.Pow( ballsRemaining / totalBalls, 0.5 );
			var resourceFloor = runs + ( ballsRemaining * resourceRunsPerBall * timeDecay );

			var floorAdjusted = Math.Max( rawProjected, resourceFloor );

			// 2.7 Wicket cap is applied last and overrides the resource floor.
			var wicketCap = runs + ( wicketsRemaining * 25.0 );
			var projected = Math.Min( floorAdjusted, wicketCap );

			if ( wicketsRemaining == 0 ) projected = runs;

			return Math.Max( runs, projected );
		}

		/// <summary>2.8 - turn the projection into a win % against par, with caps.</summary>
		private static double PerformanceWinPct( double projectedScore, double parScore )
		{
			var performanceWinPct = 50.0 + ( ( ( projectedScore - parScore ) / parScore ) * 100.0 );

			var minWinPct = 5.0 + ( 15.0 * ( projectedScore / ( parScore * 0.60 ) ) );
			minWinPct = Math.Min( 20.0, Math.Max( 5.0, minWinPct ) );

			return Math.Min( 85.0, Math.Max( minWinPct, performanceWinPct ) );
		}

		/// <summary>2.11 - short overs games are allowed a higher cap.</summary>
		private static double FirstInningsDisplayCap( int totalBalls, int ballsBowled )
		{
			if ( totalBalls <= 50 ) return 85.0;
			if ( ballsBowled <= 50 ) return 75.0;

			var cap = 75.0 + ( 10.0 * ( (double)( ballsBowled - 50 ) / ( totalBalls - 50 ) ) );
			return Math.Min( 85.0, cap );
		}

		private static PulseResult BuildFirstInningsResult(
			FirstInningsLiveData data, double battingWinPct, double parScore, double projectedScore )
		{
			return new PulseResult
			{
				InningsNumber = 1,
				BattingTeamName = data.BattingTeamName,
				BowlingTeamName = data.BowlingTeamName,
				BattingTeamWinPct = Math.Round( battingWinPct, 1 ),
				BowlingTeamWinPct = Math.Round( 100.0 - battingWinPct, 1 ),
				ShowBowlingTeamWinPct = true,
				ProjectedScore = Math.Round( projectedScore ),
				ParScore = Math.Round( parScore, 1 ),
				BallsRemaining = data.RemainingBalls,
				WicketsRemaining = data.WicketsRemaining
			};
		}

		// ─────────────────────────────────────────────────────────────────────
		// Chase meter - spec sections 3.3 to 3.15
		// ─────────────────────────────────────────────────────────────────────
		public static PulseResult CalculateChase( ChaseLiveData data )
		{
			var runsNeeded = data.RunsNeeded;
			var ballsRemaining = data.RemainingBalls;
			var ballsBowled = data.BallsBowled;
			var totalBalls = data.TotalBalls;
			var wicketsRemaining = data.WicketsRemaining;
			var maxWickets = data.MaxWicketsAvailable;

			// 3.3 Match state safeguards.
			if ( runsNeeded <= 0 ) return BuildChaseResult( data, 100.0, false, 0, 0, 1, 0, false );
			if ( runsNeeded == 1 && ( ballsRemaining <= 0 || wicketsRemaining <= 0 ) )
				return BuildChaseResult( data, 50.0, true, 0, 0, 1, 0, false );
			if ( wicketsRemaining <= 0 ) return BuildChaseResult( data, 0.0, true, 0, 0, 1, 0, false );
			if ( ballsRemaining <= 0 ) return BuildChaseResult( data, 0.0, true, 0, 0, 1, 0, false );

			// 3.4 Start of chase override - use the innings break number directly.
			if ( ballsBowled == 0 )
			{
				var breakPct = Clamp( data.InningsBreakChaseWinPct, 1.0, 99.0 );
				return BuildChaseResult( data, breakPct, runsNeeded > 1, 0, 0, 1, 0, false );
			}

			// 3.6 Pressure score.
			var targetPace = totalBalls > 0 ? (double)data.Target / totalBalls : DefaultLeagueRunsPerBall;
			var requiredPace = (double)runsNeeded / ballsRemaining;
			var pressureScore = Clamp( targetPace / requiredPace, 0.25, 1.60 );

			var wicketFactor = ChaseWicketFactor( data, pressureScore );
			var momentumFactor = ChaseMomentumFactor( data, requiredPace );

			// 3.5 Raw chase meter and raw win %.
			var rawChaseMeter = pressureScore * wicketFactor * momentumFactor;
			var rawChaseWinPct = Clamp( 50.0 + ( ( rawChaseMeter - 1.0 ) * 55.0 ), 0.0, 100.0 );

			// 3.10 Chase prior fade - the innings break number stops mattering after 25 balls.
			var priorWeight = ballsBowled >= 25
				? 0.0
				: Clamp( 0.40 * Math.Pow( 1.0 - ( (double)ballsBowled / 25.0 ), 2.0 ), 0.0, 0.40 );

			var blendedWinPct = ( data.InningsBreakChaseWinPct * priorWeight )
				+ ( rawChaseWinPct * ( 1.0 - priorWeight ) );

			// 3.11 and 3.12 - control bias then low runs boost.
			var adjustedWinPct = Clamp( blendedWinPct + ChaseControlBias( data, pressureScore ), 0.0, 100.0 );
			adjustedWinPct = Clamp( adjustedWinPct + LowRunsNeededBoost( data ), 0.0, 100.0 );

			// 3.13 and 3.14 - chaos floor then the rise cap in the last five balls.
			var chaosFloor = LmsChaosFloor( data );
			var chaosActive = ballsRemaining <= 5 && chaosFloor > adjustedWinPct;
			var finalWinPct = ApplyFinalFiveBallProtection( data, adjustedWinPct, chaosFloor, requiredPace );

			// 3.15 Live output clamp.
			finalWinPct = Clamp( finalWinPct, 1.0, 99.0 );

			return BuildChaseResult( data, finalWinPct, runsNeeded > 1,
				pressureScore, wicketFactor, momentumFactor, chaosFloor, chaosActive );
		}

		/// <summary>3.7 and 3.8 - wickets in hand, early collapse, then the critical wicket penalty.</summary>
		private static double ChaseWicketFactor( ChaseLiveData data, double pressureScore )
		{
			double wicketsRemaining = data.WicketsRemaining;
			double maxWickets = data.MaxWicketsAvailable;
			double ballsRemaining = data.RemainingBalls;
			double totalBalls = data.TotalBalls;

			var baseFactor = Clamp( 0.65 + ( 0.35 * Math.Pow( wicketsRemaining / maxWickets, 0.70 ) ), 0.65, 1.00 );

			var collapsePenalty = Clamp(
				1.0 - ( ( data.WicketsLost / maxWickets ) * Math.Pow( ballsRemaining / totalBalls, 2.5 ) ),
				0.75, 1.00 );

			var rawFactor = baseFactor * collapsePenalty;
			var importance = Math.Max( 0.50, Math.Pow( ballsRemaining / totalBalls, 0.50 ) );
			var factor = 1.0 - ( ( 1.0 - rawFactor ) * importance );

			// 3.8 Critical wicket penalty - being one or two down hurts more late.
			var criticalPhase = Clamp( ( ballsRemaining - 15 ) / 50.0, 0.0, 1.0 );
			double penalty;
			if ( data.WicketsRemaining >= 3 )
			{
				penalty = 1.0;
			}
			else if ( data.WicketsRemaining == 2 )
			{
				penalty = 1.0 - ( 0.12 * criticalPhase );
			}
			else
			{
				var oneWicketDanger = Math.Max( 0.35, Math.Pow( ballsRemaining / totalBalls, 0.35 ) );
				penalty = 0.45 + ( 0.35 * oneWicketDanger );
			}

			return Clamp( factor * penalty, 0.30, 1.00 );
		}

		/// <summary>3.9 - recent scoring rate against the required rate, window shrinks late.</summary>
		private static double ChaseMomentumFactor( ChaseLiveData data, double requiredPace )
		{
			int window;
			if ( data.RemainingBalls > 50 ) window = 12;
			else if ( data.RemainingBalls > 25 ) window = 10;
			else if ( data.RemainingBalls > 10 ) window = 8;
			else window = 5;

			double actualBalls = Math.Max( 1, Math.Min( data.BallsBowled, window ) );
			var recentRunsPerBall = data.RecentRuns / actualBalls;

			if ( requiredPace <= 0 ) return 1.0;

			var ratio = recentRunsPerBall / requiredPace;
			return Clamp( 1.0 + ( ( ratio - 1.0 ) * 0.10 ), 0.82, 1.18 );
		}

		/// <summary>3.11 - a settled chase past 40 balls earns a bonus, cut back by wickets lost.</summary>
		private static double ChaseControlBias( ChaseLiveData data, double pressureScore )
		{
			if ( data.BallsBowled < 40 || pressureScore < 0.95 ) return 0.0;

			var fade = Clamp( ( data.BallsBowled - 40 ) / 20.0, 0.0, 1.0 );
			var progress = (double)data.BallsBowled / data.TotalBalls;
			var comfort = (double)data.WicketsRemaining / data.MaxWicketsAvailable;

			var bias = 10.0 * progress * comfort * fade;

			if ( data.WicketsLost == 5 ) bias *= 0.50;
			else if ( data.WicketsLost == 6 ) bias *= 0.25;
			else if ( data.WicketsLost >= 7 ) bias = 0.0;

			return Clamp( bias, 0.0, 10.0 );
		}

		/// <summary>3.12 - twelve or fewer runs needed is a lot easier than the formula thinks.</summary>
		private static double LowRunsNeededBoost( ChaseLiveData data )
		{
			if ( data.RunsNeeded > 12 ) return 0.0;

			var ease = Math.Max( 0.0, Math.Min( 1.0, ( 12.0 - data.RunsNeeded ) / 11.0 ) );
			var ballComfort = Math.Min( 1.0, Math.Pow( data.RemainingBalls / 20.0, 0.60 ) );
			var wicketComfort = 0.60 + ( 0.40 * Math.Pow(
				(double)data.WicketsRemaining / data.MaxWicketsAvailable, 0.50 ) );

			return 35.0 * ease * ballComfort * wicketComfort;
		}

		/// <summary>3.13 - in LMS the last ball is a Home Run worth 12, so nothing is dead.</summary>
		private static double LmsChaosFloor( ChaseLiveData data )
		{
			if ( data.RemainingBalls > 5 ) return 0.0;

			var maxStandardRuns = ( ( data.RemainingBalls - 1 ) * 6 ) + 12;
			if ( maxStandardRuns <= 0 ) return 0.0;

			var reachability = (double)data.RunsNeeded / maxStandardRuns;
			var baseChance = Clamp( 45.0 * ( 1.0 - reachability ), 0.0, 45.0 );
			var scarcity = Math.Pow( data.RemainingBalls / 5.0, 0.75 );

			return Clamp( baseChance * scarcity, 0.0, 45.0 );
		}

		/// <summary>3.14 - stop the number jumping around on the last few balls.</summary>
		private static double ApplyFinalFiveBallProtection(
			ChaseLiveData data, double adjustedWinPct, double chaosFloor, double requiredPace )
		{
			if ( data.RemainingBalls > 5 ) return adjustedWinPct;

			var chaosAdjusted = Math.Max( adjustedWinPct, chaosFloor );

			if ( !data.PreviousRunsNeeded.HasValue
				|| !data.PreviousBallsRemaining.HasValue
				|| !data.PreviousChaseWinPct.HasValue
				|| data.PreviousBallsRemaining.Value <= 0 )
			{
				return chaosAdjusted;
			}

			var previousPace = (double)data.PreviousRunsNeeded.Value / data.PreviousBallsRemaining.Value;
			var improvement = previousPace - requiredPace;

			// Pressure got worse - never let the win % go up.
			if ( improvement <= 0 ) return Math.Min( chaosAdjusted, data.PreviousChaseWinPct.Value );

			var maxRise = Clamp( 5.0 + ( 20.0 * improvement / previousPace ), 3.0, 12.0 );
			return Math.Min( chaosAdjusted, data.PreviousChaseWinPct.Value + maxRise );
		}

		private static PulseResult BuildChaseResult(
			ChaseLiveData data, double chaseWinPct, bool showDefending,
			double pressureScore, double wicketFactor, double momentumFactor,
			double chaosFloor, bool chaosActive )
		{
			var currentRunsPerBall = data.BallsBowled > 0
				? (double)data.RunsScored / data.BallsBowled
				: 0.0;

			var requiredRunsPerBall = data.RemainingBalls > 0
				? (double)data.RunsNeeded / data.RemainingBalls
				: 0.0;

			return new PulseResult
			{
				InningsNumber = 2,
				BattingTeamName = data.ChasingTeamName,
				BowlingTeamName = data.DefendingTeamName,
				BattingTeamWinPct = Math.Round( chaseWinPct, 1 ),
				BowlingTeamWinPct = showDefending ? Math.Round( 100.0 - chaseWinPct, 1 ) : 0.0,
				ShowBowlingTeamWinPct = showDefending,
				RunsNeeded = data.RunsNeeded,
				BallsRemaining = data.RemainingBalls,
				WicketsRemaining = data.WicketsRemaining,
				RequiredRunsPerBall = Math.Round( requiredRunsPerBall, 2 ),
				CurrentRunsPerBall = Math.Round( currentRunsPerBall, 2 ),
				PressureScore = Math.Round( pressureScore, 3 ),
				WicketFactor = Math.Round( wicketFactor, 3 ),
				MomentumFactor = Math.Round( momentumFactor, 3 ),
				LmsChaosFloor = Math.Round( chaosFloor, 1 ),
				IsLmsChaosActive = chaosActive
			};
		}

		private static double Clamp( double value, double minimum, double maximum )
		{
			if ( double.IsNaN( value ) ) return minimum;
			if ( value < minimum ) return minimum;
			if ( value > maximum ) return maximum;
			return value;
		}
	}
}
