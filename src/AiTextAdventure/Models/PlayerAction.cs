namespace AiTextAdventure.Models;

public enum PlayerActionType { Explore, Talk, Interact, FreeText }

public record PlayerAction(
    PlayerActionType Type,
    string Target,
    string Context
);
