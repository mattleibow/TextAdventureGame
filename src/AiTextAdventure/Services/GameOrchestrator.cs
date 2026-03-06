using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;
using AiTextAdventure.Workflows;

namespace AiTextAdventure.Services;

/// <summary>
/// Orchestrates a single player turn: creates a fresh workflow, feeds input,
/// streams events to the UI, and persists state changes.
/// See: https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff
/// </summary>
public class GameOrchestrator(
    GameWorkflowFactory workflowFactory,
    IWorldStateService worldStateService,
    IDocumentStore store,
    IEventStream eventStream) : IGameOrchestrator
{
    public async Task<GameTurnResult> ProcessTurnAsync(
        Guid saveSlotId,
        string playerInput,
        CancellationToken cancellationToken = default)
    {
        // 1. Create a fresh workflow for this turn (state isolation)
        var workflow = workflowFactory.CreateTurnWorkflow(saveSlotId);

        // 2. Build the input message with world context
        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        var contextMessage = $"[World: {worldState?.CurrentBiome ?? "unknown"} / {worldState?.CurrentLocation ?? "unknown"} / {worldState?.TimeOfDay ?? "morning"}]\n\nPlayer action: {playerInput}";

        var inputMessage = new ChatMessage(ChatRole.User, contextMessage);

        // 3. Execute the workflow with streaming
        // See: https://learn.microsoft.com/en-us/agent-framework/workflows/executors
        var sessionId = Guid.NewGuid().ToString();
        var run = await InProcessExecution.RunStreamingAsync(workflow, inputMessage, sessionId, cancellationToken);

        string narrativeText = "";
        SuggestedActions? suggestions = null;

        // 4. Process all workflow events
        // See: https://learn.microsoft.com/en-us/agent-framework/workflows/events
        await foreach (var evt in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case ExecutorInvokedEvent invoke:
                    eventStream.Emit(new AgentEvent("Invoked", invoke.ExecutorId, AgentEventKind.AgentInvoked));
                    break;

                case ExecutorCompletedEvent complete:
                    eventStream.Emit(new AgentEvent("Completed", complete.ExecutorId, AgentEventKind.AgentCompleted));
                    break;

                case AgentResponseEvent agentResponse:
                    var agentText = agentResponse.Response?.Text ?? "";
                    var preview = agentText.Length > 300 ? agentText[..300] + "..." : agentText;
                    eventStream.Emit(new AgentEvent("Response", agentResponse.ExecutorId, AgentEventKind.AgentOutput, preview));

                    // Capture the Narrator's output as the narrative text
                    if (agentResponse.ExecutorId == "Narrator" && !string.IsNullOrWhiteSpace(agentText))
                        narrativeText = agentText;

                    // Capture the Suggestion agent's output for suggested actions
                    if (agentResponse.ExecutorId == "Suggestion" && !string.IsNullOrWhiteSpace(agentText))
                    {
                        try
                        {
                            suggestions = JsonSerializer.Deserialize(agentText, GameJsonContext.Default.SuggestedActions);
                        }
                        catch
                        {
                            // If the Suggestion agent didn't return valid JSON, parse what we can
                            suggestions = new SuggestedActions([new SuggestedAction("Continue", agentText)]);
                        }
                    }
                    break;

                case AgentResponseUpdateEvent update:
                    eventStream.Emit(new AgentEvent("Streaming", update.ExecutorId, AgentEventKind.Streaming, update.Update?.Text));
                    break;

                case SuperStepStartedEvent:
                    eventStream.Emit(new AgentEvent("SuperStep", "workflow", AgentEventKind.SuperStepStarted));
                    break;

                case SuperStepCompletedEvent:
                    eventStream.Emit(new AgentEvent("SuperStep Done", "workflow", AgentEventKind.SuperStepCompleted));
                    break;

                case WorkflowOutputEvent output:
                    eventStream.Emit(new AgentEvent("Complete", "workflow", AgentEventKind.WorkflowComplete));
                    break;

                case WorkflowErrorEvent error:
                    eventStream.Emit(new AgentEvent("Error", "workflow", AgentEventKind.Error, error.Exception?.Message));
                    break;
            }
        }

        // 5. Persist the journal entry for this turn
        if (!string.IsNullOrWhiteSpace(narrativeText))
        {
            var entry = new JournalEntry
            {
                Id = Guid.NewGuid(),
                SaveSlotId = saveSlotId,
                EntryText = narrativeText,
                Timestamp = DateTime.UtcNow,
                Type = JournalEntryType.Narrative
            };
            await store.Set(entry.Id.ToString(), entry, GameJsonContext.Default.JournalEntry, cancellationToken);
        }

        // 6. Update the save slot's last played time
        var slots = await store.Query<SaveSlot>(
            s => s.Id == saveSlotId,
            GameJsonContext.Default.SaveSlot,
            cancellationToken);
        var slot = slots.FirstOrDefault();
        if (slot is not null)
        {
            slot.LastPlayedAt = DateTime.UtcNow;
            await store.Set(slot.Id.ToString(), slot, GameJsonContext.Default.SaveSlot, cancellationToken);
        }

        return new GameTurnResult(
            narrativeText.Length > 0 ? narrativeText : "The world holds its breath...",
            suggestions?.Actions ?? []);
    }
}
