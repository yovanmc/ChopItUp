namespace ChopItUp.Core.Model;

public sealed record RoomModeSettings(string Mode = "relay", string First = "gpt-6-astra", string? Second = "opus", long Revision = 0)
{
    public int Turns => Mode switch { "primary" => 1, "relay" => 2, "panel" => 3, _ => throw new ArgumentException("Unknown room mode.") };
    public IReadOnlyList<string> Participants => Mode == "primary" || Second is null ? [First] : [First, Second];

    public void Validate(IReadOnlyList<Participant> roster)
    {
        _ = Turns;
        if (string.IsNullOrWhiteSpace(First) || (Mode != "primary" && string.IsNullOrWhiteSpace(Second)))
            throw new ArgumentException("This mode needs " + (Mode == "primary" ? "one participant." : "two participants."));
        if (Second == First) throw new ArgumentException("Choose distinct participants.");
        foreach (var id in new[] { First, Second }.Where(id => id is not null))
            if (!roster.Any(p => p.Id == id && p.Kind == "model" && p.Model is not null && p.Host is "claude" or "codex"))
                throw new ArgumentException($"@{id} is not a configured spawnable participant.");
    }

    public static RoomModeSettings Parse(string body, RoomModeSettings previous, IReadOnlyList<Participant> roster)
    {
        var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4 || parts[0] != "/mode") throw new ArgumentException("Use /mode primary|relay|panel [@first [@second]].");
        if (parts.Skip(2).Any(p => !p.StartsWith('@') || p.Length < 2)) throw new ArgumentException("Participants must be leading @ids without trailing prose.");
        var settings = new RoomModeSettings(parts[1], parts.Length > 2 ? parts[2][1..] : previous.First,
            parts.Length > 3 ? parts[3][1..] : parts.Length == 3 ? null : previous.Second, previous.Revision);
        settings.Validate(roster);
        return settings;
    }
}
