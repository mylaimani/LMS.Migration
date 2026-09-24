namespace LMS.Migration.Core.Pulse
{
	/// by Mani /// COPY of the input/result classes from
	/// Spawtz.LiveScoring.Web/Pulse/PulseModels.cs (24 Sep 2026).
	/// Keep in step with PulseCalculator.cs - see the note there.

	/// <summary>
	/// Everything the first innings predictor needs, read from the
	/// OutdoorCricketDetail view model while innings one is live.
	/// </summary>
	public class FirstInningsLiveData
	{
		public string BattingTeamName { get; set; }
		public string BowlingTeamName { get; set; }
		public int Runs { get; set; }
		public int WicketsLost { get; set; }
		public int RemainingBalls { get; set; }
		public int TotalBalls { get; set; }
		public int MaxWicketsAvailable { get; set; }
		public double PreMatchBattingWinPct { get; set; }
		public double ParScoreFromServer { get; set; }

		public int BallsBowled
		{
			get { return TotalBalls - RemainingBalls; }
		}

		public int WicketsRemaining
		{
			get
			{
				var remaining = MaxWicketsAvailable - WicketsLost;
				if ( remaining < 0 ) return 0;
				if ( remaining > MaxWicketsAvailable ) return MaxWicketsAvailable;
				return remaining;
			}
		}
	}

	/// <summary>
	/// Everything the chase meter needs, read from the OutdoorCricketDetail
	/// view model once innings two is live.
	/// </summary>
	public class ChaseLiveData
	{
		public string ChasingTeamName { get; set; }
		public string DefendingTeamName { get; set; }
		public int Target { get; set; }
		public int RunsScored { get; set; }
		public int WicketsLost { get; set; }
		public int TotalBalls { get; set; }
		public int RemainingBalls { get; set; }
		public int MaxWicketsAvailable { get; set; }
		public int RecentRuns { get; set; }
		public double InningsBreakChaseWinPct { get; set; }

		// Null until the second ball of the chase - see spec section 3.14.
		public int? PreviousRunsNeeded { get; set; }
		public int? PreviousBallsRemaining { get; set; }
		public double? PreviousChaseWinPct { get; set; }

		public int BallsBowled
		{
			get { return TotalBalls - RemainingBalls; }
		}

		public int RunsNeeded
		{
			get
			{
				var needed = Target - RunsScored;
				if ( needed < 0 ) return 0;
				if ( needed > Target ) return Target;
				return needed;
			}
		}

		public int WicketsRemaining
		{
			get
			{
				var remaining = MaxWicketsAvailable - WicketsLost;
				if ( remaining < 0 ) return 0;
				if ( remaining > MaxWicketsAvailable ) return MaxWicketsAvailable;
				return remaining;
			}
		}
	}

	/// <summary>
	/// The Pulse view model that gets persisted to RethinkDB and pushed to
	/// the Pulse page over SignalR as view model type "OutdoorCricketPulse".
	/// </summary>
	public class PulseResult
	{
		public int FixtureId { get; set; }
		public int InningsNumber { get; set; }

		public string BattingTeamName { get; set; }
		public string BowlingTeamName { get; set; }

		public double BattingTeamWinPct { get; set; }
		public double BowlingTeamWinPct { get; set; }
		public bool ShowBowlingTeamWinPct { get; set; }

		// Team ratings from LMST20, shown under the win chance the same way the
		// phone app's Pulse card shows them. Zero means we had no ratings.
		public double BattingTeamRating { get; set; }
		public double BowlingTeamRating { get; set; }

		// Chase only - left at zero during the first innings.
		public int RunsNeeded { get; set; }
		public int BallsRemaining { get; set; }
		public int WicketsRemaining { get; set; }
		public double RequiredRunsPerBall { get; set; }
		public double CurrentRunsPerBall { get; set; }

		// First innings only - left at zero during the chase.
		public double ProjectedScore { get; set; }
		public double ParScore { get; set; }

		// Diagnostics so we can explain a number when someone questions it.
		public double PressureScore { get; set; }
		public double WicketFactor { get; set; }
		public double MomentumFactor { get; set; }
		public double LmsChaosFloor { get; set; }
		public bool IsLmsChaosActive { get; set; }
		public string PreMatchSource { get; set; }
	}
}
