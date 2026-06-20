using LMS.Migration.Core.Models;
using Newtonsoft.Json.Linq;

namespace LMS.Migration.Core.Parsers
{
    public class ParsedFixture
    {
        public List<BallEvent> Balls { get; set; } = new();
        public List<Partnership> Partnerships { get; set; } = new();
        public List<PlayerInningsSummary> PlayerSummaries { get; set; } = new();
        /// <summary>Official innings scores from the JSON Score objects —
        /// authoritative (live scorer corrections update these, not the
        /// ball stream). Used for match result and Match RPBall.</summary>
        public List<(uint BattingTeamId, int Runs, byte Wickets)> InningsScores { get; set; } = new();
        public string? MatchResultRaw { get; set; }
        public DateTime GameDate { get; set; } = DateTime.UnixEpoch;
        public byte BallsPerOver { get; set; } = 5;
        public uint VenueId { get; set; }
        public uint RegionId { get; set; }
        public byte CountryId { get; set; }
        public byte PitchCondition { get; set; }
    }

    /// <summary>
    /// Parses the live_scoring_core OutdoorCricketFixtureState JSON.
    ///
    /// Structure (verified against production sample, fixture 515260):
    ///   Root: Id, BallsPerOver, VenueId, RegionId, CountryId,
    ///         Innings[2], Events[] (BallBowled carries TimeStamp),
    ///         ExtraFixtureInformation.PitchCondition
    ///   Innings: BattingTeam/BowlingTeam ($ref), Batsmen[], Bowlers[],
    ///            Overs[] → Events[] = Ball objects
    ///   Ball: Bowler/Striker/NonStriker/Keeper/Fielder ($ref or inline),
    ///         BallResults[] typed: Runs{Runs}, DotBall, Wide/NoBall/Bye/
    ///         LegBye{AdditionalRunsNotFromBat}, Caught, Bowled, Lbw,
    ///         RunOut, Stumped, DoublePlay, ...
    ///
    /// CRITICAL: the JSON uses Newtonsoft $id/$ref references. Any object
    /// (including entire wicket balls) may appear as {"$ref":"..."} — every
    /// token must be resolved against the $id index before reading fields.
    /// </summary>
    public class FixtureStateParser
    {
        public ParsedFixture Parse(uint fixtureId, string fixtureStateJson)
        {
            var root = JObject.Parse(fixtureStateJson);
            var result = new ParsedFixture();

            // ── Build the $id → object index, then resolve $refs ────────
            var index = new Dictionary<string, JObject>();
            BuildIdIndex(root, index);
            JObject? R(JToken? t) => Resolve(t, index);

            // ── Root facts ───────────────────────────────────────────────
            result.BallsPerOver = (byte)(root.Value<int?>("BallsPerOver") ?? 5);
            int firstInningsOvers = root.Value<int?>("FirstInningsOvers") ?? 20;
            int secondInningsOvers = root.Value<int?>("SecondInningsOvers") ?? 20;
            result.VenueId = root.Value<uint?>("VenueId") ?? 0;
            result.RegionId = root.Value<uint?>("RegionId") ?? 0;
            result.CountryId = (byte)(root.Value<int?>("CountryId") ?? 0);
            result.PitchCondition = (byte)(R(root["ExtraFixtureInformation"])?.Value<int?>("PitchCondition") ?? 0);

            // Game date + per-ball timestamps from root Events (BallBowled)
            var ballTimestamps = new Queue<DateTime>();
            if (root["Events"] is JArray rootEvents)
            {
                foreach (var ev in rootEvents)
                {
                    var type = ev.Value<string>("$type") ?? "";
                    var ts = ev.Value<DateTime?>("TimeStamp");
                    if (type.EndsWith("GameSetUp") && ts.HasValue)
                        result.GameDate = ts.Value.Date;
                    if (type.EndsWith("BallBowled") && ts.HasValue)
                        ballTimestamps.Enqueue(ts.Value);
                }
            }

            if (root["Innings"] is not JArray inningsList) return result;

            byte inningsNumber = 1;
            foreach (var inningsToken in inningsList)
            {
                var innings = R(inningsToken);
                if (innings == null) continue;

                uint battingTeamId = R(innings["BattingTeam"])?.Value<uint?>("Id") ?? 0;
                uint bowlingTeamId = R(innings["BowlingTeam"])?.Value<uint?>("Id") ?? 0;

                // Official innings score (authoritative for result/RPBall)
                var scoreObj = R(innings["Score"]);
                result.InningsScores.Add((battingTeamId,
                    scoreObj?.Value<int?>("Runs") ?? 0,
                    (byte)(scoreObj?.Value<int?>("Wickets") ?? 0)));

                ushort scoreAtBall = 0;
                byte wicketsAtBall = 0;
                byte partnershipNumber = 1;
                Partnership? currentPartnership = null;

                // RuleEngine: final over has different extras/home-run rules
                int inningsOvers = inningsNumber == 1 ? firstInningsOvers : secondInningsOvers;

                foreach (var overToken in innings["Overs"] as JArray ?? new JArray())
                {
                    var over = R(overToken);
                    if (over == null) continue;
                    byte overNumber = (byte)(over.Value<int?>("OverNumber") ?? 0);
                    bool isFinalOver = overNumber >= inningsOvers;

                    // Materialise events in order: Ball deliveries AND BatsmanRetired
                    // events must both be collected to keep correct sequence for
                    // partnership tracking (retirements fall between balls).
                    var overItems = new List<(bool IsRetirement, JObject Obj)>();
                    foreach (var evToken in over["Events"] as JArray ?? new JArray())
                    {
                        var resolved = R(evToken);
                        if (resolved == null) continue;
                        string evType = resolved.Value<string>("$type") ?? "";
                        if (evType.EndsWith(".Ball"))
                            overItems.Add((false, resolved));
                        else if (evType.Contains("BatsmanRetired"))
                            overItems.Add((true, resolved));
                    }
                    int totalBalls = overItems.Count(x => !x.IsRetirement);

                    // RuleEngine state for this over
                    int bowlerExtrasSeen = 0;   // wides/no-balls already bowled this over
                    int nonExtraBallsSeen = 0;  // deliveries without bowler extras

                    byte ballSeq = 0;
                    int ballIndex = 0; // index among balls only (for home-run last-ball check)
                    for (int i = 0; i < overItems.Count; i++)
                    {
                        var (isRetirement, item) = overItems[i];

                        // ── BatsmanRetired: close the current partnership ────
                        // A retirement is NOT a wicket, so without this the old
                        // partnership would keep accumulating runs for every
                        // subsequent batter until the next actual dismissal.
                        if (isRetirement)
                        {
                            if (currentPartnership != null)
                            {
                                result.Partnerships.Add(currentPartnership);
                                currentPartnership = null;
                                partnershipNumber++;
                            }
                            continue;
                        }

                        var ball = item;
                        ballIndex++;
                        ballSeq++;

                        var striker = R(ball["Striker"]);
                        var bowler = R(ball["Bowler"]);

                        var b = new BallEvent
                        {
                            FixtureId = fixtureId,
                            InningsNumber = inningsNumber,
                            OverNumber = overNumber,
                            BallSequence = ballSeq,
                            BallTimestamp = ballTimestamps.Count > 0 ? ballTimestamps.Dequeue()
                                          : (result.GameDate == DateTime.UnixEpoch ? DateTime.UnixEpoch : result.GameDate),
                            BowlerId = bowler?.Value<uint?>("Id") ?? 0,
                            StrikerId = striker?.Value<uint?>("Id") ?? 0,
                            NonStrikerId = R(ball["NonStriker"])?.Value<uint?>("Id") ?? 0,
                            KeeperId = R(ball["Keeper"])?.Value<uint?>("Id") ?? 0,
                            FielderId = R(ball["Fielder"])?.Value<uint?>("Id") ?? 0,
                            BattingPosition = (byte)(striker?.Value<int?>("Order") ?? 0),
                            BattingTeamId = battingTeamId,
                            BowlingTeamId = bowlingTeamId,
                            BallsPerOver = result.BallsPerOver,
                            TotalOvers = (byte)inningsOvers,
                            PitchCondition = result.PitchCondition,
                            GameDate = result.GameDate
                        };

                        // ── BallResults (0 or 1 per ball in practice) ────
                        bool thisBallHasBowlerExtras = false;

                        foreach (var res in ball["BallResults"] as JArray ?? new JArray())
                        {
                            var r = R(res);
                            if (r == null) continue;
                            string type = (r.Value<string>("$type") ?? "").Split('.').Last();
                            int runs = r.Value<int?>("Runs") ?? 0;
                            int additional = r.Value<int?>("AdditionalRunsNotFromBat") ?? 0;

                            // RuleEngine: first wide/no-ball in an over = 1 run;
                            // subsequent ones in the same over = 3 (except in the
                            // final over, always 1). Credited to the striker.
                            int bowlerExtraScore =
                                (!isFinalOver && bowlerExtrasSeen > 0) ? 3 : 1;

                            switch (type)
                            {
                                case "Runs":
                                    // RuleEngine home run: a six off the last
                                    // delivery of the innings' final over,
                                    // when 4 of 5 balls are already bowled,
                                    // scores 12 (HomeRunScore).
                                    if (runs == 6 && isFinalOver
                                        && ballIndex == totalBalls
                                        && nonExtraBallsSeen == result.BallsPerOver - 1)
                                    {
                                        b.RunsOffBat += 12;
                                        b.HomeRuns += 1;
                                    }
                                    else
                                    {
                                        b.RunsOffBat += (byte)runs;
                                    }
                                    break;
                                case "DotBall": break;
                                case "Wide":
                                    b.ExtrasWide += (byte)(bowlerExtraScore + additional);
                                    thisBallHasBowlerExtras = true;
                                    break;
                                case "NoBall":
                                    b.ExtrasNoBall += (byte)(bowlerExtraScore + additional);
                                    thisBallHasBowlerExtras = true;
                                    break;
                                case "Bye": b.ExtrasBye += (byte)additional; break;
                                case "LegBye": b.ExtrasLegBye += (byte)additional; break;
                                // Steal: AdditionalRunsNotFromBat counts in
                                // TotalExtras (RuleEngine), stored under
                                // extras_bye; steal credited to non-striker.
                                case "HomeRun":
                                case "HomeRuns": b.HomeRuns += 1; b.RunsOffBat += (byte)runs; b.ExtrasBye += (byte)additional; break;
                                case "Steal": b.Steal += 1; b.RunsOffBat += (byte)runs; b.ExtrasBye += (byte)additional; break;
                                default:
                                    // Remaining types are dismissals:
                                    // Caught, Bowled, Lbw, RunOut, Stumped,
                                    // DoublePlay, HitWicket, ...
                                    b.IsWicket = true;
                                    b.WicketType = type;
                                    if (type == "DoublePlay") b.DoublePlay = 1;
                                    b.RunsOffBat += (byte)runs;
                                    b.ExtrasBye += (byte)additional;

                                    // Exact fielding credits from the result:
                                    // Caught.Catcher ($ref), RunOut.ThrowerUserId
                                    var catcher = Resolve(r["Catcher"], index);
                                    if (catcher?.Value<uint?>("Id") is uint catcherId && catcherId != 0)
                                        b.FielderId = catcherId;
                                    if (r.Value<uint?>("ThrowerUserId") is uint throwerId && throwerId != 0)
                                        b.FielderId = throwerId;
                                    break;
                            }
                        }

                        // Advance the RuleEngine per-over counters
                        if (thisBallHasBowlerExtras) bowlerExtrasSeen++;
                        else nonExtraBallsSeen++;

                        scoreAtBall = (ushort)(scoreAtBall + b.RunsOffBat + b.ExtrasWide
                                        + b.ExtrasNoBall + b.ExtrasBye + b.ExtrasLegBye);
                        if (b.IsWicket) wicketsAtBall++;
                        b.ScoreAtBall = scoreAtBall;
                        b.WicketsAtBall = wicketsAtBall;

                        // ── Partnership tracking ─────────────────────────
                        currentPartnership ??= new Partnership
                        {
                            FixtureId = fixtureId,
                            InningsNumber = inningsNumber,
                            PartnershipNumber = partnershipNumber,
                            Batter1Id = b.StrikerId,
                            Batter2Id = b.NonStrikerId,
                            BattingTeamId = battingTeamId,
                            BowlingTeamId = bowlingTeamId,
                            StartOver = overNumber,
                            GameDate = result.GameDate
                        };
                        currentPartnership.RunsTogether += b.RunsOffBat;
                        if (b.IsLegalBall) currentPartnership.BallsTogether++;
                        if (b.IsBoundary) currentPartnership.FoursTogether++;
                        if (b.IsSix) currentPartnership.SixesTogether++;
                        currentPartnership.EndOver = overNumber;

                        if (b.IsWicket)
                        {
                            result.Partnerships.Add(currentPartnership);
                            currentPartnership = null;
                            partnershipNumber++;
                        }

                        result.Balls.Add(b);
                    }
                }

                if (currentPartnership != null)
                    result.Partnerships.Add(currentPartnership);

                // ── Authoritative player summaries from innings aggregates ─
                ExtractSummaries(innings, battingTeamId, bowlingTeamId,
                                 result.BallsPerOver, result.PlayerSummaries, index);

                inningsNumber++;
            }

            return result;
        }

        private void ExtractSummaries(
            JObject innings, uint battingTeamId, uint bowlingTeamId,
            byte ballsPerOver, List<PlayerInningsSummary> summaries,
            Dictionary<string, JObject> index)
        {
            PlayerInningsSummary Get(uint playerId, uint teamId)
            {
                var s = summaries.FirstOrDefault(x => x.PlayerId == playerId);
                if (s == null)
                {
                    s = new PlayerInningsSummary { PlayerId = playerId, TeamId = teamId };
                    summaries.Add(s);
                }
                return s;
            }

            foreach (var t in innings["Batsmen"] as JArray ?? new JArray())
            {
                var bat = Resolve(t, index);
                var id = bat?.Value<uint?>("Id");
                if (bat == null || id is null or 0) continue;

                var s = Get(id.Value, battingTeamId);
                s.Batted = true;
                s.RunsScored = (ushort)(bat.Value<int?>("RunsScored") ?? 0);
                s.BallsFaced = (ushort)(bat.Value<int?>("BallsFaced") ?? 0);
                s.BattingOrder = (byte)(bat.Value<int?>("Order") ?? 0);
                // Not out = never dismissed: OutEvent is null
                s.IsNotOut = bat["OutEvent"] == null || bat["OutEvent"]!.Type == JTokenType.Null;
            }

            foreach (var t in innings["Bowlers"] as JArray ?? new JArray())
            {
                var bowl = Resolve(t, index);
                var id = bowl?.Value<uint?>("Id");
                if (bowl == null || id is null or 0) continue;

                var overs = Resolve(bowl["Overs"], index);
                int completedOvers = overs?.Value<int?>("Over") ?? 0;
                int extraBalls = overs?.Value<int?>("Ball") ?? 0;

                var s = Get(id.Value, bowlingTeamId);
                s.Bowled = true;
                s.BallsBowled = (ushort)(completedOvers * ballsPerOver + extraBalls);
                s.RunsConceded = (ushort)(bowl.Value<int?>("RunsConceded") ?? 0);
                s.Wickets = (byte)(bowl.Value<int?>("Wickets") ?? 0);
            }
        }

        // ── $id / $ref machinery ─────────────────────────────────────────
        private static void BuildIdIndex(JToken token, Dictionary<string, JObject> index)
        {
            if (token is JObject obj)
            {
                var id = obj.Value<string>("$id");
                if (id != null) index[id] = obj;
                foreach (var prop in obj.Properties())
                    BuildIdIndex(prop.Value, index);
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                    BuildIdIndex(item, index);
            }
        }

        private static JObject? Resolve(JToken? token, Dictionary<string, JObject> index)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token is not JObject obj) return null;
            var refId = obj.Value<string>("$ref");
            if (refId != null)
                return index.TryGetValue(refId, out var resolved) ? resolved : null;
            return obj;
        }
    }
}
