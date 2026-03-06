namespace AiTextAdventure.Models;

public record InteractionResult(
    string Outcome,
    List<ItemChange>? ItemChanges,
    List<StateChange> StateChanges
);

public record ItemChange(string ItemName, string Description, int QuantityDelta);
public record StateChange(string Property, string NewValue);
