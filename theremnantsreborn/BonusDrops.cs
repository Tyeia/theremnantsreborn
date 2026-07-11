using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace theremnantsreborn;

public class BonusDropConfig
{
    public string CodePathContains = "";
    public double Chance;
    public string? RequiredTrait; // null = no trait requirement
    public string? DoubleSpecificItem; // null = no specific item requirement
}

public static class BonusDropRegistry
{
    public static List<BonusDropConfig> Configs = new List<BonusDropConfig>();

    public static void Register(string codePathContains, double chance, string? requiredTrait = null)
    {
        Configs.Add(new BonusDropConfig { CodePathContains = codePathContains, Chance = chance, RequiredTrait = requiredTrait });
    }

    public static void Register(string codePathContains, double chance, string? requiredTrait = null, string? doubleSpecificItem = null)
    {
        Configs.Add(new BonusDropConfig { CodePathContains = codePathContains, Chance = chance, RequiredTrait = requiredTrait, DoubleSpecificItem = doubleSpecificItem });
    }

    public static List<BonusDropConfig> GetConfigsFor(Block block)
    {
        var codePath = block?.Code?.Path ?? "";
        return Configs.Where(c => codePath.Contains(c.CodePathContains)).ToList();
    }
}

public class PlacedBlockTracker : ModSystem
{
    private HashSet<string> placedPositions = new HashSet<string>();
    private ICoreServerAPI sapi = null!;
    public static PlacedBlockTracker Instance = null!;

    public override void Start(ICoreAPI api)
    {
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        Instance = this;
        api.Event.SaveGameLoaded += OnSaveGameLoaded;
        api.Event.GameWorldSave += OnGameWorldSave;
        api.Event.DidPlaceBlock += OnDidPlaceBlock;

        var harmony = new Harmony("theremnantsreborn");
        harmony.PatchAll();

        
        // Register bonus drop rules here
        //Woodsman
        BonusDropRegistry.Register("log", 0.10, "forester");
        BonusDropRegistry.Register("leaves", 0.5, "forester", "stick");
        //Serf
        BonusDropRegistry.Register("leaves", 1, "arborist", "treeseed");
        //Fruit Tree Grafts
        //
    }

    private void OnSaveGameLoaded()
    {
        var data = sapi.WorldManager.SaveGame.GetData("theremnantsreborn::placedPositions");
        placedPositions = (data != null && data.Length > 0)
            ? SerializerUtil.Deserialize<HashSet<string>>(data)
            : new HashSet<string>();
    }

    private void OnGameWorldSave()
    {
        sapi.WorldManager.SaveGame.StoreData("theremnantsreborn::placedPositions",
            SerializerUtil.Serialize(placedPositions));
    }

    private void OnDidPlaceBlock(IServerPlayer player, int oldblockid, BlockSelection blockSel, ItemStack byItemStack)
    {
        var block = sapi.World.BlockAccessor.GetBlock(blockSel.Position);
        if (BonusDropRegistry.GetConfigsFor(block).Count == 0) return;

        Instance?.MarkPlaced(blockSel.Position);
    }

    public void MarkPlaced(BlockPos pos) => placedPositions.Add(pos.ToString());
    public bool WasPlayerPlaced(BlockPos pos) => placedPositions.Contains(pos.ToString());
    public void ClearPosition(BlockPos pos) => placedPositions.Remove(pos.ToString());
}

[HarmonyPatch(typeof(Block), "GetDrops")]
public static class Patch_BonusDrops
{
    static readonly Random rand = new Random();

    static void Postfix(Block __instance, IWorldAccessor world, BlockPos pos,
        IPlayer byPlayer, float dropQuantityMultiplier, ref ItemStack[] __result)
    {
        if (__result == null || __result.Length == 0) return;

        var configs = BonusDropRegistry.GetConfigsFor(__instance);
        if (configs.Count == 0) return;

        var tracker = PlacedBlockTracker.Instance;
        bool wasPlayerPlaced = tracker != null && tracker.WasPlayerPlaced(pos);
        tracker?.ClearPosition(pos);
        
        if (byPlayer == null) return;

        foreach(var config in configs)
        {
            if (!string.IsNullOrEmpty(config.RequiredTrait) && !HasTrait(byPlayer, world, config.RequiredTrait))
                continue;

            if (wasPlayerPlaced) continue; // skip bonus drops for player-placed blocks

            if (rand.NextDouble() >= config.Chance) continue;

            if (config.DoubleSpecificItem != null)
            {
                // Find a specific drop (e.g. sticks) within the result set and double just that stack
                for (int i = 0; i < __result.Length; i++)
                {
                    if (__result[i].Collectible?.Code?.Path?.StartsWith(config.DoubleSpecificItem) == true)
                    {
                        __result[i].StackSize *= 2;
                    }
                }
            }
            else
            {
                // Default behavior: append a full extra copy of the primary drop
                var extraStack = __result[0].Clone();
                var newResult = new ItemStack[__result.Length + 1];
                Array.Copy(__result, newResult, __result.Length);
                newResult[__result.Length] = extraStack;
                __result = newResult;
            }
        }
    }

    static bool HasTrait(IPlayer byPlayer, IWorldAccessor world, string traitCode)
    {
        var charSys = world.Api.ModLoader.GetModSystem<CharacterSystem>();
        if (charSys == null) return false;
        if (byPlayer?.Entity?.WatchedAttributes == null) return false;

        string? classCode = byPlayer.Entity?.WatchedAttributes?.GetString("characterClass");
        if (string.IsNullOrEmpty(classCode)) return false;

        var charClass = charSys.characterClasses.Find(c => c.Code == classCode);
        return charClass?.Traits?.Contains(traitCode) ?? false;
    }
}