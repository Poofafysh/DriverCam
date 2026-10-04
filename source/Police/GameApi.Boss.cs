using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// What a police car model is built from: one boss's car (its VehicleSkinHolder prefab, the same model a boss and
    /// the player drive) and which of its body-kit parts the boss runs. Read only; we never instantiate the prefab
    /// (its scripts and ~300 VFX objects never run): PoliceCar copies only the visible meshes.
    /// </summary>
    internal sealed class BossModel
    {
        public string Boss, Car;
        public GameObject Prefab;
        /// <summary>Position in the game's own boss list (deduplicated), 0 = first: Daredevils give each boss a fixed skill by it.</summary>
        public int Rank;
        /// <summary>Part objects to show / hide regardless of their saved active flag (the boss's body kit).</summary>
        public readonly HashSet<int> ForceOn = new HashSet<int>(), ForceOff = new HashSet<int>();
    }

    /// <summary>
    /// Boss cars (verified in dump.cs / GameAssembly.dll):
    /// GeneralReferencesData.Instance (an always-loaded singleton ScriptableObject) .BossProfileContainer.ItemArray ->
    /// BossProfileSO { profileName, vehicle: Vehicle_SO, customParts: VehiclePartData[] };
    /// Vehicle_SO { vehicleName, VehicleBody: VehicleSkinHolder (the car model prefab) };
    /// VehicleSkinHolder.vehiclePartsReferences: VehiclePartReference[] { PartType, PartArray: GameObject[], DefaultPart };
    /// VehiclePartData { PartType, PartIndex }. A boss part of the same type picks PartArray[PartIndex]; the other
    /// candidates of that slot are hidden. The traffic car's own look: AIVehicleController.SkinSelector.CurrentSkin
    /// (AIVehicleSkin), whose renderers we disable while it is a police car and give back on release.
    /// </summary>
    internal static partial class GameApi
    {
        internal static bool BossOk { get; private set; }   // boss models available (else patrols keep their traffic look + lightbar)
        internal static bool SkinOk { get; private set; }   // the traffic car's own model can be found (to hide it)

        private static void CheckBoss(Assembly asm, List<string> missing)
        {
            BossOk = Has(asm, "Game.Runtime.Data.GeneralReferencesData", missing, "BossProfileContainer")
                  && Has(asm, "SingletonScriptableObject`1", missing, "Instance")
                  && Has(asm, "Game.Runtime.Data.BossProfileContainerSO", missing)
                  && Has(asm, "Game.Runtime.Data.BossProfileSO", missing, "vehicle", "customParts", "profileName")
                  && Has(asm, "Game.Runtime.Data.Vehicle_SO", missing, "VehicleBody", "vehicleName")
                  && Has(asm, "VehicleSkinHolder", missing, "vehiclePartsReferences")
                  && Has(asm, "VehiclePartReference", missing, "PartType", "PartArray", "DefaultPart")
                  && Has(asm, "Game.Runtime.Data.VehiclePartData", missing, "PartType", "PartIndex");
            SkinOk = Has(asm, "AIVehicleController", missing, "SkinSelector")
                  && Has(asm, "AISkinSelector", missing, "CurrentSkin")
                  && Has(asm, "AIVehicleSkin", missing, "pulseAffectedRenderer");
        }

        /// <summary>
        /// Reads every boss's car model (deduplicated by prefab). Empty if the boss list isn't loaded yet. Only call when BossOk.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void LoadBossModels(List<BossModel> into, Action<string> warn)
        {
            into.Clear();
            var refs = Game.Runtime.Data.GeneralReferencesData.Instance;
            if (refs == null) return;
            var container = refs.BossProfileContainer;
            if (container == null) return;
            var bosses = container.ItemArray;
            if (bosses == null) return;
            var seen = new HashSet<IntPtr>();
            for (int b = 0; b < bosses.Length; b++)
            {
                var boss = bosses[b];
                if (boss == null) continue;
                try
                {
                    var veh = boss.vehicle;
                    var skin = veh != null ? veh.VehicleBody : null;
                    if (skin == null) continue;
                    var go = skin.gameObject;
                    if (go == null || !seen.Add(go.Pointer)) continue;
                    var m = new BossModel { Boss = boss.profileName ?? "?", Car = veh.vehicleName ?? go.name, Prefab = go, Rank = into.Count };
                    ApplyParts(skin, boss.customParts, m);
                    into.Add(m);
                }
                catch (Exception e) { warn?.Invoke($"boss {b}: {e.Message}"); }
            }
        }

        private static void ApplyParts(VehicleSkinHolder skin, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Game.Runtime.Data.VehiclePartData> custom, BossModel m)
        {
            var slots = skin.vehiclePartsReferences;
            if (slots == null || custom == null || custom.Length == 0) return;
            for (int s = 0; s < slots.Length; s++)
            {
                var slot = slots[s];
                if (slot == null || slot.PartArray == null) continue;
                Game.Runtime.Data.VehiclePartData pick = null;
                for (int c = 0; c < custom.Length; c++)
                    if (custom[c] != null && custom[c].PartType == slot.PartType) { pick = custom[c]; break; }
                if (pick == null) continue;
                int idx = pick.PartIndex;
                var arr = slot.PartArray;
                if (idx < 0 || idx >= arr.Length || arr[idx] == null) continue;
                int chosen = arr[idx].GetInstanceID();
                m.ForceOn.Add(chosen);
                for (int i = 0; i < arr.Length; i++) if (arr[i] != null && i != idx) m.ForceOff.Add(arr[i].GetInstanceID());
                if (slot.DefaultPart != null && slot.DefaultPart.GetInstanceID() != chosen) m.ForceOff.Add(slot.DefaultPart.GetInstanceID());
            }
        }

        /// <summary>
        /// The skin's pulse-effect clone (AIVehicleSkin.pulseAffectedRenderer: a copy of the car's main renderer the game
        /// itself switches on and off), or null. PoliceCar keeps it hidden but never switches it back on.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Renderer PulseRendererOf(GameObject skin)
        {
            try
            {
                var s = skin.GetComponent<AIVehicleSkin>();
                return s != null ? s.pulseAffectedRenderer : null;
            }
            catch { return null; }
        }

        /// <summary>The traffic car's current model object (its AIVehicleSkin), or null. Only call when SkinOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static GameObject SkinOf(MonoBehaviour car)
        {
            var sel = ((AIVehicleController)car).SkinSelector;
            if (sel == null) return null;
            var skin = sel.CurrentSkin;
            return skin != null ? skin.gameObject : null;
        }
    }
}
