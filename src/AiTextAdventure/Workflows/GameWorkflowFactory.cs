using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using AiTextAdventure.Agents;

namespace AiTextAdventure.Workflows;

/// <summary>
/// Creates a fresh handoff workflow for each player turn.
/// A new workflow instance is created per turn for state isolation.
/// See: https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff
/// </summary>
public class GameWorkflowFactory(AgentFactory agentFactory)
{
    /// <summary>
    /// Creates a fresh handoff workflow for a single player turn.
    /// The workflow routes: GameMaster -> [WorldGen|NpcDialog|Interaction] -> Guardian -> Narrator -> Suggestion
    /// Guardian can reject content back to the originating specialist for revision.
    /// </summary>
    public Workflow CreateTurnWorkflow(Guid saveSlotId)
    {
        // Create fresh agent instances per turn for state isolation
        var gameMaster = agentFactory.CreateGameMaster();
        var worldGen = agentFactory.CreateWorldGen(saveSlotId);
        var npcDialog = agentFactory.CreateNpcDialog(saveSlotId);
        var interaction = agentFactory.CreateInteraction(saveSlotId);
        var guardian = agentFactory.CreateGuardian(saveSlotId);
        var narrator = agentFactory.CreateNarrator();
        var suggestion = agentFactory.CreateSuggestion(saveSlotId);

        // Build the handoff workflow using the AgentWorkflowBuilder API
        // See: https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff
        var workflow = AgentWorkflowBuilder
            .CreateHandoffBuilderWith(gameMaster)
            // GameMaster routes to the appropriate specialist
            .WithHandoffs(gameMaster, [worldGen, npcDialog, interaction])
            // All specialists hand off to Guardian for consistency validation
            .WithHandoff(worldGen, guardian)
            .WithHandoff(npcDialog, guardian)
            .WithHandoff(interaction, guardian)
            // Guardian passes to Narrator on success, or rejects back to specialist
            .WithHandoffs(guardian, [narrator, worldGen, npcDialog, interaction])
            // Narrator hands off to Suggestion (final step before returning to user)
            .WithHandoff(narrator, suggestion)
            // Suggestion is terminal -- no outgoing handoffs
            .Build();

        return workflow;
    }
}
