using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Game.Runtime.Data;
using Game.Runtime.Manager;
using Game.Runtime.UI;
using Game.UI.Utility;

namespace Sandbox
{
    /// <summary>
    /// Sandbox shop and mod slots (each postfix / prefix only acts while <see cref="Plugin.Active"/>, or for the slot
    /// count <see cref="Plugin.InSandboxRun"/>; every target has its own code, RVA unique in dump.cs):
    ///   - CardShopInfo.get_CardPrice (0x9EC8F0) -> 0: CanPurchaseCard, TrySelectCard and every price label read it.
    ///   - CardContainerSO.GetRerollPrice (0x9E1220) -> 0: the only code caller is CardShopManager's reroll price
    ///     (0x8CE540, the body get_CurrentRerollPrice / CalculateRerollPrice share, which is not patched).
    ///   - ACardSO.get_SellingPrice (0x9BF610, virtual, no overrides) -> 0: free cards never sell for cash.
    ///   - CardExtensions.IsCardAvailable (0x9EAB00) prefix: ignoreLockedStatus = true, so locked mods are offered. No
    ///     unlock is written (CardEquipmentManager.UnlockCard is blocked by the guards anyway).
    ///   - CardEquipmentManager.get_CurrentModSlotCount (0x8B9100) + ExtraSlots: every slot check reads it.
    ///   - CardShop_AllCardsSelector.Awake (0x804D10) postfix: the game's hidden all-cards picker (debugRoot) is shown,
    ///     its buy-all button hidden; picks go through CardShopManager.ForceSelectCard (free).
    ///   - CardDisplayGroup.UpdateCards (0x7F8060) postfix: the Current Mods grid (FlexibleGridLayout, fixed rows) gets a
    ///     second row while more than 10 mods are shown, and its own row count back otherwise.
    /// </summary>
    internal static class Shop
    {
        private static readonly List<CardShop_AllCardsSelector> Pickers = new List<CardShop_AllCardsSelector>();
        // Saved originals, keyed by the object's pointer. Each entry keeps the object's wrapper (its GC handle stops the
        // pointer being reused while the entry exists) and is pruned once Unity has destroyed the object (scene change).
        private static readonly Dictionary<IntPtr, (FlexibleGridLayout grid, int rows)> OriginalRows = new Dictionary<IntPtr, (FlexibleGridLayout, int)>();
        private static readonly Dictionary<IntPtr, (CardShop_AllCardsSelector picker, bool active)> OriginalBuyAll = new Dictionary<IntPtr, (CardShop_AllCardsSelector, bool)>();
        private static readonly List<IntPtr> Dead = new List<IntPtr>();
        private static bool _loggedPrice, _loggedSlots;

        internal static int Install(Harmony h)
        {
            int n = 0;
            // harmony-target: CardShopInfo.get_CardPrice
            n += P(h, typeof(CardShopInfo), "get_CardPrice", null, nameof(FreePrice));
            // harmony-target: CardContainerSO.GetRerollPrice
            n += P(h, typeof(CardContainerSO), "GetRerollPrice", null, nameof(FreeReroll));
            // harmony-target: ACardSO.get_SellingPrice
            n += P(h, typeof(ACardSO), "get_SellingPrice", null, nameof(NoSellCash));
            // harmony-target: CardExtensions.IsCardAvailable
            n += P(h, typeof(CardExtensions), "IsCardAvailable", nameof(OfferLocked), null);
            // harmony-target: CardEquipmentManager.get_CurrentModSlotCount
            n += P(h, typeof(CardEquipmentManager), "get_CurrentModSlotCount", null, nameof(MoreSlots));
            // harmony-target: CardShop_AllCardsSelector.Awake
            n += P(h, typeof(CardShop_AllCardsSelector), "Awake", null, nameof(AfterPickerAwake));
            // harmony-target: CardDisplayGroup.UpdateCards
            n += P(h, typeof(CardDisplayGroup), "UpdateCards", null, nameof(AfterUpdateCards));
            return n;
        }

        private static int P(Harmony h, Type type, string method, string prefix, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null) throw new MissingMethodException(type.Name, method);
                h.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(Shop), prefix),
                                postfix: postfix == null ? null : new HarmonyMethod(typeof(Shop), postfix));
                return 1;
            }
            catch (Exception e)
            {
                // a perk, not a guard: that perk is just off
                Plugin.Log.LogWarning($"[Sandbox] perk {type.Name}.{method} not installed: {e.Message}");
                return 0;
            }
        }

        // ---------------------------------------------------------------- prices

        private static void FreePrice(ref int __result)
        {
            if (!Plugin.Active || __result == 0) return;
            __result = 0;
            if (!_loggedPrice) { _loggedPrice = true; Plugin.Log.LogInfo("[Sandbox] shop: every card is free in this sandbox run"); }
        }

        private static void FreeReroll(ref int __result)
        {
            if (Plugin.Active) __result = 0;
        }

        private static void NoSellCash(ref int __result)
        {
            if (Plugin.Active) __result = 0;
        }

        private static void OfferLocked(ref bool ignoreLockedStatus)
        {
            if (Plugin.Active) ignoreLockedStatus = true;
        }

        private static void MoreSlots(ref int __result)
        {
            if (!Plugin.InSandboxRun) return;
            int extra = Math.Max(0, Math.Min(15, Multiplayer.Slots(Plugin.ExtraSlots.Value)));   // the host's value in a sandbox MP run
            __result += extra;
            if (!_loggedSlots && extra > 0) { _loggedSlots = true; Plugin.Log.LogInfo($"[Sandbox] mod slots: {__result} ({extra} extra in sandbox runs)"); }
        }

        internal static void NewRun() { _loggedPrice = false; _loggedSlots = false; }

        // ---------------------------------------------------------------- all-cards picker

        private static void AfterPickerAwake(CardShop_AllCardsSelector __instance)
        {
            try
            {
                if (__instance == null) return;
                Pickers.RemoveAll(p => p == null);
                PruneDestroyed();
                Pickers.Add(__instance);
                Apply(__instance, Plugin.ActiveNow() && Multiplayer.Picker(Plugin.AllCardsPicker.Value));   // the host's value in a sandbox MP run
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] all-cards picker: {e.Message}"); }
        }

        /// <summary>Shows or hides every known picker (Runner calls it when Active or the setting changes).</summary>
        internal static void ApplyPickers(bool show)
        {
            Pickers.RemoveAll(p => p == null);
            PruneDestroyed();
            for (int i = 0; i < Pickers.Count; i++)
            {
                try { Apply(Pickers[i], show); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] all-cards picker: {e.Message}"); }
            }
        }

        private static void Apply(CardShop_AllCardsSelector picker, bool show)
        {
            var root = picker.debugRoot;
            var buyAll = picker.buyAllButton;
            IntPtr key = picker.Pointer;
            if (buyAll != null)
            {
                if (show)
                {
                    if (!OriginalBuyAll.ContainsKey(key)) OriginalBuyAll[key] = (picker, buyAll.gameObject.activeSelf);
                    buyAll.gameObject.SetActive(false);   // buying every card at once overflows the slots
                }
                else if (OriginalBuyAll.TryGetValue(key, out var was))
                {
                    buyAll.gameObject.SetActive(was.active);
                    OriginalBuyAll.Remove(key);
                }
            }
            if (root != null && root.activeSelf != show)
            {
                root.SetActive(show);   // the game's own Awake leaves it off; Close() and OpenMenu() handle the window inside it
                Plugin.Log.LogInfo($"[Sandbox] all-cards picker {(show ? "shown in the shop" : "hidden")}");
            }
        }

        /// <summary>Drops saved originals whose grid / picker Unity has destroyed (Unity's == null is true for those).</summary>
        private static void PruneDestroyed()
        {
            Dead.Clear();
            foreach (var kv in OriginalRows) if (kv.Value.grid == null) Dead.Add(kv.Key);
            for (int i = 0; i < Dead.Count; i++) OriginalRows.Remove(Dead[i]);
            Dead.Clear();
            foreach (var kv in OriginalBuyAll) if (kv.Value.picker == null) Dead.Add(kv.Key);
            for (int i = 0; i < Dead.Count; i++) OriginalBuyAll.Remove(Dead[i]);
            Dead.Clear();
        }

        // ---------------------------------------------------------------- Current Mods row

        private static void AfterUpdateCards(CardDisplayGroup __instance)
        {
            try
            {
                if (__instance == null || __instance.cardCategory != CardCategory.Mod) return;
                var grid = __instance.flexibleLayoutGroup;
                if (grid == null) return;
                PruneDestroyed();
                IntPtr key = grid.Pointer;
                int count = 0;   // one pooled CardDeckItem per owned mod: count the active ones
                var parent = __instance.spawnParent;
                if (parent != null)
                    for (int i = 0; i < parent.childCount; i++)
                        if (parent.GetChild(i).gameObject.activeSelf) count++;
                if (Plugin.Active && count > 10)
                {
                    if (!OriginalRows.ContainsKey(key)) OriginalRows[key] = (grid, grid.Rows);
                    if (grid.Rows != 2)
                    {
                        grid.SetRows(2);
                        grid.RebuildLayout();
                        Plugin.Log.LogInfo($"[Sandbox] Current Mods: {count} mods, shown in 2 rows");
                    }
                }
                else if (OriginalRows.TryGetValue(key, out var saved))
                {
                    OriginalRows.Remove(key);
                    int rows = saved.rows;
                    if (grid.Rows != rows) { grid.SetRows(rows); grid.RebuildLayout(); }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] Current Mods layout: {e.Message}"); }
        }
    }
}
