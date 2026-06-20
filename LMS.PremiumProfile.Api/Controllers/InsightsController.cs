// API 4 is hidden pending spec clarification (LMS Pulse project alignment, endpoint design).
// Re-enable by: (1) removing [ApiExplorerSettings] below, (2) uncommenting IInsightsService in Program.cs.
using LMS.PremiumProfile.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;

namespace LMS.PremiumProfile.Api.Controllers;

[ApiController]
[Route("api")]
[ApiExplorerSettings(IgnoreApi = true)]   // hidden — re-enable when API 4 spec is finalised
public class InsightsController : ControllerBase
{
    private readonly IInsightsService _service;

    public InsightsController(IInsightsService service)
    {
        _service = service;
    }

    // ── H2H ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Career head-to-head stats for a specific bowler vs batter pair.
    /// Uses the lms.h2h_stats MV (fast, career) unless seasonId/leagueId is supplied,
    /// in which case it queries lms.ball_events directly.
    /// </summary>
    [HttpGet("h2h/{bowlerId:int}/{batterId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetH2H(
        [FromRoute] uint  bowlerId,
        [FromRoute] uint  batterId,
        [FromQuery] uint? seasonId = null,
        [FromQuery] uint? leagueId = null,
        CancellationToken ct = default)
    {
        if (bowlerId == 0) return BadRequest("bowlerId must be greater than 0.");
        if (batterId == 0) return BadRequest("batterId must be greater than 0.");

        var result = await _service.GetH2HAsync(bowlerId, batterId, seasonId, leagueId, ct);
        return Ok(result);
    }

    // ── Pulse ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ball-by-ball LMS Pulse (win probability) for a single fixture.
    /// pulse_after_pct and pulse_change_pct are stored as Float32 in ball_events.
    /// Values are currently 0 everywhere until the win-predictor model is integrated.
    /// </summary>
    [HttpGet("pulse/{fixtureId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPulse(
        [FromRoute] uint fixtureId,
        CancellationToken ct = default)
    {
        if (fixtureId == 0) return BadRequest("fixtureId must be greater than 0.");

        var result = await _service.GetPulseAsync(fixtureId, ct);
        return Ok(result);
    }

    // ── Clips ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Highlight video clips for a single fixture.
    /// Optionally filter by clip_type: "six", "four", or "wicket".
    /// </summary>
    [HttpGet("clips/{fixtureId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetClips(
        [FromRoute] uint   fixtureId,
        [FromQuery] string? clipType = null,
        CancellationToken ct = default)
    {
        if (fixtureId == 0) return BadRequest("fixtureId must be greater than 0.");

        var allowed = new[] { "six", "four", "wicket" };
        if (!string.IsNullOrWhiteSpace(clipType) &&
            !allowed.Contains(clipType.Trim().ToLowerInvariant()))
        {
            return BadRequest($"clipType must be one of: {string.Join(", ", allowed)}.");
        }

        var result = await _service.GetClipsAsync(fixtureId, clipType, ct);
        return Ok(result);
    }
}
