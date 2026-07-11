using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using HarmonyLib;
using System;
using System.Collections.Generic;

namespace theremnantsreborn;

// Tracks positions of logs that were placed by a player (not naturally grown)
public class PlacedLogTracker : ModSystem
{
    private HashSet<string> placedLogPositions = new HashSet<string>();
    private ICoreServerAPI sapi = null!;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        Instance = this;
        api.Event.SaveGameLoaded += OnSaveGameLoaded;
        api.Event.GameWorldSave += OnGameWorldSave;
        api.Event.DidPlaceBlock += OnDidPlaceBlock;

        var harmony = new Harmony("theremnantsreborn");
        harmony.PatchAll();

        var patchedMethods = harmony.GetPatchedMethods();
        foreach (var m in patchedMethods)
        {
            sapi.Logger.Notification($"Patched: {m.DeclaringType}.{m.Name}");
        }
    }

    private void OnSaveGameLoaded()
    {   
        var data = sapi.WorldManager.SaveGame.GetData("theremnantsreborn::placedLogPositions");
        if (data != null && data.Length > 0)
        {
            placedLogPositions = SerializerUtil.Deserialize<HashSet<string>>(data);
        }
        else
        {
            placedLogPositions = new HashSet<string>();
        }
    }

    private void OnGameWorldSave()
    {
        sapi.WorldManager.SaveGame.StoreData("theremnantsreborn::placedLogPositions",
            SerializerUtil.Serialize(placedLogPositions));
    }

    private void OnDidPlaceBlock(IServerPlayer player, int oldblockid, BlockSelection blockSel, ItemStack byItemStack)
    {
        if (!PlacedLogTracker.IsLogBlock(sapi.World.BlockAccessor.GetBlock(blockSel.Position))) return;
        if (sapi.World.Side != EnumAppSide.Server) return;

        PlacedLogTracker.Instance?.MarkPlaced(blockSel.Position);
    }

    public void MarkPlaced(BlockPos pos)
    {
        placedLogPositions.Add(pos.ToString());
    }

    public bool WasPlayerPlaced(BlockPos pos)
    {
        return placedLogPositions.Contains(pos.ToString());
    }

    public void ClearPosition(BlockPos pos)
    {
        placedLogPositions.Remove(pos.ToString());
    }

    // Static accessor so Harmony patches (which run outside normal DI) can reach this instance
    public static PlacedLogTracker Instance = null!;

    public override void Start(ICoreAPI api)
    {
    }

    public static bool IsLogBlock(Block block)
    {
        var codePath = block?.Code?.Path ?? "";
        return codePath.Contains("log");
    }
}

    [HarmonyPatch(typeof(Block), "GetDrops")]
    public static class Patch_LogGetDrops
    {
        static readonly Random rand = new Random();

        static void Postfix(Block __instance, IWorldAccessor world, BlockPos pos,
            IPlayer byPlayer, float dropQuantityMultiplier, ref ItemStack[] __result)
        {
            if (!PlacedLogTracker.IsLogBlock(__instance)) return;
            if (__result == null || __result.Length == 0) return;
            if (byPlayer == null) return; // no player context, skip trait check entirely


            var tracker = PlacedLogTracker.Instance;
            bool wasPlayerPlaced = tracker != null && tracker.WasPlayerPlaced(pos);

            // Clean up tracking regardless of outcome
            tracker?.ClearPosition(pos);

            if (wasPlayerPlaced) return; // only naturally grown logs get the bonus
            if (!HasForesterTrait(byPlayer, world)) return;

            if (rand.NextDouble() < 0.10)
            {
                var extraStack = __result[0].Clone();
                extraStack.StackSize = __result[0].StackSize; // matches one full log drop
                var newResult = new ItemStack[__result.Length + 1];
                Array.Copy(__result, newResult, __result.Length);
                newResult[__result.Length] = extraStack;
                __result = newResult;
            }
        }
        static bool HasForesterTrait(IPlayer byPlayer, IWorldAccessor world)
        {
            var charSys = world.Api.ModLoader.GetModSystem<CharacterSystem>();
            if (charSys == null) return false;
            if (byPlayer?.Entity?.WatchedAttributes == null) return false;

            string? classCode = byPlayer.Entity?.WatchedAttributes?.GetString("characterClass");
            if (string.IsNullOrEmpty(classCode)) return false;

            var charClass = charSys.characterClasses.Find(c => c.Code == classCode);
            if (charClass?.Traits == null) return false;

            return charClass.Traits.Contains("forester");
        }
    }
    
