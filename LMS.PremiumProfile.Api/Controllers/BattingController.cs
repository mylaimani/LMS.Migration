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
    /// <param name="playerId">LMS player ID (required)</param>
    /// <param name="seasonId">Optional season filter</param>
    /// <param name="leagueId">Optional league filter</param>
    /// <param name="year">Optional year filter, e.g. 2024</param>
    /// <param name="fromDate">Optional start date, e.g. 2024-01-01</param>
    /// <param name="toDate">Optional end date, e.g. 2024-12-31</param>
    [HttpGet("{playerId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBattingProfile(
        [FromRoute] uint     playerId,
        [FromQuery] uint?    seasonId = null,
        [FromQuery] uint?    leagueId = null,
        [FromQuery] int?     year     = null,
        [FromQuery] DateOnly? fromDate = null,
        [FromQuery] DateOnly? toDate   = null,
        CancellationToken ct = default)
    {
        if (playerId == 0)
            return BadRequest("playerId must be greater than 0.");

        if (year.HasValue && (year < 2000 || year > DateTime.UtcNow.Year))
            return BadRequest($"year must be between 2000 and {DateTime.UtcNow.Year}.");

        if (fromDate.HasValue && toDate.HasValue && fromDate > toDate)
            return BadRequest("fromDate must be before toDate.");

        var result = await _service.GetBattingProfileAsync(
            playerId, seasonId, leagueId, year, fromDate, toDate, ct);

        return Ok(result);
    }
}
