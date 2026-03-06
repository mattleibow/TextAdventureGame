namespace AiTextAdventure.Models;

public record WorldGenResult(
    string Biome,
    string LocationName,
    string Description,
    string Atmosphere,
    List<string> Features,
    List<string> Entities
);
