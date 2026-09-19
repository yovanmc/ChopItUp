namespace ChopItUp.Core.Model;

public sealed record GoverningCommand(string Slot, string Text)
{
    public const int MaxChars = 6_000;

    public static bool TryParse(string body, out GoverningCommand command)
    {
        foreach (var slot in new[] { "objective", "correction" })
        {
            var prefix = "/" + slot;
            if (!body.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (body.Length > prefix.Length && body[prefix.Length] is not (' ' or '\t' or '\r' or '\n')) continue;
            command = new GoverningCommand(slot, body[prefix.Length..].Trim());
            return true;
        }
        command = new GoverningCommand("", "");
        return false;
    }
}

public sealed record GoverningEntry(Message Source, string Text);
public sealed record GoverningContext(GoverningEntry? Objective, GoverningEntry? Correction);
public sealed record SpawnContext(IReadOnlyList<Message> Transcript, long RetrievalOmitted, GoverningContext Governing);
