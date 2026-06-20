using LMS.PremiumProfile.Api.Models;

namespace LMS.PremiumProfile.Api.Services;

public interface ITeamProfileService
{
    Task<TeamProfileResponse> GetTeamProfileAsync(
        uint teamId, uint? seasonId, uint? leagueId,
        int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct = default);
}
