using System.Text.Json.Serialization;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using GameLocation = AiTextAdventure.Models.Documents.Location;

namespace AiTextAdventure.Models;

[JsonSerializable(typeof(SaveSlot))]
[JsonSerializable(typeof(WorldState))]
[JsonSerializable(typeof(Npc))]
[JsonSerializable(typeof(DialogEntry))]
[JsonSerializable(typeof(InventoryItem))]
[JsonSerializable(typeof(JournalEntry))]
[JsonSerializable(typeof(GameLocation))]
[JsonSerializable(typeof(PlayerStats))]
[JsonSerializable(typeof(MapTile))]
[JsonSerializable(typeof(TileGenResponse))]
[JsonSerializable(typeof(BiomeKind))]
[JsonSerializable(typeof(PlayerAction))]
[JsonSerializable(typeof(WorldGenResult))]
[JsonSerializable(typeof(NpcDialogResult))]
[JsonSerializable(typeof(InteractionResult))]
[JsonSerializable(typeof(ItemChange))]
[JsonSerializable(typeof(StateChange))]
[JsonSerializable(typeof(NarrativeText))]
[JsonSerializable(typeof(ConsistencyVerdict))]
[JsonSerializable(typeof(SuggestedActions))]
[JsonSerializable(typeof(SuggestedAction))]
[JsonSerializable(typeof(List<SuggestedAction>))]
[JsonSerializable(typeof(List<SaveSlot>))]
[JsonSerializable(typeof(List<WorldState>))]
[JsonSerializable(typeof(List<Npc>))]
[JsonSerializable(typeof(List<InventoryItem>))]
[JsonSerializable(typeof(List<JournalEntry>))]
[JsonSerializable(typeof(List<GameLocation>))]
[JsonSerializable(typeof(List<PlayerStats>))]
[JsonSerializable(typeof(List<MapTile>))]
public partial class GameJsonContext : JsonSerializerContext;

