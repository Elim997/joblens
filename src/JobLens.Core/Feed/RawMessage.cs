namespace JobLens.Core.Feed;

public record RawMessage(
    string Id,
    string ChatJid,
    string Sender,
    string Content,
    DateTimeOffset Timestamp);
