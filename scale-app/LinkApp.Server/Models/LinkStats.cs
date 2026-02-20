public record LinkStats(
    string ShortCode,
    long TotalClicks,
    long UniqueVisitors,
    DateTime? LastAccessed,
    List<DailyClickCount> ClickHistory
);

public record DailyClickCount(DateTime Date, long Count);