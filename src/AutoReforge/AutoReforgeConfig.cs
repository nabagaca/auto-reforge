using TerrariaModder.Core.Config;

namespace AutoReforge
{
    public class AutoReforgeConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Reforge Interval"), Description("Number of frames between each reforge (12 ≈ 5/sec)"), Range(1, 60)]
        public int ReforgeInterval { get; set; } = 12;

        [Client, Label("Minimum Gold"), Description("Minimum amount of gold to keep (set to 0 for no limit)"), Range(0, 999)]
        public int MinGoldThreshold { get; set; } = 0;

        [Client, Label("Default Panel X"), Description("Default horizontal position for the UI panel")]
        public int PanelX { get; set; } = 300;

        [Client, Label("Default Panel Y"), Description("Default vertical position for the UI panel")]
        public int PanelY { get; set; } = 235;
    }
}
