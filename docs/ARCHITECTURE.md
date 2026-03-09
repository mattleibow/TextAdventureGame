# Architecture — AI Text Adventure

Technical implementation reference for the AI Text Adventure game.

---

## Tech Stack

| Component | Technology |
|---|---|
| **UI Framework** | .NET MAUI (net10.0-maccatalyst, net10.0-ios, net10.0-android) |
| **Language** | C# 14, .NET 10, Nullable enabled |
| **AI Abstraction** | `Microsoft.Extensions.AI` (`IChatClient`) |
| **On-device AI** | Apple Intelligence via `AppleIntelligenceChatClient` |
| **Persistence** | `Shiny.SqliteDocumentDb` — schema-free JSON document store |
| **MVVM** | `CommunityToolkit.Mvvm` with source generators |
| **Logging** | `Microsoft.Extensions.Logging` |

---

## Project Structure

```
AiTextAdventure.slnx
├── src/AiTextAdventure/              # Main MAUI project
│   ├── Models/
│   │   ├── Documents/               # Persisted document types
│   │   │   ├── SaveSlot.cs          # Save game metadata
│   │   │   ├── WorldState.cs        # Current location + entity snapshot
│   │   │   ├── PlayerStats.cs       # HP, hunger, tiredness, armour, gear
│   │   │   ├── InventoryItem.cs     # Held item with effect string
│   │   │   ├── JournalEntry.cs      # Narrative log entry
│   │   │   └── MapTile.cs           # Persistent world tile (X,Y coords)
│   │   ├── ActionResult.cs          # ActionResolver output model
│   │   ├── SuggestedAction.cs       # Suggestion agent output
│   │   └── GameJsonContext.cs       # System.Text.Json source-gen context
│   ├── Services/
│   │   ├── GameOrchestrator.cs      # Turn flow: Narrator→ActionResolver→Suggestion
│   │   ├── MapService.cs            # Tile generation, movement, reveal
│   │   ├── WorldStateService.cs     # DB helpers for WorldState/Stats/Inventory/Journal
│   │   ├── SaveSlotService.cs       # Save slot CRUD + cascade delete
│   │   ├── AppleIntelligenceChatClient.cs  # IChatClient → Apple Intelligence
│   │   ├── FallbackChatClient.cs    # Deterministic fallback when AI unavailable
│   │   └── Observability/
│   │       └── EventStream.cs       # In-process agent event bus
│   ├── ViewModels/
│   │   ├── MainMenuViewModel.cs     # Save slot list + new/delete/load
│   │   ├── GameViewModel.cs         # Turn input, narrative display, suggestions
│   │   └── SidebarViewModel.cs     # 5-tab sidebar (Status/Pockets/Journal/Map/Events)
│   ├── Views/
│   │   ├── MainMenuPage.xaml        # Save slot list UI
│   │   ├── GamePage.xaml            # Split-panel game UI
│   │   └── MapDrawable.cs           # IDrawable for MAUI Graphics map
│   └── MauiProgram.cs               # DI registration, IChatClient wiring
└── tests/AiTextAdventure.Tests/     # xUnit test project
    ├── PersistenceTests.cs          # Document store round-trip + LINQ tests
    ├── SaveSlotDeletionTests.cs     # Cascade delete verification
    └── WorkflowRoutingTests.cs      # Agent routing / fallback tests
```

---

## Database / Persistence

### Shiny.SqliteDocumentDb

The app uses `Shiny.SqliteDocumentDb` as a schema-free JSON document store on top of SQLite. Each document type is stored in its own table; documents are serialised as JSON blobs using `System.Text.Json`.

**Registration** (`MauiProgram.cs`):
```csharp
services.AddSqliteDocumentStore(opts =>
    opts.ConnectionString = $"Data Source={dbPath}");
```

### Document Types

| Type | Key | Foreign Key | Description |
|---|---|---|---|
| `SaveSlot` | `slot.Id` | — | Game name, created/last-played timestamps |
| `WorldState` | `worldState.Id` | `SaveSlotId` | Current biome, location, player position (X,Y), entity snapshot |
| `PlayerStats` | `stats.Id` | `SaveSlotId` | HP, hunger, tiredness, armour, equipped slots |
| `InventoryItem` | `item.Id` | `SaveSlotId` | Item name, description, effect string, consumable/equippable flags |
| `JournalEntry` | `entry.Id` | `SaveSlotId` | Narrative text, timestamp, entry type |
| `MapTile` | `tile.Id` | `SaveSlotId` | X/Y coord, biome, description, features, hidden items, visited/revealed |

### Known Quirks

**Guid comparison in LINQ queries fails silently.**
`store.Query<T>(expr)` does not correctly translate Guid equality to SQL. Always use:
```csharp
var all = await store.GetAll<T>(JsonContext.Default.T, ct);
var filtered = all.Where(x => x.SaveSlotId == id).ToList();
```

**Upsert requires explicit string key.**
Use `store.Set(id.ToString(), doc, jsonTypeInfo, ct)` for predictable upsert behaviour. Calling `store.Set(doc)` generates a new auto-key every time and does not update existing records.

**System.Text.Json source generation required.**
`GameJsonContext` (annotated with `[JsonSerializable]` for every type) must be passed to all `GetAll<T>` and `Set` calls for AOT compatibility on iOS/Mac Catalyst.

### Cascade Delete

`SaveSlotService.DeleteSaveSlot()` removes the save slot and all related documents:
1. `SaveSlot` record
2. `WorldState` matching `SaveSlotId`
3. `PlayerStats` matching `SaveSlotId`
4. `InventoryItem` records matching `SaveSlotId`
5. `JournalEntry` records matching `SaveSlotId`
6. `MapTile` records matching `SaveSlotId`

---

## AI Integration

### IChatClient Abstraction

All AI calls go through `Microsoft.Extensions.AI.IChatClient`. This decouples the app from any specific model provider.

```csharp
var response = await chatClient.GetResponseAsync(messages, ct);
var text = response.Messages.LastOrDefault()?.Text?.Trim();
```

### AppleIntelligenceChatClient

Wraps Apple's on-device Foundation Models framework via `IMLLanguageModelSession`. Registered as the primary `IChatClient` when running on a supported device (iOS 18.4+ / macOS 15.4+ with Apple Intelligence enabled).

Context window: **~2,000 tokens**. All prompts are kept very short. Each prompt targets < 300 characters of user context.

Apple Intelligence has a content safety filter that may refuse some prompts. The orchestrator treats failures as non-fatal and falls back to a short error message.

### FallbackChatClient

When Apple Intelligence is unavailable (simulator, older OS, or non-Apple hardware), `FallbackChatClient` returns deterministic canned responses. It classifies the system prompt to return the right response type:

| Classified as | Detection | Response |
|---|---|---|
| `tilegen` | Prompt contains "hiddenitems" or "locationname" | Biome-appropriate tile JSON |
| `worldgen` | Prompt contains "currentbiome" | World state JSON |
| `resolver` | Prompt contains "itemspickedup" | Action result JSON |
| `suggestion` | Prompt contains "suggestion" | Suggestion JSON |
| `narrator` | Default | Atmospheric prose string |

---

## Game Orchestrator

`GameOrchestrator` runs every player turn. The flow is:

```
Player Input
    │
    ├─ Movement keyword detected? (MapService.DetectMovementDirection)
    │       └─ Yes → MapService.MovePlayer() → generates new tiles if needed
    │
    ├─ "Look around" keyword detected?
    │       └─ Yes → MapService.RevealTile() → moves hidden items to known list
    │
    ├─ [Step 1] Narrator LLM call
    │       Input: tile context (pre-generated, not invented)
    │       Output: 2–3 sentence atmospheric prose
    │
    ├─ [Step 2] ActionResolver LLM call (skipped for look-around)
    │       Input: portable items list + landmarks list + player action
    │       Output: JSON { ItemsPickedUp, ItemsDropped, EntitiesRemoved, NewEntities }
    │       → Apply changes to DB + WorldState
    │
    ├─ [Step 2b] Item use/equip (keyword detection, no LLM)
    │       "eat/drink/use/consume" → apply consumable effect + remove from inventory
    │       "equip/wield/wear" → set equipped slot in PlayerStats
    │
    ├─ [Step 2c] Stat decay
    │       Hunger += 2–4, Tiredness += 1–2 per turn
    │       Starvation/exhaustion → HP damage
    │
    └─ [Step 3] Suggestion LLM call
            Input: location, landmarks, portable items, exits
            Output: JSON { Actions: [{Label, ActionText}×3] }
```

### Context Building

The narrator receives pre-generated tile data rather than world state:
```
[FOREST — Whispering Glade] (0,0)
Ancient oaks interlock their branches overhead. Misty, birdsong, cool air.
Landmarks (inspect only, cannot pick up): mossy stone altar.
Portable items (can be picked up): luminescent healing berry, dried mushroom rations, carved bone hunting knife, bark-woven leather bracers, venomous forest asp.
```

This prevents the AI from inventing locations or items that don't exist in the world.

### Pickup Guards

Three layers prevent landmarks from entering inventory:
1. **List guard** — item must be in `WorldState.KnownEntities` (portable items only)
2. **Landmark list guard** — item must NOT be in `WorldState.LandmarkEntities`
3. **Word-boundary keyword guard** — `IsLandmarkByWords()` splits item name into tokens and checks against a `HashSet<string>` of landmark keywords (whole-word match only, avoiding false positives like "tower shield")

---

## Map System

### MapService

Responsible for all world map operations:

| Method | Description |
|---|---|
| `GetTile(saveSlotId, x, y)` | Fetch a tile from DB (null if not generated) |
| `GetAllTiles(saveSlotId)` | All tiles for map display |
| `GenerateSurroundingTiles(saveSlotId, x, y, gameName)` | Generate 3×3 grid, skip existing tiles |
| `MovePlayer(saveSlotId, worldState, direction, gameName)` | Move + generate new surroundings + sync WorldState |
| `RevealTile(saveSlotId, x, y, worldState)` | Mark as revealed, move hidden items to KnownEntities |
| `SyncTileToWorldState(worldState, tile)` | Copy tile data into WorldState snapshot |
| `BuildTileContext(tile, isRevealed)` | Compact context string for Narrator (<300 chars) |
| `DetectMovementDirection(input)` | Keyword detection: "go north" → "north" |

### Tile Generation

Tile generation uses a compact system prompt kept under ~200 characters to respect Apple Intelligence's context window. The prompt specifies:
- Output format (JSON only, no markdown)
- Biome compatibility rules
- Features = immovable landmarks, HiddenItems = portable pickups

Tiles are validated after generation and fall back to biome-appropriate defaults if the AI output is invalid or uses generic names like "item1".

### WorldState as Snapshot

`WorldState` is a cached snapshot of the current tile's data, stored in the DB. It is re-synced from the tile on every move or reveal. The **authoritative source** for world data is `MapTile`; `WorldState` is just a denormalised read cache for the orchestrator.

Fields:
- `KnownEntities` — portable items (from `MapTile.HiddenItems` after reveal)
- `LandmarkEntities` — inspect-only features (from `MapTile.Features`, always visible)
- `HiddenEntities` — not-yet-revealed items (from `MapTile.HiddenItems` before reveal)
- `PlayerX`, `PlayerY` — current tile coordinates

---

## Observability

### EventStream

An in-process publish/subscribe bus for AI agent events:

```csharp
eventStream.Emit(new AgentEvent("Narrating...", "Narrator", AgentEventKind.AgentInvoked));
```

`AgentEvent` has: `Message`, `AgentName`, `Kind` (Invoked/Output/Completed/Error/ToolResult/WorkflowComplete), optional `Detail`.

### Events Tab

The 🔮 **Events** tab in the sidebar shows a live scrolling log of all agent events, colour-coded by kind. This is useful for debugging AI behaviour during development.

---

## MVVM Architecture

### ViewModels

| ViewModel | Responsibility |
|---|---|
| `MainMenuViewModel` | Lists save slots, creates/deletes/loads games |
| `GameViewModel` | Handles player input, displays narrative, shows suggestions |
| `SidebarViewModel` | 5-tab sidebar; loads WorldState, PlayerStats, Inventory, Journal, MapTiles |
| `EventsPanelViewModel` | Subscribes to EventStream, maintains scrolling events list |

All ViewModels use `CommunityToolkit.Mvvm`:
- `[ObservableProperty]` for reactive properties
- `[RelayCommand]` for commands
- `ObservableCollection<T>` for list bindings

### Compiled Bindings

`GamePage.xaml` uses MAUI compiled bindings (`x:DataType`) for performance on iOS/Mac. Key rules:
- `x:DataType` on the page root applies to the whole tree
- Set `x:DataType` explicitly on nested elements (e.g. `DataTemplate`) when the type differs
- Deep binding paths like `{Binding EventsPanel.Events}` fail silently — expose them as single-level properties on the ViewModel

### CollectionView in ScrollView

`CollectionView` inside `ScrollView` collapses to height 0 by default. Fixes:
- Give `CollectionView` an explicit `HeightRequest`
- Or restructure using `Grid` with `RowDefinitions="Auto,Auto,*"` where CollectionView gets `*`

---

## Known Limitations

| Limitation | Impact | Workaround |
|---|---|---|
| Apple Intelligence context window (~2k tokens) | Long prompts are truncated silently | Keep all prompts < 300 chars; use compact JSON formats |
| Apple Intelligence content filter | May refuse action descriptions or narrative | Treat as non-fatal; return fallback message |
| Shiny Guid LINQ bug | `store.Query<T>(x => x.Id == guid)` returns nothing | Always use `GetAll<T>()` + in-memory LINQ |
| No multi-turn chat history | Each LLM call is stateless; no memory of previous turns | World state + tile context provide grounding |
| FallbackChatClient is deterministic | Offline play has repetitive responses | Vary by call index; biome-appropriate templates |
| MapTile generation is one AI call per 3×3 batch | Slow on first visit to a new area | Generation runs in background; subsequent moves are instant |
