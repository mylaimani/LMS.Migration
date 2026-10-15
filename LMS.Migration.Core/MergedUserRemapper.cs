// By Mani 15 Oct 2026
using LMS.Migration.Core.Models;
using LMS.Migration.Core.Parsers;

namespace LMS.Migration.Core
{
    /// <summary>
    /// Replaces merged-away player ids with the surviving id on everything the parser
    /// produced for one fixture (balls, partnerships, player summaries) and on clips.
    /// Team ids are not touched - only user ids are merged in Spawtz.
    /// </summary>
    public class MergedUserRemapper
    {
        private readonly Dictionary<uint, uint> _oldToFinalUserId;

        public MergedUserRemapper(Dictionary<uint, uint> oldToFinalUserId)
        {
            _oldToFinalUserId = oldToFinalUserId;
        }

        public int MapSize => _oldToFinalUserId.Count;

        /// <summary>The id to store for this player: the surviving id if merged, otherwise the same id.</summary>
        public uint Resolve(uint userId)
        {
            if (userId == 0) return 0;
            if (_oldToFinalUserId.TryGetValue(userId, out uint finalUserId)) return finalUserId;
            return userId;
        }

        /// <summary>Remaps every player id in the parsed fixture. Returns how many ids were replaced.</summary>
        public int Apply(ParsedFixture parsed)
        {
            int replaced = 0;

            foreach (BallEvent ball in parsed.Balls)
            {
                replaced += ApplyToBall(ball);
            }

            foreach (Partnership partnership in parsed.Partnerships)
            {
                uint batter1 = Resolve(partnership.Batter1Id);
                if (batter1 != partnership.Batter1Id) { partnership.Batter1Id = batter1; replaced++; }

                uint batter2 = Resolve(partnership.Batter2Id);
                if (batter2 != partnership.Batter2Id) { partnership.Batter2Id = batter2; replaced++; }
            }

            foreach (PlayerInningsSummary summary in parsed.PlayerSummaries)
            {
                uint playerId = Resolve(summary.PlayerId);
                if (playerId != summary.PlayerId) { summary.PlayerId = playerId; replaced++; }
            }

            return replaced;
        }

        private int ApplyToBall(BallEvent ball)
        {
            int replaced = 0;

            uint bowler = Resolve(ball.BowlerId);
            if (bowler != ball.BowlerId) { ball.BowlerId = bowler; replaced++; }

            uint striker = Resolve(ball.StrikerId);
            if (striker != ball.StrikerId) { ball.StrikerId = striker; replaced++; }

            uint nonStriker = Resolve(ball.NonStrikerId);
            if (nonStriker != ball.NonStrikerId) { ball.NonStrikerId = nonStriker; replaced++; }

            uint fielder = Resolve(ball.FielderId);
            if (fielder != ball.FielderId) { ball.FielderId = fielder; replaced++; }

            uint keeper = Resolve(ball.KeeperId);
            if (keeper != ball.KeeperId) { ball.KeeperId = keeper; replaced++; }

            return replaced;
        }

        /// <summary>Remaps the five player ids on a highlight clip. Returns how many ids were replaced.</summary>
        public int ApplyToClip(ClipRecord clip)
        {
            int replaced = 0;

            uint bowler = Resolve(clip.BowlerId);
            if (bowler != clip.BowlerId) { clip.BowlerId = bowler; replaced++; }

            uint striker = Resolve(clip.StrikerId);
            if (striker != clip.StrikerId) { clip.StrikerId = striker; replaced++; }

            uint nonStriker = Resolve(clip.NonStrikerId);
            if (nonStriker != clip.NonStrikerId) { clip.NonStrikerId = nonStriker; replaced++; }

            uint keeper = Resolve(clip.KeeperId);
            if (keeper != clip.KeeperId) { clip.KeeperId = keeper; replaced++; }

            uint fielder = Resolve(clip.FielderId);
            if (fielder != clip.FielderId) { clip.FielderId = fielder; replaced++; }

            return replaced;
        }

        /// <summary>
        /// Player ids that appear more than once in the summaries after remapping - this happens when
        /// both accounts of a merged player played in the same fixture. Reported so the fixture can be
        /// looked at by hand; the rows are still written.
        /// </summary>
        public List<uint> DuplicatePlayerIds(ParsedFixture parsed)
        {
            var seen = new HashSet<(uint PlayerId, uint TeamId)>();
            var duplicates = new List<uint>();

            foreach (PlayerInningsSummary summary in parsed.PlayerSummaries)
            {
                bool isNew = seen.Add((summary.PlayerId, summary.TeamId));
                if (!isNew && !duplicates.Contains(summary.PlayerId)) duplicates.Add(summary.PlayerId);
            }

            return duplicates;
        }
    }
}
