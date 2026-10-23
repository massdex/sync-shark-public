using System.Globalization;

namespace Massdex.SyncShark.V2;

internal sealed record DateRepairBound(DateTime Local, bool WholeDay)
{
    public static implicit operator DateRepairBound(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), true);

    internal static DateRepairBound? Parse(string? text)
    {
        if (text is null) return null;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day;
        if (DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return new(DateTime.SpecifyKind(time, DateTimeKind.Unspecified), false);
        throw new SyncCliUsageException("Use a local date yyyy-MM-dd or local time yyyy-MM-ddTHH:mm.");
    }

    public override string ToString() => Local.ToString(WholeDay ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
}
