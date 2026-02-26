namespace LinkApp.Server.Events;

public record LinkVisitedEvent
(
    string ShortCode, 
    string? IpAddress, 
    string? UserAgent, 
    DateTime ClickedAt

);
    


