using TerrariaModder.Core;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Logging;

namespace AutoReforge
{
    public class Mod : IMod
    {
        public string Id      => "auto-reforge";
        public string Name    => "Auto-Reforge";
        public string Version => "0.1.0";

        private ILogger?  _log;
        private ReforgeUI _ui = new ReforgeUI();

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _log.Info("[AutoReforge] Initializing");

            _ui.Initialize(_log);

            // Open/close the panel with a keybind (F7 by default)
            context.RegisterKeybind("toggle", "Toggle Auto-Reforge Panel",
                "Show/hide the modifier selector while at the Goblin Tinkerer",
                "F7",
                () => _ui.Toggle());

            FrameEvents.OnPostUpdate += OnPostUpdate;

            _log.Info("[AutoReforge] Ready");
        }

        public void OnWorldLoad()   { }
        public void OnWorldUnload() { }

        public void Unload()
        {
            FrameEvents.OnPostUpdate -= OnPostUpdate;
            _ui.Unload();
        }

        private void OnPostUpdate()
        {
            _ui.OnUpdate();
        }
    }
}
