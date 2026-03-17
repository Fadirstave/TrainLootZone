using UnityEngine;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("TrainLootZone", "FadirStave", "17.0")]
    public class TrainLootZone : RustPlugin
    {
        private SphereEntity dome;
        private StorageContainer stationBarrel;

        // Zone positions
        private readonly Vector3 DomePos = new Vector3(-1184.99f, 21.91f, -606.36f);
        private readonly Vector3 BarrelPos = new Vector3(-1175.78f, 21.76f, -602.06f);
        private const float DomeRadius = 18f;

        // Prefabs
        private const string SpherePrefab = "assets/bundled/prefabs/modding/events/twitch/br_sphere.prefab";
        private const string BarrelPrefab = "assets/prefabs/misc/decor_dlc/storagebarrel/storage_barrel_b.prefab";
        private const string TrainWagonLootPrefab = "trainwagonunloadableloot.entity";

        // Internal state
        private readonly Dictionary<BaseEntity, Vector3> wagonLastPos = new();
        private readonly Dictionary<BaseEntity, float> wagonStillSince = new();
        private readonly Dictionary<BaseEntity, bool> wagonUnloading = new();
        private readonly HashSet<ulong> playersInDome = new();
        private readonly Dictionary<ulong, float> lastGreetingTime = new();
        private readonly Dictionary<ulong, float> exitTimes = new();
        private readonly Dictionary<string, Dictionary<ulong, float>> messageCooldowns = new();

        // Timing
        private const float GreetingCooldown = 60f;
        private const float StillTime = 3f;
        private const float UnloadDuration = 10f;
        private const float MoveThreshold = 0.05f;
        private const float MultiWagonCooldown = 20f;
        private const float MoveCancelCooldown = 8f;
        private const float StartCooldown = 8f;
        private const float DoneCooldown = 8f;
        private const float DestroyCooldown = 8f;

        // Config container
        private PluginConfig config;

        // ---------------------------------------------------------
        // CONFIG
        // ---------------------------------------------------------
        private class PluginConfig
        {
            public string Prefix { get; set; } = "[TrainDepo]";
            public string PrefixColor { get; set; } = "#F57C00";
            public string MessageColor { get; set; } = "#FFFFFF";
            public string HighlightColor { get; set; } = "#F57C00";

            public string WelcomeMessage { get; set; } =
                "You approach the Depot. Steady your wagon and prepare for unloading.";

            public string UnloadStartMessage { get; set; } =
                "The Depot hands begin their work… unloading commences.";

            public string UnloadCompleteMessage { get; set; } =
                "Your wagon’s spoils have been secured in the Depot barrel.";

            public string DestroyMessage { get; set; } =
                "The wagon is dismantled. May your spoils serve you well.";

            public string MoveCancelMessage { get; set; } =
                "Your wagon shifted — the Depot workers flee and halt their labor!";

            public string MultiWagonMessage { get; set; } =
                "Too many wagons crowd the Depot! Only one may be serviced at a time.";

            public List<string> HighlightWords { get; set; } = new()
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
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<PluginConfig>();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        // ---------------------------------------------------------
        // MESSAGE BUILDER
        // ---------------------------------------------------------
        private string BuildMessage(string message)
        {
            string prefix = $"<color={config.PrefixColor}>{config.Prefix}</color>";
            string body = message;

            foreach (var word in config.HighlightWords)
            {
                body = System.Text.RegularExpressions.Regex.Replace(
                    body,
                    $@"\b{word}\b",
                    $"<color={config.HighlightColor}><b>{word}</b></color>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
            }

            return $"{prefix} <color={config.MessageColor}>{body}</color>";
        }

        // ---------------------------------------------------------
        // INIT
        // ---------------------------------------------------------
        void OnServerInitialized()
        {
            RemoveOldDomes();
            FindOrCreateBarrel();
            SpawnDome();

            timer.Every(0.5f, () =>
            {
                HandlePlayers();
                HandleWagons();
            });
        }

        private void SpawnDome()
        {
            dome = GameManager.server.CreateEntity(SpherePrefab, DomePos) as SphereEntity;
            if (dome == null) return;

            dome.currentRadius = DomeRadius;
            dome.lerpRadius = DomeRadius;
            dome.enableSaving = false;
            dome.Spawn();
        }

        private void RemoveOldDomes()
        {
            foreach (var ent in BaseNetworkable.serverEntities)
                if (ent is SphereEntity dome) dome.Kill();
        }

        private void FindOrCreateBarrel()
        {
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent is StorageContainer sc &&
                    Vector3.Distance(sc.transform.position, BarrelPos) < 1.5f)
                {
                    stationBarrel = sc;
                    sc.pickup.enabled = false;
                    ConfigureStationBarrel();
                    return;
                }
            }

            var b = GameManager.server.CreateEntity(BarrelPrefab, BarrelPos);
            b.enableSaving = false;
            b.Spawn();

            stationBarrel = b as StorageContainer;
            stationBarrel.pickup.enabled = false;
            ConfigureStationBarrel();
        }

        private void ConfigureStationBarrel()
        {
            if (stationBarrel == null) return;

            stationBarrel.inventory?.Clear();
            if (stationBarrel is LootContainer lootContainer)
            {
                lootContainer.lootDefinition = null;
                lootContainer.CancelInvoke("SpawnLoot");
            }
        }

        object OnEntityTakeDamage(BaseCombatEntity e, HitInfo info)
        {
            if (stationBarrel && ReferenceEquals(e, stationBarrel))
            {
                info.damageTypes.Clear();
                return true;
            }
            return null;
        }

        // ---------------------------------------------------------
        // PLAYER HANDLING
        // ---------------------------------------------------------
        private void HandlePlayers()
        {
            float now = Time.realtimeSinceStartup;
            HashSet<ulong> insideNow = new();

            foreach (BasePlayer p in BasePlayer.activePlayerList)
            {
                if (!p.IsConnected) continue;

                bool inside = Vector3.Distance(p.transform.position, DomePos) <= DomeRadius;

                if (inside)
                {
                    insideNow.Add(p.userID);

                    if (!playersInDome.Contains(p.userID))
                    {
                        playersInDome.Add(p.userID);

                        float last = lastGreetingTime.GetValueOrDefault(p.userID, 0);
                        if (now - last >= GreetingCooldown)
                        {
                            p.ChatMessage(BuildMessage(config.WelcomeMessage));
                            lastGreetingTime[p.userID] = now;
                        }
                    }
                }
                else
                {
                    exitTimes[p.userID] = now;
                }
            }

            playersInDome.RemoveWhere(id => !insideNow.Contains(id));
        }

        // ---------------------------------------------------------
        // WAGONS
        // ---------------------------------------------------------
        private bool IsCargoWagon(BaseEntity ent)
        {
            TrainCar tc = ent.GetComponent<TrainCar>();
            if (tc == null) return false;
            if (tc is TrainEngine) return false; // ignore workcarts/locos
            return true;
        }

        private void HandleWagons()
        {
            List<BaseEntity> wagons = new();

            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent is BaseEntity be &&
                    IsCargoWagon(be) &&
                    Vector3.Distance(be.transform.position, DomePos) <= DomeRadius)
                {
                    wagons.Add(be);
                }
            }

            if (wagons.Count == 0) return;

            if (wagons.Count > 1)
            {
                SendToDome("multi", config.MultiWagonMessage, MultiWagonCooldown);
                foreach (var w in wagons) ResetWagon(w);
                return;
            }

            HandleSingleWagon(wagons[0]);
        }

        private void HandleSingleWagon(BaseEntity wagon)
        {
            float now = Time.realtimeSinceStartup;
            Vector3 pos = wagon.transform.position;
            Vector3 last;

            bool hadLast = wagonLastPos.TryGetValue(wagon, out last);
            if (!hadLast)
            {
                wagonLastPos[wagon] = pos;
                wagonStillSince[wagon] = 0;
                wagonUnloading[wagon] = false;
                return;
            }

            float moved = Vector3.Distance(last, pos);
            wagonLastPos[wagon] = pos;

            bool unloading = wagonUnloading.GetValueOrDefault(wagon);

            if (moved > MoveThreshold)
            {
                wagonStillSince[wagon] = 0;

                if (unloading)
                {
                    wagonUnloading[wagon] = false;
                    SendToDome("cancel", config.MoveCancelMessage, MoveCancelCooldown);
                }
                return;
            }

            float stillSince = wagonStillSince.GetValueOrDefault(wagon, 0);
            if (stillSince == 0)
            {
                wagonStillSince[wagon] = now;
                return;
            }

            if (!unloading && now - stillSince >= StillTime)
                BeginUnload(wagon);
        }

        private void BeginUnload(BaseEntity wagon)
        {
            wagonUnloading[wagon] = true;

            SendToDome("start", config.UnloadStartMessage, StartCooldown);

            timer.Once(UnloadDuration, () =>
            {
                if (!wagon || wagon.IsDestroyed) return;

                Vector3 pos = wagon.transform.position;
                Vector3 last = wagonLastPos[wagon];

                if (Vector3.Distance(pos, last) > MoveThreshold)
                {
                    wagonUnloading[wagon] = false;
                    SendToDome("cancel", config.MoveCancelMessage, MoveCancelCooldown);
                    return;
                }

                TransferLoot(wagon);
                SendToDome("done", config.UnloadCompleteMessage, DoneCooldown);

                timer.Once(2f, () =>
                {
                    if (!wagon.IsDestroyed) wagon.Kill();
                    SendToDome("destroy", config.DestroyMessage, DestroyCooldown);

                    ResetWagon(wagon);
                });
            });
        }

        private void TransferLoot(BaseEntity wagon)
        {
            if (wagon == null || stationBarrel == null || stationBarrel.inventory == null) return;

            var containers = wagon.GetComponentsInChildren<StorageContainer>(true);
            if (containers == null || containers.Length == 0) return;

            foreach (var container in containers)
            {
                if (container == null || container.inventory == null) continue;
                if (ReferenceEquals(container, stationBarrel)) continue;
                if (container.ShortPrefabName == TrainWagonLootPrefab) continue;

                foreach (var item in new List<Item>(container.inventory.itemList))
                    item.MoveToContainer(stationBarrel.inventory, -1, true);
            }
        }

        private void ResetWagon(BaseEntity wagon)
        {
            wagonLastPos.Remove(wagon);
            wagonStillSince.Remove(wagon);
            wagonUnloading.Remove(wagon);
        }

        // ---------------------------------------------------------
        // MESSAGE SENDER
        // ---------------------------------------------------------
        private void SendToDome(string key, string message, float cooldown)
        {
            float now = Time.realtimeSinceStartup;

            if (!messageCooldowns.ContainsKey(key))
                messageCooldowns[key] = new Dictionary<ulong, float>();

            var dict = messageCooldowns[key];

            foreach (var p in BasePlayer.activePlayerList)
            {
                if (!p.IsConnected) continue;
                if (Vector3.Distance(p.transform.position, DomePos) > DomeRadius) continue;

                ulong id = p.userID;
                float last = dict.GetValueOrDefault(id, 0);

                if (now - last >= cooldown)
                {
                    p.ChatMessage(BuildMessage(message));
                    dict[id] = now;
                }
            }
        }

        void Unload()
        {
            if (dome != null && !dome.IsDestroyed)
                dome.Kill();
        }
    }
}
