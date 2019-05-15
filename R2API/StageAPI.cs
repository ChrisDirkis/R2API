using BepInEx.Logging;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using R2API.Utils;
using RoR2;

using System;
using System.Collections.Generic;
using System.Linq;

namespace R2API
{
    /// <summary>
    /// Provides an API to modify spawns of interactibles in the stage.
    /// Users register to the modifySpawnSelectionForStage callback
    /// and modify the InteractableSelections object that's passed through.
    /// 
    /// Example usage:
    /// StageAPI.AddModifier((stage, info) => {
    ///     foreach (var card in info.Regular.choices) {
    ///         card.value.cost /= 2f; // All original cards are around twice as likely to spawn
    ///     }
    /// }, -1000);
    /// 
    /// </summary>
    public static class StageAPI
    {
        public class InteractableSelections
        {
            public List<CardSpawnEntry> Early { get; set; }
            public WeightedSelection<DirectorCard> Regular { get; set; }
            public List<CardSpawnEntry> Late { get; set; }
        }

        public class CardSpawnEntry
        {
            public DirectorCard Card { get; set; }
            public int Limit { get; set; }
        }

        public delegate void SpawnInfoModifier(ClassicStageInfo stage, InteractableSelections inputSpawnInfo);

        public static void AddModifier(SpawnInfoModifier modifier, int priority = 0)
        {
            lock (modifiers)
            {
                var initialPriority = priority;
                while (modifiers.TryGetValue(priority, out _))
                {
                    priority++;
                }
                if (priority == initialPriority)
                {
                    R2API.Logger.LogInfo($"Interactable spawn modifier added at {priority}");
                }
                else
                {
                    R2API.Logger.LogInfo($"Interactable spawn modifier priority collision: Tried to add at {initialPriority}, ended up at {priority}");
                }

                modifiers.Add(priority, modifier);
            }
        }

        public static void RemoveModifier(SpawnInfoModifier modifier)
        {
            lock (modifiers)
            {
                var key = modifiers
                    .Cast<KeyValuePair<int, SpawnInfoModifier>?>()
                    .FirstOrDefault(kvp => kvp.Value.Value == modifier)
                    ?.Key;

                if (!key.HasValue)
                {
                    R2API.Logger.LogWarning($"Failed to remove interactable spawn modifier");
                    return;
                }

                modifiers.Remove(key.Value);
                R2API.Logger.LogInfo($"Removed modifier at {key.Value}");
            }
        }


        private static SortedList<int, SpawnInfoModifier> modifiers = new SortedList<int, SpawnInfoModifier>();

        private static WeightedSelection<DirectorCard> originalSelection;
        private static InteractableSelections spawnInfo;

        internal static void InitHooks()
        {
            IL.RoR2.SceneDirector.PopulateScene += (il) =>
            {
                ILCursor cursor = new ILCursor(il);

                // after "Run.instance.OnPlayerSpawnPointsPlaced(this);"
                cursor.GotoNext(MoveType.After, i => i.MatchCallvirt<Run>(nameof(Run.OnPlayerSpawnPointsPlaced)));

                // PopulateEarly(this);
                cursor.Emit(OpCodes.Ldarg_0); // push "this"
                cursor.EmitDelegate<Action<SceneDirector>>(PopulateEarly);

                // after if (...) { monsterCredit = 0; }
                cursor.GotoNext(MoveType.After, i => i.MatchStfld<SceneDirector>("monsterCredit"));
                if (!cursor.IncomingLabels.Any()) //extra check that we're actually at the end of the if body
                    throw new Exception("This isn't what I was looking for");

                cursor.MoveAfterLabels();

                // PopulateLate(this);
                cursor.Emit(OpCodes.Ldarg_0); // push "this"
                cursor.EmitDelegate<Action<SceneDirector>>(PopulateLate);
            };

            SceneDirector.onPrePopulateSceneServer += OnPrePopulateScene;
            SceneDirector.onPostPopulateSceneServer += OnPostPopulateScene;
        }

        private static void OnPrePopulateScene(SceneDirector director)
        {
            var stageInfo = StageInfo;
            originalSelection = stageInfo.interactableSelection;

            spawnInfo = new InteractableSelections()
            {
                Early = new List<CardSpawnEntry>(),
                Regular = originalSelection.Clone(),
                Late = new List<CardSpawnEntry>(),
            };

            foreach (var modifier in modifiers.Values)
            {
                modifier(stageInfo, spawnInfo);
            }

            stageInfo.interactableSelection = spawnInfo.Regular;
        }

        private static void OnPostPopulateScene(SceneDirector director) => StageInfo.interactableSelection = originalSelection;

        private static void PopulateEarly(SceneDirector director) => Populate(director, spawnInfo.Early);

        private static void PopulateLate(SceneDirector director) => Populate(director, spawnInfo.Late);

        // Almost identical to the source, sans the interactableCredit
        private static void Populate(SceneDirector director, List<CardSpawnEntry> toSpawn) {
            R2API.Logger.LogInfo("Populating unbudgeted cards");
            var placementRule = new DirectorPlacementRule();
            placementRule.placementMode = DirectorPlacementRule.PlacementMode.Random;

            foreach (var spawnEntry in toSpawn)
            {
                if (spawnEntry.Card == null || !spawnEntry.Card.CardIsValid())
                {
                    continue;
                }
                foreach (var _ in Enumerable.Range(0, spawnEntry.Limit))
                {
                    var i = 0;
                    while (i < 10)
                    {
                        if (TrySpawnCard(director, spawnEntry.Card, placementRule))
                            break;
                        else
                            i++;
                    }
                }
            }
        }

        private static bool TrySpawnCard(SceneDirector director, DirectorCard card, DirectorPlacementRule rule)
        {
            var spawnedObject = director.directorCore.TrySpawnObject(card, rule, director.rng);
            if (spawnedObject)
            {
                var purchaseInteraction = spawnedObject.GetComponent<PurchaseInteraction>();
                if (purchaseInteraction && purchaseInteraction.costType == CostType.Money)
                {
                    purchaseInteraction.Networkcost = Run.instance.GetDifficultyScaledCost(purchaseInteraction.cost);
                }
            }
            return spawnedObject;
        }

        private static ClassicStageInfo StageInfo => SceneInfo.instance.GetComponent<ClassicStageInfo>();

        private static WeightedSelection<DirectorCard> Clone(this WeightedSelection<DirectorCard> original)
        {
            var clone = new WeightedSelection<DirectorCard>();
            foreach (var choice in original.choices)
            {
                clone.AddChoice(choice.value.Clone(), choice.weight);
            }
            return clone;
        }

        private static DirectorCard Clone(this DirectorCard original)
        {
            return new DirectorCard()
            {
                allowAmbushSpawn = original.allowAmbushSpawn,
                cost = original.cost,
                forbiddenUnlockable = original.forbiddenUnlockable,
                minimumStageCompletions = original.minimumStageCompletions,
                preventOverhead = original.preventOverhead,
                requiredUnlockable = original.requiredUnlockable,
                selectionWeight = original.selectionWeight,
                spawnCard = original.spawnCard,
                spawnDistance = original.spawnDistance,
            };
            
        }
    }
}
