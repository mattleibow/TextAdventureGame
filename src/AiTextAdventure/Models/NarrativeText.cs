namespace AiTextAdventure.Models;

public record NarrativeText(
    string Text,
    string Mood,
    float Tension  // 0.0 = calm, 1.0 = intense
);
