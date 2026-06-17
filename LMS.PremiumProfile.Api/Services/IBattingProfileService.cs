using LMS.PremiumProfile.Api.Models;

namespace LMS.PremiumProfile.Api.Services;

public interface IBattingProfileService
{
    Task<BattingProfileResponse> GetBattingProfileAsync(
        uint  playerId,
        uint? seasonId,
        uint? leagueId,
        CancellationToken ct = default);
}
