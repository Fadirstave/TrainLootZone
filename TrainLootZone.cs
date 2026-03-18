using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TrainLootZone", "FadirStave", "1.0")]
    public class TrainLootZone : RustPlugin
    {
        #region Fields

        private SphereEntity dome;
        private StorageContainer stationBarrel;
        private Timer mainLoopTimer;
        private int tick;
        private bool serverReady;

        private readonly Vector3 DomePos = new Vector3(-1184.99f, 21.91f, -606.36f);
        private readonly Vector3 BarrelPos = new Vector3(-1175.78f, 21.76f, -602.06f);
        private const float DomeRadius = 18f;
        private const float DomeRadiusSqr = DomeRadius * DomeRadius;

        private const string SpherePrefab = "assets/bundled/prefabs/modding/events/twitch/br_sphere.prefab";
        private const string BarrelPrefab = "assets/prefabs/misc/decor_dlc/storagebarrel/storage_barrel_b.prefab";
        private static readonly HashSet<string> AllowedWagonPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "assets/content/vehicles/trains/wagons/trainwagonunloadable.entity.prefab",
            "assets/content/vehicles/trains/wagons/trainwagonunloadablefuel.entity.prefab",
            "assets/content/vehicles/trains/wagons/trainwagonunloadableloot.entity.prefab"
        };

        private readonly HashSet<BaseEntity> trackedWagons = new HashSet<BaseEntity>();
        private readonly List<BaseEntity> nearbyEntities = new List<BaseEntity>();
        private readonly List<StorageContainer> tempStorageContainers = new List<StorageContainer>();
        private readonly List<BaseEntity> staleWagons = new List<BaseEntity>();
        private readonly List<ulong> stalePlayerIds = new List<ulong>();
        private readonly HashSet<ulong> playersSeenThisTick = new HashSet<ulong>();

        private readonly Dictionary<BaseEntity, Vector3> wagonLastPos = new Dictionary<BaseEntity, Vector3>();
        private readonly Dictionary<BaseEntity, float> wagonStillSince = new Dictionary<BaseEntity, float>();
        private readonly Dictionary<BaseEntity, bool> wagonUnloading = new Dictionary<BaseEntity, bool>();

        private readonly HashSet<ulong> playersInDome = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> lastGreetingTime = new Dictionary<ulong, float>();
        private readonly Dictionary<string, Dictionary<ulong, float>> messageCooldowns = new Dictionary<string, Dictionary<ulong, float>>();

        private const float MainLoopInterval = 2f;
        private const float GreetingCooldown = 60f;
        private const float StillTime = 3f;
        private const float UnloadDuration = 10f;
        private const float MoveThreshold = 0.05f;
        private const float MoveThresholdSqr = MoveThreshold * MoveThreshold;
        private const float MoveCancelCooldown = 8f;
        private const float StartCooldown = 8f;
        private const float DoneCooldown = 8f;
        private const float DestroyCooldown = 8f;
        private const float PlayerDataTimeout = 600f;

        private PluginConfig config;

        private string formattedWelcomeMessage;
        private string formattedUnloadStartMessage;
        private string formattedUnloadCompleteMessage;
        private string formattedDestroyMessage;
        private string formattedMoveCancelMessage;

        #endregion

        #region Config

        private class PluginConfig
        {
            public string Prefix { get; set; } = "[TrainDepo]";
            public string PrefixColor { get; set; } = "#F57C00";
            public string MessageColor { get; set; } = "#FFFFFF";
            public string HighlightColor { get; set; } = "#F57C00";

            public string WelcomeMessage { get; set; } = "You approach the Depot. Draw your wagon to the heart of the dome, bring it to a full halt, and dismount whilst the Depot hands begin their work.";
            public string UnloadStartMessage { get; set; } = "The Depot hands begin their work… unloading has begun! Please wait....";
            public string UnloadCompleteMessage { get; set; } = "Your wagon’s spoils have been secured within the Depot barrel at the station’s entrance.";
            public string DestroyMessage { get; set; } = "The wagon is dismantled. May your spoils serve you well.";
            public string MoveCancelMessage { get; set; } = "Your wagon shifted — the Depot workers flee and halt their labor!";

            public List<string> HighlightWords { get; set; } = new List<string>()
            {
                "Depot", "unloading", "spoils", "secured", "dismantled",
                "shifted", "halt", "serviced", "workers", "barrel",
                "wagon", "spoils", "hands", "commences"
            };
        }

        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig();
            SaveConfig();
            BuildFormattedMessages();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
            BuildFormattedMessages();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        #endregion

        #region Hooks

        private void Init()
        {
            if (config == null)
            {
                LoadConfig();
            }
            else
            {
                BuildFormattedMessages();
            }

        }

        private void OnServerInitialized()
        {
            serverReady = true;
            timer.Once(2f, () =>
            {
                if (!serverReady)
                {
                    return;
                }

                RemoveOldDomeAtPosition();
                FindOrCreateBarrel();
                SpawnDome();
                TrackExistingTrainCars();
                StartMainLoop();
            });
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var baseEntity = entity as BaseEntity;
            if (baseEntity == null || baseEntity.IsDestroyed)
            {
                return;
            }

            if (IsCargoWagon(baseEntity))
            {
                trackedWagons.Add(baseEntity);
            }
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var baseEntity = entity as BaseEntity;
            if (baseEntity == null)
            {
                return;
            }

            if (ReferenceEquals(baseEntity, dome))
            {
                dome = null;
            }

            if (ReferenceEquals(baseEntity, stationBarrel))
            {
                stationBarrel = null;
            }

            if (trackedWagons.Remove(baseEntity))
            {
                ResetWagon(baseEntity);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
            {
                return;
            }

            var id = player.userID;
            playersInDome.Remove(id);
            lastGreetingTime.Remove(id);

            foreach (var cooldownByPlayer in messageCooldowns.Values)
            {
                cooldownByPlayer.Remove(id);
            }
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (stationBarrel != null && ReferenceEquals(entity, stationBarrel))
            {
                info.damageTypes.Clear();
                return true;
            }

            return null;
        }

        private void Unload()
        {
            if (mainLoopTimer != null)
            {
                mainLoopTimer.Destroy();
                mainLoopTimer = null;
            }

            if (dome != null && !dome.IsDestroyed)
            {
                dome.Kill();
            }

            serverReady = false;
            tick = 0;
            staleWagons.Clear();
            trackedWagons.Clear();
            wagonLastPos.Clear();
            wagonStillSince.Clear();
            wagonUnloading.Clear();
            playersInDome.Clear();
            lastGreetingTime.Clear();
            messageCooldowns.Clear();
        }

        #endregion

        #region Core Logic

        private void StartMainLoop()
        {
            if (mainLoopTimer != null)
            {
                mainLoopTimer.Destroy();
            }

            tick = 0;
            mainLoopTimer = timer.Every(MainLoopInterval, () =>
            {
                tick++;

                UpdateTrackedWagons();

                if (tick % 2 == 0)
                {
                    UpdatePlayersInZone();
                }

                if (tick % 5 == 0)
                {
                    CleanupStalePlayerData(Time.realtimeSinceStartup);
                }
            });
        }

        private void UpdatePlayersInZone()
        {
            float now = Time.realtimeSinceStartup;
            playersSeenThisTick.Clear();

            var activePlayers = BasePlayer.activePlayerList;
            for (int i = 0; i < activePlayers.Count; i++)
            {
                var player = activePlayers[i];
                if (player == null || !player.IsConnected)
                {
                    continue;
                }

                ulong playerId = player.userID;
                Vector3 playerPos = player.transform.position;
                bool inside = (playerPos - DomePos).sqrMagnitude <= DomeRadiusSqr;

                if (!inside)
                {
                    continue;
                }

                playersSeenThisTick.Add(playerId);

                if (playersInDome.Contains(playerId))
                {
                    continue;
                }

                playersInDome.Add(playerId);

                float last;
                if (!lastGreetingTime.TryGetValue(playerId, out last))
                {
                    last = 0f;
                }
                if (now - last < GreetingCooldown)
                {
                    continue;
                }

                player.ChatMessage(formattedWelcomeMessage);
                lastGreetingTime[playerId] = now;
            }

            stalePlayerIds.Clear();
            foreach (ulong playerId in playersInDome)
            {
                if (!playersSeenThisTick.Contains(playerId))
                {
                    stalePlayerIds.Add(playerId);
                }
            }

            for (int i = 0; i < stalePlayerIds.Count; i++)
            {
                playersInDome.Remove(stalePlayerIds[i]);
            }
        }

        private void UpdateTrackedWagons()
        {
            if (trackedWagons.Count == 0)
            {
                return;
            }

            Vector3 domePos = DomePos;
            staleWagons.Clear();

            foreach (var wagon in trackedWagons)
            {
                if (wagon == null || wagon.IsDestroyed)
                {
                    staleWagons.Add(wagon);
                    continue;
                }

                if ((wagon.transform.position - domePos).sqrMagnitude > DomeRadiusSqr)
                {
                    continue;
                }

                HandleSingleWagon(wagon);
            }

            for (int i = 0; i < staleWagons.Count; i++)
            {
                trackedWagons.Remove(staleWagons[i]);
                ResetWagon(staleWagons[i]);
            }
        }

        private void HandleSingleWagon(BaseEntity wagon)
        {
            float now = Time.realtimeSinceStartup;
            Vector3 position = wagon.transform.position;

            if (!wagonLastPos.TryGetValue(wagon, out Vector3 lastPos))
            {
                wagonLastPos[wagon] = position;
                wagonStillSince[wagon] = 0f;
                wagonUnloading[wagon] = false;
                return;
            }

            Vector3 delta = position - lastPos;
            float movedSqr = delta.sqrMagnitude;
            wagonLastPos[wagon] = position;

            bool unloading;
            if (!wagonUnloading.TryGetValue(wagon, out unloading))
            {
                unloading = false;
            }
            if (movedSqr > MoveThresholdSqr)
            {
                wagonStillSince[wagon] = 0f;
                if (unloading)
                {
                    wagonUnloading[wagon] = false;
                    SendToDome("cancel", formattedMoveCancelMessage, MoveCancelCooldown);
                }
                return;
            }

            float stillSince;
            if (!wagonStillSince.TryGetValue(wagon, out stillSince))
            {
                stillSince = 0f;
            }
            if (stillSince <= 0f)
            {
                wagonStillSince[wagon] = now;
                return;
            }

            if (!unloading && now - stillSince >= StillTime)
            {
                BeginUnload(wagon);
            }
        }

        private void BeginUnload(BaseEntity wagon)
        {
            wagonUnloading[wagon] = true;
            SendToDome("start", formattedUnloadStartMessage, StartCooldown);

            timer.Once(UnloadDuration, () =>
            {
                if (!serverReady)
                {
                    return;
                }

                if (wagon == null || wagon.IsDestroyed)
                {
                    ResetWagon(wagon);
                    return;
                }

                if (!wagonLastPos.TryGetValue(wagon, out Vector3 lastPos))
                {
                    wagonUnloading[wagon] = false;
                    return;
                }

                Vector3 currentPos = wagon.transform.position;
                if ((currentPos - lastPos).sqrMagnitude > MoveThresholdSqr)
                {
                    wagonUnloading[wagon] = false;
                    SendToDome("cancel", formattedMoveCancelMessage, MoveCancelCooldown);
                    return;
                }

                bool success = TransferLoot(wagon);
                if (!success)
                {
                    wagonUnloading[wagon] = false;
                    return;
                }

                SendToDome("done", formattedUnloadCompleteMessage, DoneCooldown);

                timer.Once(2f, () =>
                {
                    if (!serverReady)
                    {
                        return;
                    }

                    if (wagon != null && !wagon.IsDestroyed)
                    {
                        wagon.Kill();
                    }

                    SendToDome("destroy", formattedDestroyMessage, DestroyCooldown);
                    ResetWagon(wagon);
                });
            });
        }

        private bool TransferLoot(BaseEntity wagon)
        {
            if (wagon == null || wagon.IsDestroyed || stationBarrel == null || stationBarrel.inventory == null)
            {
                return false;
            }

            try
            {
                wagon.GetComponentsInChildren(true, tempStorageContainers);

                for (int i = tempStorageContainers.Count - 1; i >= 0; i--)
                {
                    var container = tempStorageContainers[i];
                    if (container == null || container.inventory == null)
                    {
                        tempStorageContainers.RemoveAt(i);
                        continue;
                    }

                    if (container.GetParentEntity() != wagon)
                    {
                        tempStorageContainers.RemoveAt(i);
                        continue;
                    }

                    if (ReferenceEquals(container, stationBarrel))
                    {
                        tempStorageContainers.RemoveAt(i);
                        continue;
                    }

                }

                if (tempStorageContainers.Count == 0)
                {
                    return true;
                }

                for (int i = 0; i < tempStorageContainers.Count; i++)
                {
                    var container = tempStorageContainers[i];

                    // TEMP DEBUG: Remove after source validation is complete.
                    Puts($"[TEMP DEBUG] Wagon prefab: {wagon.PrefabName}");
                    Puts($"[TEMP DEBUG] Container short: {container.ShortPrefabName}, prefab: {container.PrefabName}");
                    var debugItems = container.inventory.itemList;
                    for (int debugIndex = 0; debugIndex < debugItems.Count; debugIndex++)
                    {
                        var debugItem = debugItems[debugIndex];
                        if (debugItem == null)
                        {
                            continue;
                        }

                        Puts($"[TEMP DEBUG]  - item: {debugItem.info.shortname} x{debugItem.amount}");
                    }

                    var items = container.inventory.itemList;
                    bool failed = false;
                    for (int itemIndex = items.Count - 1; itemIndex >= 0; itemIndex--)
                    {
                        var item = items[itemIndex];
                        if (item == null)
                        {
                            continue;
                        }

                        if (!item.MoveToContainer(stationBarrel.inventory))
                        {
                            failed = true;
                            break;
                        }
                    }

                    if (failed)
                    {
                        SendToDome("full", "<color=#FF5555>Please empty the Depot Barrel before unloading more wagons.</color>", 5f);
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                tempStorageContainers.Clear();
            }
        }

        private void ResetWagon(BaseEntity wagon)
        {
            if (wagon == null)
            {
                return;
            }

            wagonLastPos.Remove(wagon);
            wagonStillSince.Remove(wagon);
            wagonUnloading.Remove(wagon);
        }

        #endregion

        #region Helpers

        private void BuildFormattedMessages()
        {
            formattedWelcomeMessage = BuildPreformattedMessage(config.WelcomeMessage);
            formattedUnloadStartMessage = BuildPreformattedMessage(config.UnloadStartMessage);
            formattedUnloadCompleteMessage = BuildPreformattedMessage(config.UnloadCompleteMessage);
            formattedDestroyMessage = BuildPreformattedMessage(config.DestroyMessage);
            formattedMoveCancelMessage = BuildPreformattedMessage(config.MoveCancelMessage);
        }

        private string BuildPreformattedMessage(string rawMessage)
        {
            string highlightedBody = HighlightWords(rawMessage);
            return $"<color={config.PrefixColor}>{config.Prefix}</color> <color={config.MessageColor}>{highlightedBody}</color>";
        }

        private string HighlightWords(string message)
        {
            if (string.IsNullOrEmpty(message) || config.HighlightWords == null || config.HighlightWords.Count == 0)
            {
                return message;
            }

            var highlights = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < config.HighlightWords.Count; i++)
            {
                string word = config.HighlightWords[i];
                if (string.IsNullOrWhiteSpace(word) || highlights.ContainsKey(word))
                {
                    continue;
                }

                highlights[word] = $"<color={config.HighlightColor}><b>{word}</b></color>";
            }

            var builder = new StringBuilder(message.Length + 64);
            int index = 0;
            while (index < message.Length)
            {
                if (char.IsLetterOrDigit(message[index]))
                {
                    int start = index;
                    while (index < message.Length && char.IsLetterOrDigit(message[index]))
                    {
                        index++;
                    }

                    string token = message.Substring(start, index - start);
                    if (highlights.TryGetValue(token, out string replacement))
                    {
                        builder.Append(replacement);
                    }
                    else
                    {
                        builder.Append(token);
                    }

                    continue;
                }

                builder.Append(message[index]);
                index++;
            }

            return builder.ToString();
        }

        private void SendToDome(string key, string message, float cooldown)
        {
            float now = Time.realtimeSinceStartup;

            if (!messageCooldowns.TryGetValue(key, out Dictionary<ulong, float> cooldownByPlayer))
            {
                cooldownByPlayer = new Dictionary<ulong, float>();
                messageCooldowns[key] = cooldownByPlayer;
            }

            var activePlayers = BasePlayer.activePlayerList;
            for (int i = 0; i < activePlayers.Count; i++)
            {
                var player = activePlayers[i];
                if (player == null || !player.IsConnected)
                {
                    continue;
                }

                if ((player.transform.position - DomePos).sqrMagnitude > DomeRadiusSqr)
                {
                    continue;
                }

                ulong playerId = player.userID;
                float lastSent;
                if (!cooldownByPlayer.TryGetValue(playerId, out lastSent))
                {
                    lastSent = 0f;
                }
                if (now - lastSent < cooldown)
                {
                    continue;
                }

                player.ChatMessage(message);
                cooldownByPlayer[playerId] = now;
            }
        }

        private void CleanupStalePlayerData(float now)
        {
            stalePlayerIds.Clear();
            foreach (var kvp in lastGreetingTime)
            {
                if (now - kvp.Value > PlayerDataTimeout)
                {
                    stalePlayerIds.Add(kvp.Key);
                }
            }

            for (int i = 0; i < stalePlayerIds.Count; i++)
            {
                ulong playerId = stalePlayerIds[i];
                lastGreetingTime.Remove(playerId);
                playersInDome.Remove(playerId);
            }

            foreach (var cooldownByPlayer in messageCooldowns.Values)
            {
                stalePlayerIds.Clear();
                foreach (var kvp in cooldownByPlayer)
                {
                    if (now - kvp.Value > PlayerDataTimeout)
                    {
                        stalePlayerIds.Add(kvp.Key);
                    }
                }

                for (int i = 0; i < stalePlayerIds.Count; i++)
                {
                    cooldownByPlayer.Remove(stalePlayerIds[i]);
                }
            }
        }

        private bool IsCargoWagon(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed)
            {
                return false;
            }

            return AllowedWagonPrefabs.Contains(entity.PrefabName);
        }

        private void SpawnDome()
        {
            var domeEntity = GameManager.server.CreateEntity(SpherePrefab, DomePos) as SphereEntity;
            if (domeEntity == null)
            {
                return;
            }

            dome = domeEntity;
            dome.currentRadius = DomeRadius;
            dome.lerpRadius = DomeRadius;
            dome.enableSaving = false;
            dome.Spawn();
        }

        private void RemoveOldDomeAtPosition()
        {
            nearbyEntities.Clear();
            Vis.Entities(DomePos, 1f, nearbyEntities);
            for (int i = 0; i < nearbyEntities.Count; i++)
            {
                var entity = nearbyEntities[i];
                if (entity == null || entity.IsDestroyed)
                {
                    continue;
                }

                if (entity is SphereEntity && entity.PrefabName == SpherePrefab)
                {
                    entity.Kill();
                }
            }

            nearbyEntities.Clear();
        }

        private void FindOrCreateBarrel()
        {
            nearbyEntities.Clear();
            Vis.Entities(BarrelPos, 1.5f, nearbyEntities);
            for (int i = 0; i < nearbyEntities.Count; i++)
            {
                var storageContainer = nearbyEntities[i] as StorageContainer;
                if (storageContainer != null)
                {
                    stationBarrel = storageContainer;
                    stationBarrel.pickup.enabled = false;
                    ConfigureStationBarrel();
                    nearbyEntities.Clear();
                    return;
                }
            }

            nearbyEntities.Clear();

            var barrel = GameManager.server.CreateEntity(BarrelPrefab, BarrelPos) as StorageContainer;
            if (barrel == null)
            {
                return;
            }

            barrel.enableSaving = false;
            barrel.Spawn();
            stationBarrel = barrel;

            stationBarrel.pickup.enabled = false;

            ConfigureStationBarrel();
        }

        private void ConfigureStationBarrel()
        {
            if (stationBarrel == null)
            {
                return;
            }

            stationBarrel.inventory?.Clear();
            if (stationBarrel is LootContainer lootContainer)
            {
                lootContainer.lootDefinition = null;
                lootContainer.CancelInvoke("SpawnLoot");
            }
        }

        private void TrackExistingTrainCars()
        {
            var allTrainCars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
            for (int i = 0; i < allTrainCars.Length; i++)
            {
                var trainCar = allTrainCars[i];
                if (trainCar == null)
                {
                    continue;
                }

                var baseEntity = trainCar as BaseEntity;
                if (!IsCargoWagon(baseEntity))
                {
                    continue;
                }

                trackedWagons.Add(baseEntity);
            }
        }

        #endregion
    }
}