using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;
using Lang = Terraria.Lang;

namespace AutoReforge
{
    public enum StopReason { None, Success, OutOfMoney, BelowThreshold }

    /// <summary>
    /// The Auto-Reforge panel: shown whenever the vanilla Goblin Tinkerer reforge menu
    /// is open. Lets the player pick a target modifier and auto-reforge until they get it.
    /// </summary>
    public class ReforgeUI
    {
        // ── Layout constants ──────────────────────────────────────────────────────
        private const int PanelWidth  = 240;
        private const int PanelHeight = 560;
        private const int RowHeight   = 22;
        private const int RowSpacing  = 2;
        private const int FooterH     = 130; // status + sliders + buttons

        // ── State ─────────────────────────────────────────────────────────────────
        private readonly DraggablePanel _panel;
        private readonly ScrollView     _scroll    = new ScrollView();
        private readonly Slider         _speedSlider     = new Slider();
        private readonly Slider         _thresholdSlider = new Slider();
        private ILogger? _log;

        // Prefix list — rebuilt whenever the item in the reforge slot changes
        private List<PrefixInfo>? _prefixes;
        private int _lastItemType = -1;

        // User's selection
        private int _selectedPrefixId = -1;

        // Settings (persisted within the session)
        private int _reforgeInterval = 12;  // frames between each reforge (12 ≈ 5/sec)
        private int _minMoneyGold    = 0;   // gold coins to keep; 0 = no minimum

        // Auto-reforge state
        private bool       _autoRunning;
        private int        _reforgeTimer;
        private int        _attempts;
        private long       _totalSpent;
        private StopReason _lastStopReason = StopReason.None;

        // Edge-detection for InReforgeMenu
        private bool _wasInReforgeMenu;

        // ── Constructor ───────────────────────────────────────────────────────────

        public ReforgeUI()
        {
            _panel = new DraggablePanel("auto-reforge", "Auto-Reforge", PanelWidth, PanelHeight)
            {
                ClipContent    = false,
                ShowCloseButton = true,
                CloseOnEscape  = false,
            };
            _panel.OnClose = StopAuto;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        public void Initialize(ILogger log)
        {
            _log = log;
            _panel.RegisterDrawCallback(OnDraw);
        }

        public void Unload()
        {
            _panel.UnregisterDrawCallback();
            StopAuto();
        }

        // ── Toggle (keybind) ──────────────────────────────────────────────────────

        public void Toggle()
        {
            if (_panel.IsOpen) _panel.Close();
            else Open();
        }

        private void Open()
        {
            // Position to the right of the vanilla reforge UI (slot is at ~50,270)
            _panel.Open(300, 235);
            RebuildPrefixList();
        }

        // ── Per-frame update (called from FrameEvents.OnPostUpdate) ───────────────

        public void OnUpdate()
        {
            bool inReforgeMenu = Main.InReforgeMenu;

            // Auto-open on rising edge
            if (inReforgeMenu && !_wasInReforgeMenu)
                Open();

            // Auto-close on falling edge
            if (!inReforgeMenu && _wasInReforgeMenu)
            {
                StopAuto();
                _panel.Close();
            }

            _wasInReforgeMenu = inReforgeMenu;

            if (!inReforgeMenu) return;

            // Rebuild prefix list when item in slot changes
            var slot = Main.reforgeItem;
            if (slot != null && slot.type != _lastItemType)
            {
                _lastItemType   = slot.type;
                _selectedPrefixId = -1;
                StopAuto();
                RebuildPrefixList();
            }

            PrefixData.Tick();

            if (!_autoRunning) return;

            // ── Frame rate limiter ────────────────────────────────────────────────
            _reforgeTimer++;
            if (_reforgeTimer < _reforgeInterval) return;
            _reforgeTimer = 0;

            // ── Guards ────────────────────────────────────────────────────────────
            if (slot == null || slot.type <= 0)   { StopAuto(); return; }
            if (slot.prefix == _selectedPrefixId) { FinishAuto(StopReason.Success); return; }
            if (!slot.CanRollPrefix(_selectedPrefixId))
            {
                _log?.Warn("[AutoReforge] Target prefix no longer valid for this item.");
                StopAuto();
                return;
            }

            // ── Money threshold check ─────────────────────────────────────────────
            long cost  = CalculateReforgeCost(slot);
            long total = GetPlayerCoins();
            long kept  = (long)_minMoneyGold * 10_000; // gold → copper

            if (total - cost < kept)
            {
                FinishAuto(StopReason.BelowThreshold);
                return;
            }

            // ── Pay ───────────────────────────────────────────────────────────────
            var player = Main.player[Main.myPlayer];
            if (!player.BuyItem(cost)) { FinishAuto(StopReason.OutOfMoney); return; }

            // ── Reforge ───────────────────────────────────────────────────────────
            slot.ResetPrefix();
            slot.Prefix(-2, out _);

            _attempts++;
            _totalSpent += cost;

            // Vanilla feedback: floating text above the player + reforge sound
            PopupText.NewText(PopupTextContext.ItemReforge, slot, Main.LocalPlayer.Center, 1, noStack: true);
            SoundEngine.PlaySound(SoundID.Item37);

            // ── Check for success ─────────────────────────────────────────────────
            if (slot.prefix == _selectedPrefixId)
                FinishAuto(StopReason.Success);
        }

        // ── Draw callback ─────────────────────────────────────────────────────────

        private void OnDraw()
        {
            if (!_panel.BeginDraw()) return;
            try { DrawContent(); }
            finally { _panel.EndDraw(); }
        }

        private void DrawContent()
        {
            int px = _panel.ContentX;
            int py = _panel.ContentY;
            int pw = _panel.ContentWidth;

            // ── Info lines ────────────────────────────────────────────────────────
            var slot    = Main.reforgeItem;
            bool hasItem = slot != null && slot.type > 0;

            UIRenderer.DrawText(
                TextUtil.Truncate(hasItem ? $"Item: {slot!.Name}" : "Put an item in the reforge slot", pw),
                px, py + 2, UIColors.TextHint);

            if (hasItem && slot!.prefix > 0)
                UIRenderer.DrawText(
                    TextUtil.Truncate($"Current: {Lang.prefix[slot.prefix]?.Value ?? "None"}", pw),
                    px, py + 18, UIColors.TextDim);

            // ── Prefix list ───────────────────────────────────────────────────────
            int listY = py + 38;
            int listH = PanelHeight - _panel.HeaderHeight - 38 - FooterH - _panel.Padding;
            DrawPrefixList(px, listY, pw, listH, hasItem);

            // ── Footer ────────────────────────────────────────────────────────────
            DrawFooter(px, listY + listH + 6, pw);
        }

        private void DrawPrefixList(int x, int y, int width, int height, bool hasItem)
        {
            UIRenderer.DrawRect(x, y, width, height, new Color4(25, 25, 40, 200));

            if (!hasItem || _prefixes == null || _prefixes.Count == 0)
            {
                string msg = hasItem ? "Item cannot be reforged" : "No item in slot";
                int tw = TextUtil.MeasureWidth(msg);
                UIRenderer.DrawText(msg, x + (width - tw) / 2, y + height / 2 - 7, UIColors.TextHint);
                UIRenderer.DrawRectOutline(x, y, width, height, new Color4(70, 70, 100), 1);
                return;
            }

            int contentH = ComputeContentHeight();
            _scroll.Begin(x, y, width, height, contentH);

            int itemY    = 0;
            int rowIndex = 0;
            PrefixTier? lastTier = null;

            foreach (var info in _prefixes)
            {
                // Tier header
                if (info.Tier != lastTier)
                {
                    if (_scroll.IsVisible(itemY, 18))
                    {
                        int ry = _scroll.ContentY + itemY;
                        UIRenderer.DrawRect(x, ry, width, 18, new Color4(45, 45, 65, 220));
                        UIRenderer.DrawText(PrefixData.GetTierLabel(info.Tier),
                            x + 4, ry + 2, PrefixData.GetTierColor(info.Tier, rowIndex));
                    }
                    itemY  += 19;
                    lastTier = info.Tier;
                }

                // Prefix row
                if (_scroll.IsVisible(itemY, RowHeight))
                {
                    int ry       = _scroll.ContentY + itemY;
                    bool selected  = info.Id == _selectedPrefixId;
                    bool hovered   = WidgetInput.IsMouseOver(x, ry, _scroll.ContentWidth, RowHeight);
                    bool isCurrent = Main.reforgeItem?.prefix == info.Id;

                    Color4 bg = selected
                        ? new Color4(80, 70, 120, 230)
                        : hovered
                            ? new Color4(55, 55, 80, 210)
                            : PrefixData.GetTierBgColor(info.Tier);
                    UIRenderer.DrawRect(x, ry, _scroll.ContentWidth, RowHeight, bg);

                    if (isCurrent)
                        UIRenderer.DrawRect(x, ry, 3, RowHeight, new Color4(255, 220, 50));

                    string label = isCurrent ? $"> {info.Name}" : $"  {info.Name}";
                    UIRenderer.DrawText(
                        TextUtil.Truncate(label, _scroll.ContentWidth - 20),
                        x + 4, ry + (RowHeight - 14) / 2,
                        PrefixData.GetTierColor(info.Tier, rowIndex));

                    if (selected)
                        UIRenderer.DrawText("✓",
                            x + _scroll.ContentWidth - 14, ry + (RowHeight - 14) / 2,
                            new Color4(200, 200, 80));

                    if (hovered && WidgetInput.MouseLeftClick && !WidgetInput.BlockInput)
                    {
                        WidgetInput.ConsumeClick();
                        _selectedPrefixId = selected ? -1 : info.Id;
                        if (_autoRunning && _selectedPrefixId == -1) StopAuto();
                    }
                }

                itemY += RowHeight + RowSpacing;
                rowIndex++;
            }

            _scroll.End();
            UIRenderer.DrawRectOutline(x, y, width, height, new Color4(70, 70, 100), 1);
        }

        private void DrawFooter(int x, int y, int width)
        {
            // ── Speed slider ──────────────────────────────────────────────────────
            // Range: 4–60 frames. Lower = faster. We label the right-hand value.
            float reforgesPerSec = 60f / _reforgeInterval;
            string speedLabel = $"Speed: {reforgesPerSec:F1}/s";
            UIRenderer.DrawText(speedLabel, x, y, UIColors.TextDim);
            _reforgeInterval = _speedSlider.Draw(x, y + 14, width, 12, _reforgeInterval, 4, 60);

            // ── Threshold slider ──────────────────────────────────────────────────
            // Range: 0–100 gold. Show "No min" when 0.
            string threshLabel = _minMoneyGold == 0
                ? "Stop if low: No minimum"
                : $"Stop if low: keep {_minMoneyGold}g";
            UIRenderer.DrawText(threshLabel, x, y + 30, UIColors.TextDim);
            _minMoneyGold = _thresholdSlider.Draw(x, y + 44, width, 12, _minMoneyGold, 0, 100);

            // ── Status line ───────────────────────────────────────────────────────
            Color4 statusColor;
            string statusText;
            if (_autoRunning)
            {
                statusText  = $"Running… {_attempts} attempt{(_attempts == 1 ? "" : "s")}  {FormatCoins(_totalSpent)}";
                statusColor = UIColors.Warning;
            }
            else
            {
                switch (_lastStopReason)
                {
                    case StopReason.Success:
                        statusText  = $"Got it!  {_attempts} attempt{(_attempts == 1 ? "" : "s")}  ({FormatCoins(_totalSpent)})";
                        statusColor = UIColors.Success;
                        break;
                    case StopReason.OutOfMoney:
                        statusText  = $"Stopped: out of money  ({_attempts} attempts)";
                        statusColor = UIColors.Error;
                        break;
                    case StopReason.BelowThreshold:
                        statusText  = $"Stopped: below {_minMoneyGold}g threshold  ({_attempts} attempts)";
                        statusColor = UIColors.Warning;
                        break;
                    default:
                        bool hasSelection = _selectedPrefixId > 0;
                        statusText  = hasSelection
                            ? $"Target: {Lang.prefix[_selectedPrefixId]?.Value ?? $"#{_selectedPrefixId}"}"
                            : "Select a modifier above";
                        statusColor = hasSelection ? UIColors.Info : UIColors.TextHint;
                        break;
                }
            }
            UIRenderer.DrawText(TextUtil.Truncate(statusText, width), x, y + 60, statusColor);

            // ── Buttons ───────────────────────────────────────────────────────────
            int btnY  = y + 78;
            int halfW = (width - 4) / 2;

            if (_autoRunning)
            {
                if (Button.Draw(x, btnY, halfW, 26, "STOP",
                        new Color4(100, 40, 40), new Color4(140, 50, 50), UIColors.Text))
                    StopAuto();
            }
            else
            {
                bool canStart = Main.reforgeItem?.type > 0 && _selectedPrefixId > 0;
                Color4 bg    = canStart ? new Color4(40, 80, 40)  : new Color4(45, 45, 55);
                Color4 hover = canStart ? new Color4(60, 120, 60) : new Color4(45, 45, 55);
                if (Button.Draw(x, btnY, halfW, 26, "AUTO-REFORGE", bg, hover, UIColors.Text) && canStart)
                    StartAuto();
            }

            if (Button.Draw(x + halfW + 4, btnY, halfW, 26, "Reset Stats"))
            {
                _attempts       = 0;
                _totalSpent     = 0;
                _lastStopReason = StopReason.None;
            }
        }

        // ── Auto-reforge state helpers ────────────────────────────────────────────

        private void StartAuto()
        {
            if (_selectedPrefixId <= 0) return;
            _attempts       = 0;
            _totalSpent     = 0;
            _reforgeTimer   = _reforgeInterval; // fire on the very next frame
            _lastStopReason = StopReason.None;
            _autoRunning    = true;
        }

        private void StopAuto()
        {
            _autoRunning = false;
        }

        private void FinishAuto(StopReason reason)
        {
            _autoRunning    = false;
            _lastStopReason = reason;
            if (reason == StopReason.Success)
                SoundEngine.PlaySound(SoundID.BestReforge); // celebratory sound on success
        }

        // ── Prefix list helpers ───────────────────────────────────────────────────

        private void RebuildPrefixList()
        {
            _prefixes = PrefixData.BuildForCurrentItem();
            _scroll.ResetScroll();
        }

        private int ComputeContentHeight()
        {
            if (_prefixes == null) return 0;
            int h = 0;
            PrefixTier? lastTier = null;
            foreach (var info in _prefixes)
            {
                if (info.Tier != lastTier) { h += 19; lastTier = info.Tier; }
                h += RowHeight + RowSpacing;
            }
            return h;
        }

        // ── Cost calculation (mirrors vanilla DrawInterface) ──────────────────────

        private static long CalculateReforgeCost(Item item)
        {
            var player = Main.player[Main.myPlayer];
            long cost = (long)item.value * item.stack;
            if (player.discountAvailable) cost = (long)(cost * 0.8);
            cost = (long)(cost * player.currentShoppingSettings.PriceAdjustment);
            cost /= 3;
            return Math.Max(1, cost);
        }

        // ── Player coin count (all main inventory + coin slots; not banks) ────────

        private static long GetPlayerCoins()
        {
            var player = Main.player[Main.myPlayer];
            long total = 0;
            for (int i = 0; i < 58; i++) // 0-53 main inventory, 54-57 coin slots
            {
                var item = player.inventory[i];
                if (item == null || item.stack <= 0) continue;
                switch (item.type)
                {
                    case ItemID.CopperCoin:    total += item.stack; break;
                    case ItemID.SilverCoin:    total += (long)item.stack * 100; break;
                    case ItemID.GoldCoin:      total += (long)item.stack * 10_000; break;
                    case ItemID.PlatinumCoin:  total += (long)item.stack * 1_000_000; break;
                }
            }
            return total;
        }

        // ── Coin display helper ───────────────────────────────────────────────────

        private static string FormatCoins(long copper)
        {
            if (copper <= 0) return "0c";
            long plat   = copper / 1_000_000; copper -= plat   * 1_000_000;
            long gold   = copper / 10_000;    copper -= gold   * 10_000;
            long silver = copper / 100;       copper -= silver * 100;

            var sb = new System.Text.StringBuilder();
            if (plat   > 0) sb.Append($"{plat}p ");
            if (gold   > 0) sb.Append($"{gold}g ");
            if (silver > 0) sb.Append($"{silver}s ");
            if (copper > 0 || sb.Length == 0) sb.Append($"{copper}c");
            return sb.ToString().TrimEnd();
        }
    }
}
