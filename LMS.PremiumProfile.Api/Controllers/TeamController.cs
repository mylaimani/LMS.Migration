using LMS.PremiumProfile.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LMS.PremiumProfile.Api.Controllers;

[ApiController]
[Route("api/team")]
public class TeamController : ControllerBase
{
    private readonly ITeamProfileService _service;

    public TeamController(ITeamProfileService service)
    {
        _service = service;
    }

    /// <summary>
    /// Returns team phase analysis, league benchmark, top partnerships, and all-time club greats.
    /// All filters are optional. Club greats are always all-time (filters ignored for that section).
    /// </summary>
    [HttpGet("{teamId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetTeamProfile(
        [FromRoute] uint      teamId,
        [FromQuery] uint?     seasonId = null,
        [FromQuery] uint?     leagueId = null,
        [FromQuery] int?      year     = null,
        [FromQuery] DateOnly? fromDate = null,
        [FromQuery] DateOnly? toDate   = null,
        CancellationToken ct = default)
    {
        if (teamId == 0)
            return BadRequest("teamId must be greater than 0.");

        if (year.HasValue && (year < 2000 || year > DateTime.UtcNow.Year))
            return BadRequest($"year must be between 2000 and {DateTime.UtcNow.Year}.");

        if (fromDate.HasValue && toDate.HasValue && fromDate > toDate)
            return BadRequest("fromDate must be earlier than toDate.");

        var result = await _service.GetTeamProfileAsync(
            teamId, seasonId, leagueId, year, fromDate, toDate, ct);

        return Ok(result);
    }
}
