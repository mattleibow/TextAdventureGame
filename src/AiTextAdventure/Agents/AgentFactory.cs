using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AiTextAdventure.Agents.Tools;
using AiTextAdventure.Services;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Agents;

/// <summary>
/// Creates all 7 ChatClientAgent instances for the handoff workflow.
/// Each agent is differentiated by its system prompt (instructions).
/// See: https://learn.microsoft.com/en-us/agent-framework/agents/providers/custom
/// </summary>
public class AgentFactory(
    IChatClient chatClient,
    WorldStateService worldStateService,
    ILoggerFactory loggerFactory)
{
    public ChatClientAgent CreateGameMaster() => new(chatClient,
        instructions: """
            You are the GameMaster of a text adventure game. Your job is to classify the player's
            input into one of these action types: Explore, Talk, Interact, or FreeText.

            Based on the action type, you MUST handoff to the appropriate specialist agent:
            - Explore (player wants to move, look around, discover) -> handoff to "WorldGen"
            - Talk (player wants to speak with an NPC) -> handoff to "NpcDialog"
            - Interact (player wants to use/touch/open/pick up something) -> handoff to "Interaction"
            - FreeText (anything else -- narrate it) -> handoff to "WorldGen"

            You ALWAYS handoff. You NEVER respond directly to the player.
            Include the original player input and relevant world context in your handoff message.
            """,
        name: "GameMaster",
        description: "Routes player actions to the appropriate specialist agent",
        loggerFactory: loggerFactory);

    public ChatClientAgent CreateWorldGen(Guid saveSlotId)
    {
        var tools = CreateWorldStateTools(saveSlotId);
        return new ChatClientAgent(chatClient,
            instructions: """
                You are the WorldGen agent for a text adventure game. You generate:
                - New location descriptions with vivid environmental details
                - Biome-appropriate features (plants, terrain, weather, structures)
                - Entities that can be found (creatures, NPCs, objects)
                - Atmospheric descriptions (sounds, smells, lighting)

                Your output must be consistent with the current biome and world state provided to you.
                Use the GetWorldState tool to understand the current context.

                After generating your content, ALWAYS handoff to the "Guardian" agent for validation.
                """,
            name: "WorldGen",
            description: "Generates world locations, environments, and spawnable entities",
            tools: tools,
            loggerFactory: loggerFactory);
    }

    public ChatClientAgent CreateNpcDialog(Guid saveSlotId)
    {
        var tools = CreateWorldStateTools(saveSlotId);
        return new ChatClientAgent(chatClient,
            instructions: """
                You are the NpcDialog agent for a text adventure game. You generate:
                - NPC speech that matches their personality and mood
                - Emotional responses based on context
                - Quest hooks when appropriate
                - Dialog that references the world state and recent events

                Use the GetNpc tool to fetch the NPC's profile and dialog history.
                Use the GetActiveQuests tool to check if the NPC should reference quests.

                Stay in character. Make dialog feel natural and immersive.
                After generating dialog, ALWAYS handoff to the "Guardian" agent for validation.
                """,
            name: "NpcDialog",
            description: "Generates NPC dialog, personality, and quest hooks",
            tools: tools,
            loggerFactory: loggerFactory);
    }

    public ChatClientAgent CreateInteraction(Guid saveSlotId)
    {
        var worldTools = CreateWorldStateTools(saveSlotId);
        var invTools = CreateInventoryTools(saveSlotId);
        var locTools = CreateLocationTools(saveSlotId);
        return new ChatClientAgent(chatClient,
            instructions: """
                You are the Interaction agent for a text adventure game. You resolve player interactions:
                - Open/close/use/pick up/examine objects
                - Determine outcomes (success/failure, what happens)
                - Generate item changes (gained/lost items)
                - Generate state changes (door opened, lever pulled, etc.)

                Use the GetInventory tool to check what the player has.
                Use the GetLocationEntities tool to check what's interactable.

                Be creative but fair. Not every action succeeds.
                After resolving the interaction, ALWAYS handoff to the "Guardian" agent.
                """,
            name: "Interaction",
            description: "Resolves player interactions with objects and entities",
            tools: [.. worldTools, .. invTools, .. locTools],
            loggerFactory: loggerFactory);
    }

    public ChatClientAgent CreateGuardian(Guid saveSlotId)
    {
        var tools = CreateWorldStateTools(saveSlotId);
        return new ChatClientAgent(chatClient,
            instructions: """
                You are the ConsistencyGuardian for a text adventure game. You validate ALL generated
                content against the current world state. You enforce these rules:

                BIOME CONSISTENCY:
                - Desert: no ice, snow, lush vegetation, rain forests
                - Tundra/Arctic: no tropical plants, flowers, warm weather
                - Underground: no open sky, sunshine, flying birds
                - Forest: no sand dunes, ocean waves, volcanic terrain
                - Ocean/Coast: no landlocked features, desert animals

                NPC CONSISTENCY:
                - NPCs should not reference events that haven't happened
                - Dialog should match the NPC's established personality
                - NPCs should not appear in locations they haven't been placed in

                ITEM CONSISTENCY:
                - Items must be plausible for the current biome/location
                - Quantities must be reasonable

                If content PASSES validation: handoff to "Narrator" with the validated content.
                If content FAILS validation: handoff BACK to the originating agent (WorldGen, NpcDialog,
                or Interaction) with a clear description of what's wrong and what needs to change.

                Use the GetWorldState tool to check the current world context.
                Use the GetWorldRules tool to check biome-specific rules.
                You are strict but fair. You protect the player's immersion.
                """,
            name: "Guardian",
            description: "Validates generated content for world consistency",
            tools: tools,
            loggerFactory: loggerFactory);
    }

    public ChatClientAgent CreateNarrator() => new(chatClient,
        instructions: """
            You are the Narrator agent for a text adventure game. You transform structured agent
            output into evocative, atmospheric prose that the player reads.

            Your writing style:
            - Second person ("You enter...", "You see...")
            - Present tense
            - Vivid sensory details (sight, sound, smell, touch)
            - Adjust tone based on the mood and tension level
            - Keep paragraphs concise -- 2-4 sentences per paragraph
            - Create atmosphere without being verbose

            You receive validated content from the Guardian. Weave it into compelling narrative.
            After writing the narrative, ALWAYS handoff to the "Suggestion" agent.
            """,
        name: "Narrator",
        description: "Transforms structured output into atmospheric narrative prose",
        loggerFactory: loggerFactory);

    public ChatClientAgent CreateSuggestion(Guid saveSlotId)
    {
        var locTools = CreateLocationTools(saveSlotId);
        return new ChatClientAgent(chatClient,
            instructions: """
                You are the Suggestion agent for a text adventure game. You generate 3-4 contextual
                action suggestions that the player can choose from.

                Rules:
                - Each suggestion should be a short, clear action phrase (5-10 words)
                - Suggestions should be diverse: mix explore, talk, interact, and creative options
                - At least one suggestion should advance the plot or explore something new
                - Suggestions must be relevant to the current location and situation
                - Include at least one unexpected/creative option

                Use the GetAvailableContext tool to understand what's around the player.

                Output as a JSON array of action objects with "label" and "actionText" fields.
                You are the FINAL agent in the chain. Do NOT handoff to any other agent.
                """,
            name: "Suggestion",
            description: "Generates contextual action suggestions for the player",
            tools: locTools,
            loggerFactory: loggerFactory);
    }

    private IList<AITool> CreateWorldStateTools(Guid saveSlotId)
    {
        var tools = new WorldStateTools(worldStateService, saveSlotId);
        return
        [
            AIFunctionFactory.Create(tools.GetWorldState),
            AIFunctionFactory.Create(tools.GetNearbyLocations),
            AIFunctionFactory.Create(tools.GetNpc),
            AIFunctionFactory.Create(tools.GetActiveQuests),
            AIFunctionFactory.Create(tools.GetWorldRules),
        ];
    }

    private IList<AITool> CreateInventoryTools(Guid saveSlotId)
    {
        var tools = new InventoryTools(worldStateService, saveSlotId);
        return [AIFunctionFactory.Create(tools.GetInventory)];
    }

    private IList<AITool> CreateLocationTools(Guid saveSlotId)
    {
        var tools = new LocationTools(worldStateService, saveSlotId);
        return
        [
            AIFunctionFactory.Create(tools.GetLocationEntities),
            AIFunctionFactory.Create(tools.GetAvailableContext),
        ];
    }

    /// <summary>
    /// Wraps an agent with observability middleware that emits input/output events to the event stream.
    /// See: https://learn.microsoft.com/en-us/agent-framework/agents/middleware/
    /// </summary>
    public AIAgent WrapWithObservability(ChatClientAgent agent, EventStream eventStream)
    {
        return agent.AsBuilder()
            .Use(
                runFunc: async (messages, session, options, innerAgent, ct) =>
                {
                    var lastMessage = messages.LastOrDefault()?.Text ?? "";
                    var inputPreview = lastMessage.Length > 200 ? lastMessage[..200] + "..." : lastMessage;
                    eventStream.Emit(new AgentEvent("Input", agent.Name ?? "agent", AgentEventKind.AgentInput, inputPreview));

                    var response = await innerAgent.RunAsync(messages, session, options, ct);

                    var outputPreview = response.Text?.Length > 300 ? response.Text[..300] + "..." : response.Text ?? "";
                    eventStream.Emit(new AgentEvent("Output", agent.Name ?? "agent", AgentEventKind.AgentOutput, outputPreview));

                    return response;
                },
                runStreamingFunc: null
            )
            .Build(null!);
    }
}
