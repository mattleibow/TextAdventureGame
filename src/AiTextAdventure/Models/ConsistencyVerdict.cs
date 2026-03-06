namespace AiTextAdventure.Models;

public record ConsistencyVerdict(
    bool Pass,
    List<string> Violations,
    string? Suggestion
);
