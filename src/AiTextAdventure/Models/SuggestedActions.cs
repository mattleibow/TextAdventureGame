using System.ComponentModel;

namespace AiTextAdventure.Models;

[Description("A set of suggested player actions for a text adventure game.")]
public record SuggestedActions(
    [property: Description("3-4 diverse action suggestions. Include at least one movement option and one interaction option. Each must be distinct.")]
    List<SuggestedAction> Actions
);

[Description("A single suggested player action shown as a button.")]
public record SuggestedAction(
    [property: Description("Short button label shown to the player (2-3 words, e.g. 'Go North', 'Pick Up Sword', 'Examine Altar').")]
    string Label,
    [property: Description("ONE single player action (2-6 words). Must be exactly one action — never compound or combine actions with 'and', 'then', or commas. E.g. 'go north', 'pick up the iron sword', 'examine the stone altar', 'look around'.")]
    string ActionText
);

