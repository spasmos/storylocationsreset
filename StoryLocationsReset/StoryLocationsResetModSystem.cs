using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using System.Collections;
using System.Reflection;

namespace StoryLocationsReset;

public class StoryLocationsResetModSystem : ModSystem
{
    private const string ConfigFileName = "storylocationsreset.json";
    private const string StateFileName = "storylocationsreset.state.json";
    private const int ChunkSize = 32;
    private const int GeneralChatGroupId = 0;

    private ICoreServerAPI? sapi;
    private StoryLocationsResetConfig config = StoryLocationsResetConfig.CreateDefault();
    private StoryLocationsResetState state = new();
    private readonly List<StoryLocationEntry> cachedLocations = new();
    private readonly Queue<QueuedStoryReset> queuedResets = new();
    private bool resetQueueRunning;
    private bool waitingForWgenRegen;
    private int activeResetSequence;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        LoadConfig();
        LoadState();
        RegisterCommands(api);
        api.Event.RegisterEventBusListener(OnEventBus, 0.0, "wgenregendone");
        api.Event.PlayerJoin += OnPlayerJoin;

        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            if (!config.Enabled || !config.RunOnServerStart)
            {
                return;
            }

            int delayMs = Math.Max(1, config.ServerStartDelaySeconds) * 1000;
            api.Event.RegisterCallback(_ => ResetEnabledLocations("server-start"), delayMs);
        });
    }

    public override void Dispose()
    {
        if (sapi != null)
        {
            sapi.Event.UnregisterEventBusListener(OnEventBus);
            sapi.Event.PlayerJoin -= OnPlayerJoin;
        }
    }

    private void LoadConfig()
    {
        if (sapi == null)
        {
            return;
        }

        config = sapi.LoadModConfig<StoryLocationsResetConfig>(ConfigFileName) ?? StoryLocationsResetConfig.CreateDefault();
        config.EnsureDefaults();
        sapi.StoreModConfig(config, ConfigFileName);
    }

    private void LoadState()
    {
        if (sapi == null)
        {
            return;
        }

        state = sapi.LoadModConfig<StoryLocationsResetState>(StateFileName) ?? new StoryLocationsResetState();
        PruneRecentResetAreas();
        SaveState();
    }

    private void SaveState()
    {
        sapi?.StoreModConfig(state, StateFileName);
    }

    private void RegisterCommands(ICoreServerAPI api)
    {
        CommandArgumentParsers parsers = api.ChatCommands.Parsers;

        api.ChatCommands.Create("storyreset")
            .WithDescription("Reset Vintage Story story locations")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("reload")
                .WithDescription("Reload storylocationsreset.json")
                .HandleWith(OnReloadCommand)
                .EndSubCommand()
            .BeginSubCommand("scan")
                .WithDescription("Scan story locations and refresh the in-memory cache")
                .HandleWith(OnScanCommand)
                .EndSubCommand()
            .BeginSubCommand("list")
                .WithDescription("List configured story locations")
                .HandleWith(OnListCommand)
                .EndSubCommand()
            .BeginSubCommand("reset")
                .WithDescription("Reset one configured story location code, or all enabled locations")
                .WithArgs(parsers.OptionalWord("code-or-all"))
                .HandleWith(OnResetCommand)
                .EndSubCommand();
    }

    private TextCommandResult OnReloadCommand(TextCommandCallingArgs args)
    {
        LoadConfig();
        return TextCommandResult.Success("storylocationsreset config reloaded.");
    }

    private TextCommandResult OnScanCommand(TextCommandCallingArgs args)
    {
        int count = ScanStoryLocations();
        return TextCommandResult.Success($"Found {count} configured story location instance(s).");
    }

    private TextCommandResult OnListCommand(TextCommandCallingArgs args)
    {
        ScanStoryLocations();

        if (cachedLocations.Count == 0)
        {
            return TextCommandResult.Success("No configured story locations were found.");
        }

        string lines = string.Join(
            "\n",
            cachedLocations
                .OrderBy(location => location.Code)
                .ThenBy(location => location.Center.X)
                .ThenBy(location => location.Center.Z)
                .Select(location =>
                {
                    string source = location.FoundInGeneratedStructure
                        ? "registered+generated-region"
                        : "registered-only";

                    return $"{location.Code}: {location.Center.X}, {location.Center.Y}, {location.Center.Z} ({source})";
                }));

        return TextCommandResult.Success(lines);
    }

    private TextCommandResult OnResetCommand(TextCommandCallingArgs args)
    {
        string requestedCode = args[0] as string ?? "all";
        ResetResult result = ResetLocations(requestedCode, "manual-command");

        return TextCommandResult.Success(
            $"Story reset queued. Queued: {result.Reset}. Skipped: {result.Skipped}. Not found: {result.NotFound}.");
    }

    private void ResetEnabledLocations(string reason)
    {
        ResetResult result = ResetLocations("all", reason);
        sapi?.Logger.Notification(
            "[storylocationsreset] Automatic reset queued. Queued: {0}. Skipped: {1}. Not found: {2}.",
            result.Reset,
            result.Skipped,
            result.NotFound);
    }

    private ResetResult ResetLocations(string requestedCode, string reason)
    {
        ResetResult result = new();

        if (sapi == null || !config.Enabled)
        {
            return result;
        }

        ScanStoryLocations();

        IEnumerable<KeyValuePair<string, StoryLocationResetOptions>> selectedLocations =
            requestedCode.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? config.Locations.Where(pair => pair.Value.Enabled)
                : config.Locations.Where(pair => pair.Key.Equals(requestedCode, StringComparison.OrdinalIgnoreCase));

        foreach ((string code, StoryLocationResetOptions options) in selectedLocations)
        {
            if (options.MaxInstancesToReset <= 0)
            {
                result.Skipped++;
                sapi.Logger.Notification(
                    "[storylocationsreset] Skipping '{0}': maxInstancesToReset is {1}.",
                    code,
                    options.MaxInstancesToReset);
                continue;
            }

            List<StoryLocationEntry> matches = cachedLocations
                .Where(location => location.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
                .Take(options.MaxInstancesToReset)
                .ToList();

            if (matches.Count == 0)
            {
                result.NotFound++;
                sapi.Logger.Notification("[storylocationsreset] No generated story location found for code '{0}'.", code);
                continue;
            }

            foreach (StoryLocationEntry location in matches)
            {
                if (reason.Equals("server-start", StringComparison.OrdinalIgnoreCase)
                    && !config.DebugAllowServerStartForUngeneratedLocations
                    && !location.FoundInGeneratedStructure)
                {
                    result.Skipped++;
                    sapi.Logger.Notification(
                        "[storylocationsreset] Skipping '{0}' at {1}/{2}/{3}: registered story location is not present in generated map regions.",
                        location.Code,
                        location.Center.X,
                        location.Center.Y,
                        location.Center.Z);
                    continue;
                }

                if (reason.Equals("server-start", StringComparison.OrdinalIgnoreCase)
                    && !config.DebugAllowServerStartForUngeneratedLocations)
                {
                    EnqueueResetAfterChunkExistenceCheck(location, reason);
                }
                else
                {
                    EnqueueReset(location, reason);
                }

                result.Reset++;
            }
        }

        return result;
    }

    private void EnqueueResetAfterChunkExistenceCheck(StoryLocationEntry location, string reason)
    {
        if (sapi == null)
        {
            return;
        }

        ChunkRange range = GetResetChunkRange(location);
        List<Vec2i> chunks = GetChunkExistenceChecks(range);
        object sync = new();
        int pending = chunks.Count;
        bool anyExist = false;

        foreach (Vec2i chunk in chunks)
        {
            sapi.WorldManager.TestMapChunkExists(chunk.X, chunk.Y, exists =>
            {
                bool completed;

                lock (sync)
                {
                    anyExist |= exists;
                    pending--;
                    completed = pending == 0;
                }

                if (!completed || sapi == null)
                {
                    return;
                }

                sapi.Event.EnqueueMainThreadTask(() =>
                {
                    if (!anyExist)
                    {
                        sapi.Logger.Notification(
                            "[storylocationsreset] Skipping '{0}' at {1}/{2}/{3}: reset area is known but none of its chunk columns are generated yet.",
                            location.Code,
                            location.Center.X,
                            location.Center.Y,
                            location.Center.Z);
                        return;
                    }

                    EnqueueReset(location, reason);
                }, "storylocationsreset-existing-chunks");
            });
        }
    }

    private static List<Vec2i> GetChunkExistenceChecks(ChunkRange range)
    {
        List<Vec2i> chunks = new();

        for (int chunkX = range.MinChunkX; chunkX <= range.MaxChunkX; chunkX++)
        {
            for (int chunkZ = range.MinChunkZ; chunkZ <= range.MaxChunkZ; chunkZ++)
            {
                chunks.Add(new Vec2i(chunkX, chunkZ));
            }
        }

        return chunks;
    }

    private void EnqueueReset(StoryLocationEntry location, string reason)
    {
        queuedResets.Enqueue(new QueuedStoryReset(location, reason));

        if (resetQueueRunning)
        {
            sapi?.Logger.Notification(
                "[storylocationsreset] Queued '{0}'. Pending reset operations: {1}.",
                location.Code,
                queuedResets.Count);
            return;
        }

        resetQueueRunning = true;
        sapi?.Logger.Notification("[storylocationsreset] Starting queued story reset operations.");
        ProcessNextQueuedReset();
    }

    private void ProcessNextQueuedReset(float dt = 0)
    {
        if (sapi == null || waitingForWgenRegen)
        {
            return;
        }

        while (queuedResets.Count > 0)
        {
            QueuedStoryReset queuedReset = queuedResets.Dequeue();

            if (!ExecuteReset(queuedReset.Location, queuedReset.Reason))
            {
                continue;
            }

            waitingForWgenRegen = true;
            int sequence = ++activeResetSequence;
            int timeoutMs = Math.Max(5, config.RegenCompletionTimeoutSeconds) * 1000;
            sapi.Event.RegisterCallback(_ => OnRegenTimeout(sequence), timeoutMs);
            return;
        }

        resetQueueRunning = false;
        sapi.Logger.Notification("[storylocationsreset] Queued story reset operations finished.");
    }

    private void OnEventBus(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!eventName.Equals("wgenregendone", StringComparison.OrdinalIgnoreCase) || sapi == null)
        {
            return;
        }

        sapi.Event.EnqueueMainThreadTask(OnWgenRegenDone, "storylocationsreset-wgen-done");
    }

    private void OnWgenRegenDone()
    {
        if (sapi == null || !waitingForWgenRegen)
        {
            return;
        }

        waitingForWgenRegen = false;
        sapi.Logger.Notification(
            "[storylocationsreset] Vanilla worldgen reported regen complete. Pending reset operations: {0}.",
            queuedResets.Count);

        ScheduleNextQueuedReset();
    }

    private void OnRegenTimeout(int sequence)
    {
        if (sapi == null || sequence != activeResetSequence || !waitingForWgenRegen)
        {
            return;
        }

        waitingForWgenRegen = false;
        sapi.Logger.Warning(
            "[storylocationsreset] Timed out waiting for vanilla wgenregendone after {0} seconds. Continuing queued resets conservatively.",
            config.RegenCompletionTimeoutSeconds);

        ScheduleNextQueuedReset();
    }

    private void ScheduleNextQueuedReset()
    {
        if (sapi == null)
        {
            return;
        }

        if (queuedResets.Count == 0)
        {
            ProcessNextQueuedReset();
            return;
        }

        int cooldownMs = Math.Max(1, config.ResetQueueCooldownSeconds) * 1000;
        sapi.Event.RegisterCallback(ProcessNextQueuedReset, cooldownMs);
    }

    private int ScanStoryLocations()
    {
        if (sapi == null)
        {
            return 0;
        }

        cachedLocations.Clear();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        ScanRegisteredStoryStructureLocations(seen);
        ScanGeneratedStructures(seen);

        return cachedLocations.Count;
    }

    private void ScanRegisteredStoryStructureLocations(HashSet<string> seen)
    {
        if (sapi == null)
        {
            return;
        }

        object? storySystem = sapi.ModLoader.GetModSystem("Vintagestory.GameContent.GenStoryStructures");
        object? structures = GetMemberValue(storySystem, "Structures");

        if (structures is not IEnumerable enumerable)
        {
            return;
        }

        foreach (object entry in enumerable)
        {
            object? locationEntry = GetMemberValue(entry, "Value") ?? entry;
            string? code = GetMemberValue(entry, "Key") as string
                ?? GetMemberValue(locationEntry, "Code") as string;
            Cuboidi? location = GetMemberValue(locationEntry, "Location") as Cuboidi;

            AddStoryLocation(code, location, seen, foundInGeneratedStructure: false);
        }
    }

    private void ScanGeneratedStructures(HashSet<string> seen)
    {
        if (sapi == null)
        {
            return;
        }

        BlockPos min = new(0, 0, 0);
        BlockPos max = new(
            sapi.WorldManager.MapSizeX - 1,
            sapi.WorldManager.MapSizeY - 1,
            sapi.WorldManager.MapSizeZ - 1);

        sapi.World.BlockAccessor.WalkStructures(min, max, structure =>
        {
            if (structure.Code == null || !config.Locations.ContainsKey(structure.Code))
            {
                return;
            }

            AddStoryLocation(structure.Code, structure.Location, seen, foundInGeneratedStructure: true);
        });
    }

    private bool AddStoryLocation(string? code, Cuboidi? location, HashSet<string> seen, bool foundInGeneratedStructure)
    {
        if (code == null || location == null || !config.Locations.ContainsKey(code))
        {
            return false;
        }

        string key = $"{code}:{location.X1}:{location.Y1}:{location.Z1}";

        if (!seen.Add(key))
        {
            if (foundInGeneratedStructure)
            {
                int index = cachedLocations.FindIndex(entry =>
                    entry.Code.Equals(code, StringComparison.OrdinalIgnoreCase)
                    && entry.Location.X1 == location.X1
                    && entry.Location.Y1 == location.Y1
                    && entry.Location.Z1 == location.Z1);

                if (index >= 0)
                {
                    cachedLocations[index] = cachedLocations[index] with { FoundInGeneratedStructure = true };
                }
            }

            return false;
        }

        cachedLocations.Add(new StoryLocationEntry(code, location.Center, location.Clone(), foundInGeneratedStructure));
        return true;
    }

    private static object? GetMemberValue(object? instance, string memberName)
    {
        if (instance == null)
        {
            return null;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = instance.GetType();

        return type.GetProperty(memberName, Flags)?.GetValue(instance)
            ?? type.GetField(memberName, Flags)?.GetValue(instance);
    }

    private bool ExecuteReset(StoryLocationEntry location, string reason)
    {
        if (sapi == null)
        {
            return false;
        }

        ChunkRange range = GetResetChunkRange(location);

        if (config.SkipIfPlayersNearby && HasPlayersNearby(location.Center, config.PlayerSafetyRadius))
        {
            sapi.Logger.Notification(
                "[storylocationsreset] Skipping '{0}' at {1}/{2}/{3}: player nearby.",
                location.Code,
                location.Center.X,
                location.Center.Y,
                location.Center.Z);
            return false;
        }

        if (!HandlePlayersInsideResetArea(location, range))
        {
            return false;
        }

        sapi.Logger.Notification(
            "[storylocationsreset] Resetting '{0}' at {1}/{2}/{3} using chunk range {4}/{5} -> {6}/{7}. Reason: {8}",
            location.Code,
            location.Center.X,
            location.Center.Y,
            location.Center.Z,
            range.MinChunkX,
            range.MinChunkZ,
            range.MaxChunkX,
            range.MaxChunkZ,
            reason);

        ExecuteServerCommand($"/wgen story removeschematiccount {location.Code}");
        ExecuteServerCommand($"/wgen regenrange {range.MinChunkX} {range.MinChunkZ} {range.MaxChunkX} {range.MaxChunkZ}");
        RememberRecentResetArea(location, range);

        return true;
    }

    private void ExecuteServerCommand(string command)
    {
        if (sapi == null)
        {
            return;
        }

        sapi.ChatCommands.ExecuteUnparsed(command, new TextCommandCallingArgs
        {
            Caller = new Caller
            {
                Type = EnumCallerType.Console,
                CallerPrivileges = new[] { "*" },
                FromChatGroupId = 0
            }
        }, result =>
        {
            if (result.Status == EnumCommandStatus.Success)
            {
                sapi.Logger.Notification("[storylocationsreset] Executed command: {0}", command);
                return;
            }

            sapi.Logger.Warning(
                "[storylocationsreset] Command failed: {0}. Status: {1}. Message: {2}.",
                command,
                result.Status,
                result.StatusMessage);
        });
    }

    private bool HasPlayersNearby(Vec3i center, int radius)
    {
        if (sapi == null || radius <= 0)
        {
            return false;
        }

        double radiusSq = radius * radius;

        foreach (IPlayer player in sapi.World.AllOnlinePlayers)
        {
            if (player.Entity == null)
            {
                continue;
            }

            double dx = player.Entity.Pos.X - center.X;
            double dz = player.Entity.Pos.Z - center.Z;

            if (dx * dx + dz * dz <= radiusSq)
            {
                return true;
            }
        }

        return false;
    }

    private ChunkRange GetResetChunkRange(StoryLocationEntry location)
    {
        int maxChunkX = Math.Max(0, sapi!.WorldManager.MapSizeX / ChunkSize - 1);
        int maxChunkZ = Math.Max(0, sapi.WorldManager.MapSizeZ / ChunkSize - 1);

        int minX = (int)Math.Floor(location.Location.X1 / (double)ChunkSize);
        int maxX = (int)Math.Floor(location.Location.X2 / (double)ChunkSize);
        int minZ = (int)Math.Floor(location.Location.Z1 / (double)ChunkSize);
        int maxZ = (int)Math.Floor(location.Location.Z2 / (double)ChunkSize);

        return new ChunkRange(
            Math.Clamp(minX, 0, maxChunkX),
            Math.Clamp(minZ, 0, maxChunkZ),
            Math.Clamp(maxX, 0, maxChunkX),
            Math.Clamp(maxZ, 0, maxChunkZ));
    }

    private bool HandlePlayersInsideResetArea(StoryLocationEntry location, ChunkRange range)
    {
        if (sapi == null)
        {
            return false;
        }

        List<IServerPlayer> playersInside = GetPlayersInsideResetArea(range).ToList();

        if (playersInside.Count == 0)
        {
            return true;
        }

        if (!config.EvacuatePlayersInsideResetAreaToSpawn)
        {
            sapi.Logger.Notification(
                "[storylocationsreset] Skipping '{0}' at {1}/{2}/{3}: {4} player(s) inside reset area.",
                location.Code,
                location.Center.X,
                location.Center.Y,
                location.Center.Z,
                playersInside.Count);
            return false;
        }

        foreach (IServerPlayer player in playersInside)
        {
            EntityPos evacuationSpawn = GetSafeEvacuationSpawn(player, range);
            player.Entity.TeleportTo(evacuationSpawn);
            player.SendMessage(
                GeneralChatGroupId,
                "You were moved to spawn because a story location reset is running.",
                EnumChatType.Notification);
        }

        sapi.Logger.Notification(
            "[storylocationsreset] Moved {0} player(s) to spawn before resetting '{1}'.",
            playersInside.Count,
            location.Code);

        return true;
    }

    private EntityPos GetSafeEvacuationSpawn(IServerPlayer player, ChunkRange range)
    {
        FuzzyEntityPos playerSpawn = player.GetSpawnPosition(consumeSpawnUse: false);

        if (playerSpawn == null)
        {
            return GetWorldSpawnPosition();
        }

        if (!range.Contains(playerSpawn.X, playerSpawn.Z))
        {
            return playerSpawn;
        }

        player.ClearSpawnPosition();
        player.SendMessage(
            GeneralChatGroupId,
            "Your spawn was inside a story reset area and has been cleared.",
            EnumChatType.Notification);
        sapi?.Logger.Notification(
            "[storylocationsreset] Cleared player spawn override for '{0}' because their resolved spawn was inside the reset area.",
            player.PlayerName);

        return GetWorldSpawnPosition();
    }

    private EntityPos GetWorldSpawnPosition()
    {
        return sapi!.World.DefaultSpawnPosition;
    }

    private IEnumerable<IServerPlayer> GetPlayersInsideResetArea(ChunkRange range)
    {
        if (sapi == null)
        {
            yield break;
        }

        foreach (IPlayer player in sapi.World.AllOnlinePlayers)
        {
            if (player is not IServerPlayer serverPlayer || player.Entity == null)
            {
                continue;
            }

            double x = player.Entity.Pos.X;
            double z = player.Entity.Pos.Z;

            if (range.Contains(x, z))
            {
                yield return serverPlayer;
            }
        }
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        if (sapi == null || !config.EvacuatePlayersOnJoinFromRecentlyResetAreas || player.Entity == null)
        {
            return;
        }

        PruneRecentResetAreas();

        RecentResetArea? matchingArea = state.RecentResetAreas.FirstOrDefault(area =>
            new ChunkRange(area.MinChunkX, area.MinChunkZ, area.MaxChunkX, area.MaxChunkZ)
                .Contains(player.Entity.Pos.X, player.Entity.Pos.Z));

        if (matchingArea == null)
        {
            return;
        }

        ChunkRange range = new(
            matchingArea.MinChunkX,
            matchingArea.MinChunkZ,
            matchingArea.MaxChunkX,
            matchingArea.MaxChunkZ);

        EntityPos evacuationSpawn = GetSafeEvacuationSpawn(player, range);
        player.Entity.TeleportTo(evacuationSpawn);
        player.SendMessage(
            GeneralChatGroupId,
            "You were moved to spawn because your last position was inside a recently reset story location.",
            EnumChatType.Notification);

        sapi.Logger.Notification(
            "[storylocationsreset] Evacuated joining player '{0}' from recently reset area '{1}'.",
            player.PlayerName,
            matchingArea.Code);
    }

    private void RememberRecentResetArea(StoryLocationEntry location, ChunkRange range)
    {
        if (sapi == null || !config.EvacuatePlayersOnJoinFromRecentlyResetAreas)
        {
            return;
        }

        PruneRecentResetAreas();

        state.RecentResetAreas.Add(new RecentResetArea
        {
            Code = location.Code,
            MinChunkX = range.MinChunkX,
            MinChunkZ = range.MinChunkZ,
            MaxChunkX = range.MaxChunkX,
            MaxChunkZ = range.MaxChunkZ,
            ResetAtUtc = DateTime.UtcNow
        });

        SaveState();
    }

    private void PruneRecentResetAreas()
    {
        if (config.RecentResetAreaRetentionHours <= 0)
        {
            state.RecentResetAreas.Clear();
            return;
        }

        DateTime cutoff = DateTime.UtcNow.AddHours(-config.RecentResetAreaRetentionHours);
        state.RecentResetAreas.RemoveAll(area => area.ResetAtUtc < cutoff);
    }
}

public class StoryLocationsResetConfig
{
    public bool Enabled { get; set; } = true;

    public bool RunOnServerStart { get; set; }

    public int ServerStartDelaySeconds { get; set; } = 15;

    public bool DebugAllowServerStartForUngeneratedLocations { get; set; }

    public int ResetQueueCooldownSeconds { get; set; } = 5;

    public int RegenCompletionTimeoutSeconds { get; set; } = 300;

    public bool SkipIfPlayersNearby { get; set; } = true;

    public int PlayerSafetyRadius { get; set; } = 256;

    public bool EvacuatePlayersInsideResetAreaToSpawn { get; set; }

    public bool EvacuatePlayersOnJoinFromRecentlyResetAreas { get; set; } = true;

    public int RecentResetAreaRetentionHours { get; set; } = 24;

    public Dictionary<string, StoryLocationResetOptions> Locations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static StoryLocationsResetConfig CreateDefault()
    {
        return new StoryLocationsResetConfig
        {
            Locations = new Dictionary<string, StoryLocationResetOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["resonancearchive"] = new() { Enabled = true },
                ["devastationarea"] = new() { Enabled = false },
                ["lazaret"] = new() { Enabled = false },
                ["tobiascave"] = new() { Enabled = false },
                ["treasurehunter"] = new() { Enabled = false, MaxInstancesToReset = 0 },
                ["village"] = new() { Enabled = false }
            }
        };
    }

    public void EnsureDefaults()
    {
        StoryLocationsResetConfig defaults = CreateDefault();

        foreach ((string code, StoryLocationResetOptions options) in defaults.Locations)
        {
            Locations.TryAdd(code, options);
        }

        ServerStartDelaySeconds = Math.Max(1, ServerStartDelaySeconds);
        ResetQueueCooldownSeconds = Math.Max(1, ResetQueueCooldownSeconds);
        RegenCompletionTimeoutSeconds = Math.Max(5, RegenCompletionTimeoutSeconds);
        PlayerSafetyRadius = Math.Max(0, PlayerSafetyRadius);
        RecentResetAreaRetentionHours = Math.Max(0, RecentResetAreaRetentionHours);
    }
}

public class StoryLocationResetOptions
{
    public bool Enabled { get; set; }

    public int MaxInstancesToReset { get; set; } = 1;
}

internal sealed record StoryLocationEntry(string Code, Vec3i Center, Cuboidi Location, bool FoundInGeneratedStructure);

internal sealed record QueuedStoryReset(StoryLocationEntry Location, string Reason);

internal sealed record ChunkRange(int MinChunkX, int MinChunkZ, int MaxChunkX, int MaxChunkZ)
{
    private const int ChunkSize = 32;

    public bool Contains(double x, double z)
    {
        int minX = MinChunkX * ChunkSize;
        int maxX = (MaxChunkX + 1) * ChunkSize - 1;
        int minZ = MinChunkZ * ChunkSize;
        int maxZ = (MaxChunkZ + 1) * ChunkSize - 1;

        return x >= minX && x <= maxX && z >= minZ && z <= maxZ;
    }
}

internal sealed class ResetResult
{
    public int Reset { get; set; }

    public int Skipped { get; set; }

    public int NotFound { get; set; }
}

public class StoryLocationsResetState
{
    public List<RecentResetArea> RecentResetAreas { get; set; } = new();
}

public class RecentResetArea
{
    public string Code { get; set; } = "";

    public int MinChunkX { get; set; }

    public int MinChunkZ { get; set; }

    public int MaxChunkX { get; set; }

    public int MaxChunkZ { get; set; }

    public DateTime ResetAtUtc { get; set; }
}
