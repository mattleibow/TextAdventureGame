using AiTextAdventure.ViewModels;

namespace AiTextAdventure.Views;

/// <summary>
/// Selects the correct DataTemplate for the Events CollectionView based on whether
/// the item is a turn group header or a regular agent event. This avoids the MAUI
/// CollectionView crash that occurs when multiple sibling elements in one DataTemplate
/// toggle IsVisible on/off (the layout measure pass throws a managed exception).
/// </summary>
public class EventsDataTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TurnHeaderTemplate { get; set; }
    public DataTemplate? RegularEventTemplate { get; set; }

    protected override DataTemplate? OnSelectTemplate(object item, BindableObject container) =>
        item is AgentEventViewModel { IsTurnHeader: true }
            ? TurnHeaderTemplate
            : RegularEventTemplate;
}
