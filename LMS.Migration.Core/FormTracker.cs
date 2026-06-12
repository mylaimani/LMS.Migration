namespace LMS.Migration.Core
{
    /// <summary>
    /// Tracks each team's last 6 match results to produce the Form Score
    /// (spec §3.2): Win = +1, Loss = -1, Tie / No Result = 0; score = sum ÷ 6.
    /// Fixtures MUST be processed in chronological order so that the form
    /// used for Match N only contains results prior to Match N
    /// (opposition strength is locked pre-match).
    /// </summary>
    public class FormTracker
    {
        private const int Window = 6;
        private readonly Dictionary<uint, Queue<sbyte>> _results = new();

        /// <summary>Form score BEFORE the current match: sum of last 6 ÷ 6.</summary>
        public float FormScore(uint teamId)
        {
            if (!_results.TryGetValue(teamId, out var q) || q.Count == 0) return 0f;
            int sum = 0;
            foreach (var r in q) sum += r;
            return sum / (float)Window;
        }

        /// <summary>Record a result AFTER the match has been processed.</summary>
        public void Record(uint teamId, sbyte result)   // +1 win, -1 loss, 0 tie/NR
        {
            if (!_results.TryGetValue(teamId, out var q))
            {
                q = new Queue<sbyte>(Window);
                _results[teamId] = q;
            }
            if (q.Count == Window) q.Dequeue();
            q.Enqueue(result);
        }
    }
}
