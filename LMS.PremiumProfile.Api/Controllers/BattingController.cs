using LMS.PremiumProfile.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LMS.PremiumProfile.Api.Controllers;

[ApiController]
[Route("api/batting")]
public class BattingController : ControllerBase
{
    private readonly IBattingProfileService _service;

    public BattingController(IBattingProfileService service)
    {
        _service = service;
    }

    /// <summary>
    /// Returns the full batting profile for a player.
    /// Includes phase stats, scoring pattern, favourite/nemesis bowlers, and partnerships.
    /// </summary>
    /// <param name="playerId">LMS player ID</param>
    /// <param name="seasonId">Optional season filter</param>
    /// <param name="leagueId">Optional league filter</param>
    [HttpGet("{playerId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBattingProfile(
        [FromRoute] uint playerId,
        [FromQuery] uint? seasonId = null,
        [FromQuery] uint? leagueId = null,
        CancellationToken ct = default)
    {
        if (playerId == 0)
            return BadRequest("playerId must be greater than 0.");

        var result = await _service.GetBattingProfileAsync(playerId, seasonId, leagueId, ct);
        return Ok(result);
    }
}
