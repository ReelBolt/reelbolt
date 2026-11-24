namespace ReelBolt.Inference.Api.Services.SocialPublishing;

public sealed record SocialScheduleResult(DateTime? Utc, string? TimeZone, string? ErrorCode, string? ErrorMessage)
{
    public bool Ok => ErrorCode is null;

    public static SocialScheduleResult Success(DateTime utc, string? timeZone) => new(utc, timeZone, null, null);

    public static SocialScheduleResult Fail(string code, string message) => new(null, null, code, message);
}

/// <summary>
/// Turns what the person picked into the UTC instant the sweeper fires at, and keeps their IANA zone for display.
/// Daylight saving is handled explicitly: a wall-clock time that does not exist (the spring-forward gap) is refused
/// with a clear message, and one that happens twice (the autumn repeat) means the FIRST occurrence.
/// </summary>
public static class SocialScheduleTime
{
    public static readonly TimeSpan PastGrace = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(366);

    public static SocialScheduleResult Resolve(bool publishNow, DateTimeOffset? scheduledAt, DateTime? scheduledLocal, string? timeZone, DateTime nowUtc)
    {
        int choices = (publishNow ? 1 : 0) + (scheduledAt is null ? 0 : 1) + (scheduledLocal is null ? 0 : 1);
        if (choices != 1)
            return SocialScheduleResult.Fail("schedule_required", "Choose either post now, or one date and time.");

        TimeZoneInfo? zone = null;
        string? zoneId = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();
        if (zoneId is not null)
        {
            if (zoneId.Length > 64 || !TryFindZone(zoneId, out zone))
                return SocialScheduleResult.Fail("invalid_time_zone", "That time zone is not known. Use an IANA name such as Europe/Rome.");
        }

        if (publishNow) return SocialScheduleResult.Success(nowUtc, zoneId);

        DateTime utc;
        if (scheduledAt is { } instant)
        {
            utc = instant.UtcDateTime;
        }
        else
        {
            if (zone is null)
                return SocialScheduleResult.Fail("time_zone_required", "A local date and time needs the time zone it is in.");
            DateTime local = DateTime.SpecifyKind(scheduledLocal!.Value, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
                return SocialScheduleResult.Fail("nonexistent_local_time", "That time does not exist on that day in that time zone (the clocks skip it). Pick another time.");
            TimeSpan offset = zone.IsAmbiguousTime(local)
                ? zone.GetAmbiguousTimeOffsets(local).Max() // the first of the two passes has the larger (summer) offset
                : zone.GetUtcOffset(local);
            utc = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        if (utc < nowUtc - PastGrace) return SocialScheduleResult.Fail("in_the_past", "That time has already passed.");
        if (utc > nowUtc + MaxAhead) return SocialScheduleResult.Fail("too_far_ahead", "Posts can be scheduled up to a year ahead.");
        return SocialScheduleResult.Success(utc, zoneId);
    }

    /// <summary>The wall-clock time of a UTC instant in the post's zone, for display. Null when there is no zone.</summary>
    public static string? ToLocalText(DateTime? utc, string? timeZone)
    {
        if (utc is null || string.IsNullOrWhiteSpace(timeZone) || !TryFindZone(timeZone, out TimeZoneInfo? zone)) return null;
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), zone!).ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryFindZone(string id, out TimeZoneInfo? zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = null;
            return false;
        }
    }
}
