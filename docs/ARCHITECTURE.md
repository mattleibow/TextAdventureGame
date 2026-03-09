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
└── src/AiTextAdventure/              # Main MAUI project
    ├── Models/
    │   ├── Documents/               # Persisted document types
    │   │   ├── SaveSlot.cs          # Save game metadata
    │   │   ├── WorldState.cs        # Current location + entity snapshot
    │   │   ├── PlayerStats.cs       # HP, hunger, tiredness, armour, gear
    │   │   ├── InventoryItem.cs     # Held item with effect string
    │   │   ├── JournalEntry.cs      # Narrative log entry
    │   │   └── MapTile.cs           # Persistent world tile (X,Y coords)
    │   ├── SuggestedActions.cs      # SuggestedAction record (label + action text)
    │   ├── TileGenResponse.cs       # AI structured output DTO for tile generation
    │   └── GameJsonContext.cs       # System.Text.Json source-gen context
    ├── Services/
    │   ├── GameMaster.cs            # AI Game Master — drives every turn via tool calling
    │   ├── Tools/                   # One file per AI-callable tool
    │   │   ├── ToolContext.cs       # Shared context + helpers (ApplyEffect, RemoveFromTile, SaveRecentEvent)
    │   │   ├── ToolRegistry.cs      # Assembles all AITool instances for ChatOptions.Tools
    │   │   ├── GetWorldStateTool.cs # Returns location, items, stats, inventory
    │   │   ├── GetCurrentTileTool.cs# Returns tile description and atmosphere
    │   │   ├── MovePlayerTool.cs    # Moves player, generates new tiles
    │   │   ├── LookAroundTool.cs    # Reveals hidden items in current tile
    │   │   ├── PickUpItemTool.cs    # Adds item to inventory, removes from tile
    │   │   ├── DropItemTool.cs      # Removes from inventory, returns to tile
    │   │   ├── UseItemTool.cs       # Applies consumable effect, removes item
    │   │   └── EquipItemTool.cs     # Sets weapon/armor in PlayerStats
    │   ├── MapService.cs            # Tile generation (AI-powered), movement, reveal
    │   ├── WorldStateService.cs     # DB helpers for WorldState/Stats/Inventory/Journal
    │   ├── SaveSlotService.cs       # Save slot CRUD + cascade delete
    │   └── Observability/
    │       └── EventStream.cs       # In-process agent event bus
    ├── ViewModels/
    │   ├── MainMenuViewModel.cs     # Save slot list + new/delete/load
    │   ├── GameViewModel.cs         # Turn input, narrative display, suggestions
    │   └── SidebarViewModel.cs      # 5-tab sidebar (Status/Pockets/Journal/Map/Events)
    ├── Views/
    │   ├── MainMenuPage.xaml        # Save slot list UI
    │   ├── GamePage.xaml            # Split-panel game UI
    │   └── MapDrawable.cs           # IDrawable for MAUI Graphics map
    └── MauiProgram.cs               # DI registration, IChatClient wiring
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
var response = await chatClient.GetResponseAsync(messages, options, ct);
var text = response.Messages.LastOrDefault()?.Text?.Trim();
```

### AppleIntelligenceChatClient

`Microsoft.Maui.Essentials.AI.AppleIntelligenceChatClient` wraps Apple's on-device Foundation Models framework. Registered directly in `MauiProgram.cs` — the app **only supports Apple platforms**. On Android/Windows a `PlatformNotSupportedException` is thrown at startup.

```csharp
// MauiProgram.cs
#if IOS || MACCATALYST
IChatClient raw = new Microsoft.Maui.Essentials.AI.AppleIntelligenceChatClient();
return raw.AsBuilder()
    .UseLogging(loggerFactory)
    .UseFunctionInvocation()   // handles the tool-call loop automatically
    .Build();
#else
throw new PlatformNotSupportedException("Requires Apple Intelligence");
#endif
```

`UseFunctionInvocation()` is critical — it enables the tool-calling loop where the AI can call tools, receive results, and continue reasoning before producing its final response.

Context window: **~2,000 tokens**. All prompts are kept very short. Each prompt targets < 300 characters of user context.

Apple Intelligence has a content safety filter that may refuse some prompts. The Game Master treats failures as non-fatal and returns a short error message.

---

## Game Master — Tool-Calling Architecture

`GameMaster` runs every player turn using a single AI call with `ChatOptions.Tools`. The AI is given a set of tools and a system prompt that describes its role. It freely calls tools to read state, execute actions, and write back results — then narrates what happened.

### Turn Flow

```
Player Input ("pick up the sword")
    │
    ▼
GameMaster.ProcessTurnAsync
    │
    ├─ Create GameTools instance (scoped to this save slot)
    │
    ├─ Build messages:
    │       [System] GameMasterSystemPrompt
    │       [User]   player input text
    │
    ├─ chatClient.GetResponseAsync(messages, ChatOptions { Tools = toolRegistry.GetAllTools() })
    │       │
    │       │  ← UseFunctionInvocation() middleware manages this loop:
    │       ├─ AI calls get_world_state()
    │       │       └─ Returns location, items, stats, inventory
    │       ├─ AI calls get_current_tile()
    │       │       └─ Returns tile description, atmosphere
    │       ├─ AI calls pick_up_item("weathered iron sword", "...", "weapon:15")
    │       │       └─ Adds to InventoryItem in DB, removes from tile
    │       └─ AI produces final narrative (plain text)
    │
    ├─ Phase 2: GetResponseAsync<SuggestedActions>(suggMessages)
    │       └─ Structured output — no parsing, typed SuggestedActions returned
    │          SuggestedActions has [Description] attrs that guide the schema
    │          suggMessages = [System: SuggestionSystemPrompt] + [User: compact world context]
    │
    ├─ ApplyStatDecayAsync  (automatic per-turn mechanic — not an AI decision)
    │
    └─ Returns GameTurnResult { Narrative, Suggestions }
```

The AI is the **sole decision-maker**. It reads world state via tools, decides what actions make sense, executes them, and narrates the result. No C# code makes game decisions.

### Game Tools

`GameTools` provides 8 AI-callable functions. Each is created with `AIFunctionFactory.Create()` and has a `[Description]` attribute to guide the AI's understanding.

| Tool | Description |
|---|---|
| `get_world_state` | Returns location, visible items, inventory, stats, recent events |
| `get_current_tile` | Returns tile description, biome, atmosphere |
| `move_player(direction)` | Moves player in a compass direction, generates new tiles if needed |
| `look_around` | Reveals hidden items in the current tile |
| `pick_up_item(name, description, effect)` | Adds item to inventory, removes from tile. AI must set `effect` |
| `drop_item(name)` | Removes from inventory, returns to tile |
| `use_item(name)` | Applies consumable effect (heal/food), removes from inventory |
| `equip_item(name)` | Equips weapon or armor, updates PlayerStats |

The `effect` parameter in `pick_up_item` is critical — the AI sets it when picking up:
- `heal:N` — restores N HP when used
- `food:N` — reduces hunger by N when used
- `weapon:N` — equippable, N attack power
- `armor:N` — equippable, N damage reduction
- `poison:N` — venomous: damages on contact, item is not stored

The `[Description]` on each tool method and parameter controls what the AI understands about when and how to use each tool. These are read by `AIFunctionFactory.Create()` via reflection and embedded in the function schema sent to the model.

### System Prompts

**Phase 1 (tool calling)** — `GameMasterSystemPrompt`:
```
You are the Game Master of a text adventure. You have tools to read and change the game world.
When the player gives you an action: call get_world_state first, then execute the intent,
then write a vivid 2-4 sentence narrative in second-person present tense.
```

**Phase 2 (structured suggestions)** — `SuggestionSystemPrompt`:
```
You are a game assistant. Suggest exactly 3 distinct player actions.
Include at least one movement direction and one interaction.
```

### Response Format

Phase 1 returns plain text narrative. Phase 2 uses `GetResponseAsync<SuggestedActions>()` — no parsing, no string splitting. The `SuggestedActions` type has `[Description]` attributes that guide what the AI puts in each field:

```csharp
[Description("A set of suggested player actions for a text adventure game.")]
public record SuggestedActions(
    [property: Description("3-4 diverse action suggestions.")] List<SuggestedAction> Actions
);

[Description("A single suggested player action shown as a button.")]
public record SuggestedAction(
    [property: Description("Short button label (2-3 words, e.g. 'Go North').")] string Label,
    [property: Description("Full natural-language action text the player would type.")] string ActionText
);
```

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
| `BuildTileContext(tile, isRevealed)` | Compact context string for AI (<300 chars) |

### Tile Generation

Tile generation uses `GetResponseAsync<TileGenResponse>()` with structured output. `TileGenResponse` uses `[Description]` attributes to guide the AI. The system prompt specifies biome compatibility rules, and biomes use a `BiomeKind` enum with `[JsonStringEnumMemberName]` for correct serialisation.

Tiles fall back to biome-appropriate defaults if generation fails.

### WorldState as Snapshot

`WorldState` is a cached snapshot of the current tile's data, stored in the DB. It is re-synced from the tile on every move or reveal. The **authoritative source** for world data is `MapTile`; `WorldState` is a denormalised read cache.

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
eventStream.Emit(new AgentEvent("look_around", "GameMaster", AgentEventKind.ToolResult, detail));
```

`AgentEvent` has: `Title`, `AgentName`, `Kind`, optional `Detail` (short summary), optional `FullContent` (full prompt/response shown when expanded).

Key event kinds emitted per turn:

| Kind | When emitted |
|---|---|
| `AgentInvoked` | Before the AI call |
| `Prompt` | System+user prompt text (📋 icon, blue) |
| `Response` | Full AI response text (💬 icon, green) |
| `ToolResult` | Tool invocation results (DB operations, tool outputs) |
| `Error` | Any failure |
| `WorkflowComplete` | End of turn |

### Events Tab

The 🔮 **Events** tab shows a live scrolling log of the AI's tool calls and reasoning. Each event is **collapsible**:
- **Collapsed**: one-line summary (icon + agent name + title + timestamp)
- **Expanded**: full content (prompt text, response, tool arguments) — tap to toggle

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
| Apple Intelligence context window (~2k tokens) | Long prompts are truncated silently | Keep all prompts short; tool results are compact strings |
| Apple Intelligence content filter | May refuse action descriptions or narrative | Treat as non-fatal; return fallback message |
| Shiny Guid LINQ bug | `store.Query<T>(x => x.Id == guid)` returns nothing | Always use `GetAll<T>()` + in-memory LINQ |
| No multi-turn chat history | Each AI call is stateless; no memory of prior turns | WorldState + tile context provide grounding via `get_world_state` tool |
| MapTile generation is sequential | Slow on first visit to a new area | Generation runs in background; subsequent moves are instant |

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
│   ├── Agents/
│   │   ├── AgentFactory.cs          # Creates agent instances
│   │   ├── Tools/                   # Agent tool functions
│   │   └── Workflows/
│   │       └── GameWorkflowFactory.cs   # Handoff workflow (retained for future multi-agent use)
│   ├── Services/
│   │   ├── GameOrchestrator.cs      # Turn flow: Narrator→ActionResolver→Suggestion
│   │   ├── MapService.cs            # Tile generation (AI-powered), movement, reveal
│   │   ├── WorldStateService.cs     # DB helpers for WorldState/Stats/Inventory/Journal
│   │   ├── SaveSlotService.cs       # Save slot CRUD + cascade delete
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

`Microsoft.Maui.Essentials.AI.AppleIntelligenceChatClient` wraps Apple's on-device Foundation Models framework. Registered directly in `MauiProgram.cs` — the app **only supports Apple platforms**. On Android/Windows a `PlatformNotSupportedException` is thrown at startup.

```csharp
// MauiProgram.cs
#if IOS || MACCATALYST
IChatClient raw = new Microsoft.Maui.Essentials.AI.AppleIntelligenceChatClient();
return raw.AsBuilder().UseLogging(loggerFactory).UseFunctionInvocation().Build();
#else
throw new PlatformNotSupportedException("Requires Apple Intelligence");
#endif
```

Context window: **~2,000 tokens**. All prompts are kept very short. Each prompt targets < 300 characters of user context.

Apple Intelligence has a content safety filter that may refuse some prompts. The orchestrator treats failures as non-fatal and returns a short error message.

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

`AgentEvent` has: `Title`, `AgentName`, `Kind`, optional `Detail` (short summary), optional `FullContent` (full prompt/response text shown when expanded).

Key event kinds emitted per turn:

| Kind | When emitted |
|---|---|
| `AgentInvoked` | Before each AI call |
| `Prompt` | System+user prompt text (📋 icon, blue) |
| `Response` | Full LLM response text (💬 icon, green) |
| `AgentOutput` | After AI call completes |
| `ToolCall` / `ToolResult` | Tool invocations and DB operations |
| `Error` | Any failure |
| `WorkflowComplete` | End of turn |

### Events Tab

The 🔮 **Events** tab shows a live scrolling log. Each event is **collapsible**:
- **Collapsed**: one-line summary (icon + agent name + title + timestamp)
- **Expanded**: full content (prompt text, response JSON, tool arguments) — tap to toggle
- Events with `FullContent` show a ▶/▼ expand indicator

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
| MapTile generation is one AI call per 3×3 batch | Slow on first visit to a new area | Generation runs in background; subsequent moves are instant |
