using LMS.PremiumProfile.Api.Models;

namespace LMS.PremiumProfile.Api.Services;

public interface IBowlingProfileService
{
    Task<BowlingProfileResponse> GetBowlingProfileAsync(
        uint playerId, uint? seasonId, uint? leagueId,
        int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct = default);
}
