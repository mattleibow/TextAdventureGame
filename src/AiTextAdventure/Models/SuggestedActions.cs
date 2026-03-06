namespace AiTextAdventure.Models;

public record SuggestedActions(
    List<SuggestedAction> Actions
);

public record SuggestedAction(string Label, string ActionText);
