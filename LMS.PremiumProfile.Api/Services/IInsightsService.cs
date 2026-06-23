using LMS.PremiumProfile.Api.Models;

namespace LMS.PremiumProfile.Api.Services;

public interface IInsightsService
{
    /// <summary>Career head-to-head stats for a bowler vs batter pair.</summary>
    Task<H2HResponse> GetH2HAsync(
        uint bowlerId, uint batterId,
        uint? seasonId, uint? leagueId,
        CancellationToken ct = default);

    Task<PulseResponse> GetPulseAsync(uint fixtureId, CancellationToken ct = default);

    /// <summary>Highlight video clips for a fixture.</summary>
    Task<ClipsResponse> GetClipsAsync(
        uint fixtureId, string? clipType,
        CancellationToken ct = default);
}
