using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;  // WidgetInput, TextUtil
using FlexUI;               // UIRect, Color4
using Color4 = FlexUI.Color4;
using UIStackLayout = FlexUI.Layout.StackLayout;
using FlexUI.Panels;        // UIPanel
using FlexUI.Scroll;        // ScrollRegion
using UIButton     = FlexUI.Widgets.Button;
using SliderState  = FlexUI.Widgets.SliderState;
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
        private const int BasePanelWidth  = 280;
        private const int BasePanelHeight = 560;
        private const int RowSpacing  = 2;
        private static int PanelWidth => Math.Max(BasePanelWidth, 240 + FlexText.MeasureWidth("Stop if low: keep 100g"));
        private static int InfoHeight => FlexText.LineHeight * 2 + 8;
        private static int TierHeaderHeight => Math.Max(18, FlexText.LineHeight + 2);
        private static int PrefixRowHeight => Math.Max(24, FlexText.LineHeight + 6);
        private static int SliderHeight => Math.Max(12, (int)Math.Ceiling(FlexText.LineHeight * 0.65f));
        private static int ButtonHeight => Math.Max(28, FlexText.LineHeight + 10);

        private static int FooterH
        {
            get
            {
                int labelH = FlexText.LineHeight + 4;
                return 3 * (labelH + 2) + 2 * (SliderHeight + 2) + 3 * 4 + ButtonHeight + 4;
            }
        }

        private static int PanelHeight => 160 + FooterH + Math.Max(220, 12 * (PrefixRowHeight + RowSpacing));

        // ── State ─────────────────────────────────────────────────────────────────
        private readonly UIPanel      _panel;
        private readonly ScrollRegion _scroll          = new ScrollRegion();
        private readonly SliderState  _speedSt         = new SliderState();
        private readonly SliderState  _thresholdSt     = new SliderState();
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
            _panel = new UIPanel("auto-reforge", "Auto-Reforge", PanelWidth, PanelHeight)
            {
                ShowCloseButton = true,
                CloseOnEscape   = false,
                AutoResizeToContent = true,
                Resizable       = true,
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
            _panel.Close();
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
            _panel.SetContentSize(PanelWidth - _panel.Padding * 2,
                PanelHeight - _panel.HeaderHeight - _panel.Padding);
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
                _lastItemType     = slot.type;
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
            // Close the panel if the game has returned to the main menu
            if (Main.gameMenu)
            {
                if (_panel.IsOpen) { StopAuto(); _panel.Close(); }
                return;
            }
            if (!_panel.Begin()) return;
            try { DrawContent(); }
            finally { _panel.End(); }
        }

        private void DrawContent()
        {
            _panel.MinWidth = PanelWidth;
            _panel.MinHeight = PanelHeight;
            _panel.SetContentSize(PanelWidth - _panel.Padding * 2,
                PanelHeight - _panel.HeaderHeight - _panel.Padding);

            var content = _panel.ContentRect;

            // Divide content: info strip at top, footer at bottom, list fills middle
            var (footerRect, upper) = content.SliceBottom(FooterH);
            var (infoRect,  listRect) = upper.SliceTop(InfoHeight);

            DrawInfoArea(infoRect);
            DrawPrefixList(listRect);
            DrawFooter(footerRect);
        }

        // ── Info area (item name + current modifier) ──────────────────────────────

        private static void DrawInfoArea(UIRect rect)
        {
            var slot    = Main.reforgeItem;
            bool hasItem = slot != null && slot.type > 0;

            string itemText = hasItem ? $"Item: {slot!.Name}" : "Put an item in the reforge slot";
            FlexText.Draw(
                FlexText.Truncate(itemText, rect.W),
                rect.X, rect.Y + 2, UIColors.TextHint);

            if (hasItem && slot!.prefix > 0)
                FlexText.Draw(
                    FlexText.Truncate($"Current: {Lang.prefix[slot.prefix]?.Value ?? "None"}", rect.W),
                    rect.X, rect.Y + 4 + FlexText.LineHeight, UIColors.TextDim);
        }

        // ── Prefix list ───────────────────────────────────────────────────────────

        private void DrawPrefixList(UIRect listRect)
        {
            UIRenderer.DrawRect(listRect.X, listRect.Y, listRect.W, listRect.H, new Color4(25, 25, 40, 200));

            var slot    = Main.reforgeItem;
            bool hasItem = slot != null && slot.type > 0;

            if (!hasItem || _prefixes == null || _prefixes.Count == 0)
            {
                string msg = hasItem ? "Item cannot be reforged" : "No item in slot";
                int tw = FlexText.MeasureWidth(msg);
                FlexText.Draw(msg, listRect.X + (listRect.W - tw) / 2, listRect.Y + (listRect.H - FlexText.LineHeight) / 2, UIColors.TextHint);
                UIRenderer.DrawRectOutline(listRect.X, listRect.Y, listRect.W, listRect.H, new Color4(70, 70, 100), 1);
                return;
            }

            int contentH = ComputeContentHeight();
            _scroll.Begin(listRect, contentH);

            int itemY    = 0;
            int rowIndex = 0;
            PrefixTier? lastTier = null;

            foreach (var info in _prefixes)
            {
                // Tier header
                if (info.Tier != lastTier)
                {
                    if (_scroll.IsVisible(itemY, TierHeaderHeight))
                    {
                        int ry    = _scroll.ContentToScreen(itemY);
                        int textY = ry + Math.Max(0, (TierHeaderHeight - FlexText.LineHeight) / 2);
                        UIRenderer.DrawRect(listRect.X, ry, listRect.W, TierHeaderHeight, new Color4(45, 45, 65, 220));
                        FlexText.Draw(PrefixData.GetTierLabel(info.Tier),
                            listRect.X + 4, textY, PrefixData.GetTierColor(info.Tier, rowIndex));
                    }
                    itemY   += TierHeaderHeight + 1;
                    lastTier = info.Tier;
                }

                // Prefix row
                if (_scroll.IsVisible(itemY, PrefixRowHeight))
                {
                    int  ry       = _scroll.ContentToScreen(itemY);
                    int  cw       = _scroll.ContentWidth;
                    bool selected  = info.Id == _selectedPrefixId;
                    bool hovered   = WidgetInput.IsMouseOver(listRect.X, ry, cw, PrefixRowHeight);
                    bool isCurrent = Main.reforgeItem?.prefix == info.Id;

                    Color4 bg = selected
                        ? new Color4(80, 70, 120, 230)
                        : hovered
                            ? new Color4(55, 55, 80, 210)
                            : PrefixData.GetTierBgColor(info.Tier);

                    // Scissor clip (set by ScrollRegion.Begin) handles boundary clipping.
                    UIRenderer.DrawRect(listRect.X, ry, cw, PrefixRowHeight, bg);

                    if (isCurrent)
                        UIRenderer.DrawRect(listRect.X, ry, 3, PrefixRowHeight, new Color4(255, 220, 50));

                    int textY = ry + Math.Max(0, (PrefixRowHeight - FlexText.LineHeight) / 2);
                    string label = isCurrent ? $"> {info.Name}" : $"  {info.Name}";
                    FlexText.Draw(
                        FlexText.Truncate(label, cw - 20),
                        listRect.X + 4, textY,
                        PrefixData.GetTierColor(info.Tier, rowIndex));

                    if (selected)
                        FlexText.Draw("✓",
                            listRect.X + cw - 14, textY,
                            new Color4(200, 200, 80));

                    if (hovered && WidgetInput.MouseLeftClick)
                    {
                        WidgetInput.ConsumeClick();
                        _selectedPrefixId = selected ? -1 : info.Id;
                        if (_autoRunning && _selectedPrefixId == -1) StopAuto();
                    }
                }

                itemY += PrefixRowHeight + RowSpacing;
                rowIndex++;
            }

            _scroll.End();
            UIRenderer.DrawRectOutline(listRect.X, listRect.Y, listRect.W, listRect.H, new Color4(70, 70, 100), 1);
        }

        // ── Footer (sliders + status + buttons) ───────────────────────────────────

        private void DrawFooter(UIRect footerRect)
        {
            var stack = new UIStackLayout(footerRect, gap: 2);

            // Speed slider
            float reforgesPerSec = 60f / _reforgeInterval;
            stack.Label($"Speed: {reforgesPerSec:F1}/s", UIColors.TextDim);
            _reforgeInterval = stack.Slider(_speedSt, _reforgeInterval, 4, 60, height: SliderHeight);
            stack.Space(4);

            // Money threshold slider
            string threshLabel = _minMoneyGold == 0
                ? "Stop if low: No minimum"
                : $"Stop if low: keep {_minMoneyGold}g";
            stack.Label(threshLabel, UIColors.TextDim);
            _minMoneyGold = stack.Slider(_thresholdSt, _minMoneyGold, 0, 100, height: SliderHeight);
            stack.Space(4);

            // Status line
            var (statusText, statusColor) = GetStatus();
            stack.Label(FlexText.Truncate(statusText, footerRect.W), statusColor);
            stack.Space(4);

            // Buttons — two halves of the next row
            var btnRect = stack.Next(ButtonHeight);
            int halfW   = (btnRect.W - 4) / 2;
            var leftBtn  = new UIRect(btnRect.X,             btnRect.Y, halfW, ButtonHeight);
            var rightBtn = new UIRect(btnRect.X + halfW + 4, btnRect.Y, halfW, ButtonHeight);

            if (_autoRunning)
            {
                if (UIButton.Draw(leftBtn, "Stop",
                        new Color4(100, 40, 40), new Color4(140, 50, 50), UIColors.Text))
                    StopAuto();
            }
            else
            {
                bool canStart = Main.reforgeItem?.type > 0 && _selectedPrefixId > 0;
                Color4 bg    = canStart ? new Color4(40, 80, 40)  : new Color4(45, 45, 55);
                Color4 hover = canStart ? new Color4(60, 120, 60) : new Color4(45, 45, 55);
                if (UIButton.Draw(leftBtn, "Auto Reforge", bg, hover, UIColors.Text) && canStart)
                    StartAuto();
            }

            if (UIButton.Draw(rightBtn, "Reset Stats"))
            {
                _attempts       = 0;
                _totalSpent     = 0;
                _lastStopReason = StopReason.None;
            }
        }

        private (string text, Color4 color) GetStatus()
        {
            if (_autoRunning)
                return ($"Running… {_attempts} attempt{(_attempts == 1 ? "" : "s")}  {FormatCoins(_totalSpent)}", UIColors.Warning);

            switch (_lastStopReason)
            {
                case StopReason.Success:
                    return ($"Got it!  {_attempts} attempt{(_attempts == 1 ? "" : "s")}  ({FormatCoins(_totalSpent)})", UIColors.Success);
                case StopReason.OutOfMoney:
                    return ($"Stopped: out of money  ({_attempts} attempts)", UIColors.Error);
                case StopReason.BelowThreshold:
                    return ($"Stopped: below {_minMoneyGold}g threshold  ({_attempts} attempts)", UIColors.Warning);
                default:
                    bool hasSelection = _selectedPrefixId > 0;
                    return hasSelection
                        ? ($"Target: {Lang.prefix[_selectedPrefixId]?.Value ?? $"#{_selectedPrefixId}"}", UIColors.Info)
                        : ("Select a modifier above", UIColors.TextHint);
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
                SoundEngine.PlaySound(SoundID.BestReforge);
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
                if (info.Tier != lastTier) { h += TierHeaderHeight + 1; lastTier = info.Tier; }
                h += PrefixRowHeight + RowSpacing;
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
                    case ItemID.CopperCoin:   total += item.stack; break;
                    case ItemID.SilverCoin:   total += (long)item.stack * 100; break;
                    case ItemID.GoldCoin:     total += (long)item.stack * 10_000; break;
                    case ItemID.PlatinumCoin: total += (long)item.stack * 1_000_000; break;
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
