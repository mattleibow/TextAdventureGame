using AiTextAdventure.Models.Documents;
using Microsoft.Maui.Graphics;

namespace AiTextAdventure.Views;

/// <summary>
/// MAUI Graphics drawable for the world map.
/// Renders a grid of colored tiles centered on the player's position.
/// Each biome has a distinct color; visited tiles are bright, unvisited are dim.
/// The player's position is marked with a bright cross/dot.
/// </summary>
public class MapDrawable : IDrawable
{
    public List<MapTile> Tiles { get; set; } = [];
    public int PlayerX { get; set; } = 0;
    public int PlayerY { get; set; } = 0;

    private const int TileSize = 18;    // px per tile
    private const int TileGap = 1;      // gap between tiles
    private const int TileStep = TileSize + TileGap;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.FillColor = Color.FromArgb("#1a1a2e");
        canvas.FillRectangle(dirtyRect);

        if (dirtyRect.Width < 10 || dirtyRect.Height < 10) return;

        // Centre the view on the player
        var centerPx = dirtyRect.Width / 2f;
        var centerPy = dirtyRect.Height / 2f;

        // Draw each known tile
        var tileDict = Tiles.ToDictionary(t => (t.X, t.Y));

        // Determine visible range
        var halfW = (int)(centerPx / TileStep) + 2;
        var halfH = (int)(centerPy / TileStep) + 2;

        for (var dy = -halfH; dy <= halfH; dy++)
        {
            for (var dx = -halfW; dx <= halfW; dx++)
            {
                var tx = PlayerX + dx;
                var ty = PlayerY + dy;
                var px = centerPx + dx * TileStep - TileSize / 2f;
                var py = centerPy + dy * TileStep - TileSize / 2f;

                if (tileDict.TryGetValue((tx, ty), out var tile))
                {
                    // Visited tile — full biome colour
                    var fillColor = tile.IsVisited
                        ? BiomeColor(tile.Biome)
                        : DimColor(BiomeColor(tile.Biome));
                    canvas.FillColor = fillColor;
                    canvas.FillRoundedRectangle(px, py, TileSize, TileSize, 2);

                    // Outline for revealed tiles
                    if (tile.IsRevealed)
                    {
                        canvas.StrokeColor = Colors.White.WithAlpha(0.4f);
                        canvas.StrokeSize = 0.5f;
                        canvas.DrawRoundedRectangle(px, py, TileSize, TileSize, 2);
                    }
                }
                else
                {
                    // Unknown tile — dark placeholder dot
                    canvas.FillColor = Color.FromArgb("#2a2a3e");
                    canvas.FillRoundedRectangle(px, py, TileSize, TileSize, 2);
                }
            }
        }

        // Draw player marker (bright cross)
        var markerX = centerPx;
        var markerY = centerPy;
        var half = TileSize / 2f;

        canvas.StrokeColor = Colors.White;
        canvas.StrokeSize = 2f;
        canvas.DrawLine(markerX - half, markerY, markerX + half, markerY);
        canvas.DrawLine(markerX, markerY - half, markerX, markerY + half);

        // Draw coordinate overlay
        canvas.FontColor = Colors.White.WithAlpha(0.5f);
        canvas.FontSize = 8;
        canvas.DrawString($"({PlayerX},{PlayerY})", markerX + half + 2, markerY - 5, HorizontalAlignment.Left);
    }

    private static Color BiomeColor(string biome) => biome.ToLowerInvariant() switch
    {
        "forest" => Color.FromArgb("#2d6a4f"),
        "plains" => Color.FromArgb("#8cb369"),
        "hills" => Color.FromArgb("#6b8f71"),
        "mountain" => Color.FromArgb("#6c757d"),
        "desert" => Color.FromArgb("#c9a227"),
        "swamp" => Color.FromArgb("#4a7c59"),
        "cave" => Color.FromArgb("#5c4033"),
        "ocean" => Color.FromArgb("#1565c0"),
        "tundra" => Color.FromArgb("#90a4ae"),
        "ruins" => Color.FromArgb("#7b6d5a"),
        _ => Color.FromArgb("#4a4a6a")
    };

    private static Color DimColor(Color c) =>
        Color.FromRgba(c.Red * 0.35f, c.Green * 0.35f, c.Blue * 0.35f, 0.6f);
}
