namespace AiTextAdventure.Models;

public record NpcDialogResult(
    string NpcName,
    string Dialog,
    string Emotion,
    string? QuestHook
);
