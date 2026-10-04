using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Police
{
    /// <summary>Whose game this is: single-player, the multiplayer host (owns traffic), a guest, or multiplayer we can't use.</summary>
    internal enum NetMode { Single, Host, Guest, None }

    /// <summary>A remote player on the host (from NetworkPlayer.instances).</summary>
    internal struct RemotePlayer
    {
        public MonoBehaviour Np;   // Game.Runtime.Multiplayer.NetworkPlayer, untyped (see GameApi)
        public IntPtr Ptr;
        public uint NetId;
        public ulong Steam;        // from its Mirror connection address (FizzySteamworks: the SteamID), 0 if not one
    }

    /// <summary>
    /// Multiplayer (0.8.0), all verified in GameAssembly.dll by disassembly (dump.cs addresses, 2026-10-04):
    /// - Traffic: Game.Runtime.Multiplayer.MultiplayerAISpawner (AISpawnerBase, so AISpawnerBase.Instance) instantiates
    ///   network AI cars and keeps their AIVehicleController in instantiatedCars; respawnVehicle reuses a car in place
    ///   (AIVehicleController.Reset + AIPathFollower.SetVehicle, like the single-player pool). On the host the cars run the
    ///   same AI components as in single-player: NetworkAIVehicle.FixedUpdate (server only) copies
    ///   pathFollower.DistanceTravelled, laneHandler.CurrentLaneOffset, TurnInput and Speed into its SyncVars distance /
    ///   laneOffset / turnInput / speed, so what Police writes into the host's AI moves the cars for everyone, by the game's
    ///   own sync. Police never writes a SyncVar or calls a Command / RPC.
    /// - On a guest, NetworkAIVehicle.OnStartClient destroys controller, pathFollower, laneHandler and the other AI parts
    ///   (only the server keeps them) and SpawnSkinOnClient builds currentSkin and sizes boxCollider: a guest only reads
    ///   those and draws on top (Renderer.enabled of its local copy, never synced).
    /// - Players: NetworkPlayer.instances (static list), LocalPlayer (static). TryUpdateMovementVars (every 0.033 s) sends
    ///   PlayerPathFollower.distanceTravelled (= GetDistanceTravelled), VehicleMovement.CurrentSpeed and the same lane
    ///   offset as GetLaneOffset (both InverseTransformPoint(car).x) through CMD_SetDistanceTravelled, which sets the
    ///   SyncVars distance / velocity / laneOffset on the host. So on the host every player's distance, lane and speed
    ///   are in the same frame as the traffic's AvoidanceRoadDistance / AvoidanceLaneOffset, about half a round trip old.
    ///   NetworkPlayer.IsAIRacer is false for real players (AI racers are skipped).
    /// - SteamIDs: a remote player's connectionToClient.address is the transport's client address (FizzySteamworks: the
    ///   SteamID); a guest connected to networkAddress, which MultiplayerManager.OnLobbyEntered sets from the lobby data
    ///   before StartClient (the host's SteamID); fallback: the lobby owner of MultiplayerManager.GetLobbyId().
    /// </summary>
    internal static partial class GameApi
    {
        internal static bool NetOk { get; private set; }       // host: the multiplayer spawner, players, car netIds
        internal static bool NetViewOk { get; private set; }   // guest: cars by netId, their skin, box and synced distance

        private static bool _spawnerMp;   // _spawner is the MultiplayerAISpawner (host)

        private static void CheckNet(Assembly asm, List<string> missing)
        {
            Assembly mirror = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Mirror");
            if (mirror == null) { try { mirror = Assembly.Load("Mirror"); } catch { /* reported below */ } }
            bool mirrorOk = Has(mirror, "Mirror.NetworkServer", missing, "active")
                         && Has(mirror, "Mirror.NetworkClient", missing, "active", "spawned")
                         && Has(mirror, "Mirror.NetworkBehaviour", missing, "netId", "connectionToClient")
                         && Has(mirror, "Mirror.NetworkConnectionToClient", missing, "address")
                         && Has(mirror, "Mirror.NetworkManager", missing, "singleton", "networkAddress");
            bool players = Has(asm, "Game.Runtime.Multiplayer.NetworkPlayer", missing, "instances", "LocalPlayer", "distance", "laneOffset", "velocity", "isDead", "IsAIRacer");
            NetOk = TrafficOk && ModeOk && mirrorOk && players
                 && Has(asm, "Game.Runtime.Multiplayer.MultiplayerAISpawner", missing, "instantiatedCars")
                 && Has(asm, "NetworkAIVehicle", missing, "controller");
            NetViewOk = ModeOk && mirrorOk && players
                     && Has(asm, "NetworkAIVehicle", missing, "currentSkin", "boxCollider", "speed", "distance")
                     && Has(asm, "MultiplayerManager", missing, "GetLobbyId");
        }

        /// <summary>
        /// The current role. Single in single-player; in multiplayer Host when this game runs the Mirror server, Guest when
        /// it is only a client; None when the mode or role can't be read (nothing runs then).
        /// </summary>
        internal static NetMode Mode()
        {
            if (!IsMultiplayer()) return NetMode.Single;   // unreadable mode counts as multiplayer
            if (!ModeOk || (!NetOk && !NetViewOk)) return NetMode.None;
            try
            {
                if (MirrorServer()) return NetOk ? NetMode.Host : NetMode.None;
                if (MirrorClient()) return NetViewOk ? NetMode.Guest : NetMode.None;
            }
            catch { /* below */ }
            return NetMode.None;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static bool MirrorServer() => Mirror.NetworkServer.active;
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool MirrorClient() => Mirror.NetworkClient.active;

        /// <summary>The spawner as the multiplayer one (host only), or null.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static MonoBehaviour MpSpawner(AISpawnerBase sp) => sp.TryCast<Game.Runtime.Multiplayer.MultiplayerAISpawner>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Il2CppSystem.Collections.Generic.List<AIVehicleController> MpCars(MonoBehaviour sp)
            => ((Game.Runtime.Multiplayer.MultiplayerAISpawner)sp).instantiatedCars;

        /// <summary>The traffic list: DefaultAISpawner.activeAiCars (single-player) or MultiplayerAISpawner.instantiatedCars (host).</summary>
        private static Il2CppSystem.Collections.Generic.List<AIVehicleController> CarList()
        {
            if (_spawner == null) return null;
            return _spawnerMp ? MpCars(_spawner) : ((DefaultAISpawner)_spawner).activeAiCars;
        }

        // ------------------------------------------------------------------ players (host)

        /// <summary>Every remote real player (not the local one, not AI racers). Only when NetOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void RemotePlayers(List<RemotePlayer> into)
        {
            into.Clear();
            var list = Game.Runtime.Multiplayer.NetworkPlayer.instances;
            if (list == null) return;
            var local = Game.Runtime.Multiplayer.NetworkPlayer.LocalPlayer;
            IntPtr lp = local == null ? IntPtr.Zero : local.Pointer;
            for (int i = 0; i < list.Count; i++)
            {
                var np = list[i];
                if (np == null || np.Pointer == lp || np.IsAIRacer) continue;
                ulong steam = 0;
                try { var c = np.connectionToClient; if (c != null) ulong.TryParse(c.address, out steam); } catch { steam = 0; }
                into.Add(new RemotePlayer { Np = np, Ptr = np.Pointer, NetId = np.netId, Steam = steam });
            }
        }

        /// <summary>A remote player's synced road distance, lane offset (m), speed (m/s) and dead flag. False if it is gone.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool ReadRemote(MonoBehaviour npObj, out float dist, out float lane, out float vel, out bool dead)
        {
            dist = lane = vel = 0f; dead = true;
            if (npObj == null) return false;
            var np = (Game.Runtime.Multiplayer.NetworkPlayer)npObj;
            dist = np.distance; lane = np.laneOffset; vel = np.velocity; dead = np.isDead;
            return true;
        }

        /// <summary>The local player's NetworkPlayer netId (0 if none).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static uint LocalNetId()
        {
            var lp = Game.Runtime.Multiplayer.NetworkPlayer.LocalPlayer;
            return lp == null ? 0u : lp.netId;
        }

        /// <summary>A traffic car's netId on the host (its NetworkAIVehicle), 0 if none. Fetch once per patrol / rival.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static uint NetIdOf(MonoBehaviour car)
        {
            if (car == null) return 0;
            var n = car.GetComponent<NetworkAIVehicle>();
            return n == null ? 0u : n.netId;
        }

        // ------------------------------------------------------------------ cars by netId (guest)

        /// <summary>The guest's copy of a network car (NetworkAIVehicle) by netId, or null. Only when NetViewOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MonoBehaviour FindNetCar(uint netId)
        {
            var d = Mirror.NetworkClient.spawned;
            if (d == null || netId == 0) return null;
            if (!d.TryGetValue(netId, out var id) || id == null) return null;
            return id.GetComponent<NetworkAIVehicle>();
        }

        /// <summary>The copy's box collider (car space), zero size if none.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void NetCarBox(MonoBehaviour nav, out Vector3 centre, out Vector3 size)
        {
            var box = ((NetworkAIVehicle)nav).boxCollider;
            if (box == null) { centre = Vector3.zero; size = Vector3.zero; return; }
            centre = box.center; size = box.size;
        }

        /// <summary>The copy's model (built by SpawnSkinOnClient), or null.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static GameObject NetCarSkin(MonoBehaviour nav)
        {
            var s = ((NetworkAIVehicle)nav).currentSkin;
            return s == null ? null : s.gameObject;
        }

        /// <summary>The copy's synced distance along the path and speed (m, m/s): the host's AI values.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void NetCarRead(MonoBehaviour nav, out float dist, out float speed)
        {
            var n = (NetworkAIVehicle)nav;
            dist = n.distance; speed = n.speed;
        }

        /// <summary>The session's Steam lobby (MultiplayerManager.GetLobbyId), 0 if none (LAN).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong LobbyId()
        {
            var nm = Mirror.NetworkManager.singleton;
            if (nm == null) return 0;
            var mm = nm.TryCast<MultiplayerManager>();
            return mm != null ? mm.GetLobbyId() : 0;
        }

        /// <summary>The host's SteamID as a guest sees it (the address it connected to, else the lobby owner), 0 if unknown.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong HostSteamId(out string how)
        {
            how = null;
            var nm = Mirror.NetworkManager.singleton;
            if (nm == null) return 0;
            if (ulong.TryParse(nm.networkAddress, out ulong id) && id > 76561197960265728UL) { how = "connect address"; return id; }
            try
            {
                ulong owner = SteamNet.LobbyOwner(LobbyId());
                if (owner != 0) { how = "lobby owner"; return owner; }
            }
            catch { /* no lobby (LAN) */ }
            return 0;
        }
    }
}
