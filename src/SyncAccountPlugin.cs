using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SyncAccount.Models;
using SyncAccount.Services;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.ProtobufDefinitions;
using SwiftlyS2.Shared.Sounds;
using SwiftlyS2.Shared.StringTable;

namespace SyncAccount;

[PluginMetadata(Id = "SyncAccount", Version = "1.0.0", Name = "SyncAccount", Author = "VHMing", Description = "Links player accounts to server identities.")]
public sealed class SyncAccountPlugin(ISwiftlyCore core) : BasePlugin(core)
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _sessions = new();
    private readonly ConcurrentDictionary<int, byte> _blockedGuests = new();
    private readonly ConcurrentDictionary<int, string> _registeredNames = new();
    private readonly ConcurrentDictionary<int, byte> _customAvatarApplied = new();
    private readonly CancellationTokenSource _lifetime = new();
    private PluginConfig _config = new();
    private string _configPath = string.Empty;
    private ApiService? _api;
    private Guid _commandHookGuid;
    private CancellationTokenSource? _nameWatchdog;
    private byte[]? _mockAvatarBytes;

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) { }
    public override void UseSharedInterface(IInterfaceManager interfaceManager) { }

    public override void Load(bool hotReload)
    {
        _configPath = Core.Configuration.GetConfigPath("config.jsonc");
        Core.Configuration
            .InitializeJsonWithModel<PluginConfig>("config.jsonc", "SyncAccount")
            .Configure(builder => builder.AddJsonFile(_configPath, optional: false, reloadOnChange: false));
        _config = Core.Configuration.Manager.GetSection("SyncAccount").Get<PluginConfig>() ?? new PluginConfig();

        _api = new ApiService(() => _config, Core.Logger);
        _commandHookGuid = Core.Command.HookClientCommand(OnClientCommand);
        _nameWatchdog = Core.Scheduler.RepeatBySeconds(0.5f, EnforceAllRegisteredNames);

        _ = FetchAndApplyMockAvatarAsync();

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            ApplyMockAvatar(player);
            QueueSync(player.PlayerID);
        }
    }

    public override void Unload()
    {
        _nameWatchdog?.Cancel();
        _nameWatchdog?.Dispose();
        _nameWatchdog = null;
        if (_commandHookGuid != Guid.Empty)
        {
            Core.Command.UnhookClientCommand(_commandHookGuid);
            _commandHookGuid = Guid.Empty;
        }
        _registeredNames.Clear();
        _blockedGuests.Clear();
        _customAvatarApplied.Clear();
        _lifetime.Cancel();
        _lifetime.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.Cancel();
            session.Dispose();
        }
        _sessions.Clear();
    }

    private HookResult OnClientCommand(int playerId, string commandLine)
    {
        if (_blockedGuests.ContainsKey(playerId))
        {
            return HookResult.Stop;
        }
        return HookResult.Continue;
    }

    [EventListener<EventDelegates.OnClientSteamAuthorize>]
    public void OnClientSteamAuthorize(IOnClientSteamAuthorizeEvent e)
    {
        var authorizedPlayer = Core.PlayerManager.GetPlayer(e.PlayerId);
        if (authorizedPlayer?.IsFakeClient == true)
            return;

        if (authorizedPlayer is { IsValid: true })
            ApplyMockAvatar(authorizedPlayer);

        Core.Scheduler.NextTick(() => WaitForPlayerAndQueueSync(e.PlayerId, 0));
    }

    private void WaitForPlayerAndQueueSync(int playerId, int attempt)
    {
        var player = Core.PlayerManager.GetPlayer(playerId);
        if (player?.IsFakeClient == true)
            return;

        if (player is { IsValid: true })
        {
            ApplyMockAvatar(player);
            QueueSync(playerId);
            return;
        }

        if (attempt >= 12)
        {
            Core.Logger.LogWarning("[SyncAccount] Player {PlayerId} was still unavailable after {Attempts} readiness checks; sync was not started.", playerId, attempt + 1);
            return;
        }

        Core.Scheduler.DelayBySeconds(0.25f, () => WaitForPlayerAndQueueSync(playerId, attempt + 1));
    }

    [EventListener<EventDelegates.OnMapLoad>]
    public void OnMapLoad(IOnMapLoadEvent _)
    {
        _customAvatarApplied.Clear();
        Core.Scheduler.NextTick(() =>
        {
            foreach (var player in Core.PlayerManager.GetAllValidPlayers())
            {
                ApplyMockAvatar(player);
                QueueSync(player.PlayerID);
            }
        });
    }

    [EventListener<EventDelegates.OnMapUnload>]
    public void OnMapUnload(IOnMapUnloadEvent _)
    {
        _registeredNames.Clear();
        _blockedGuests.Clear();
        _customAvatarApplied.Clear();
        foreach (var entry in _sessions.ToArray())
        {
            if (_sessions.TryRemove(entry.Key, out var session))
            {
                session.Cancel();
                session.Dispose();
            }
        }
    }

    [EventListener<EventDelegates.OnClientDisconnected>]
    public void OnClientDisconnected(IOnClientDisconnectedEvent e)
    {
        _registeredNames.TryRemove(e.PlayerId, out _);
        _blockedGuests.TryRemove(e.PlayerId, out _);
        _customAvatarApplied.TryRemove(e.PlayerId, out _);
        if (_sessions.TryRemove(e.PlayerId, out var session))
        {
            session.Cancel();
            session.Dispose();
        }
    }

    private void QueueSync(int playerId)
    {
        var player = Core.PlayerManager.GetPlayer(playerId);
        if (player == null || !player.IsValid || player.IsFakeClient)
            return;

        if (_api == null)
        {
            Core.Logger.LogWarning("[SyncAccount] Sync skipped for player {PlayerId}: plugin is not ready.", playerId);
            return;
        }

        if (_sessions.TryRemove(playerId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        var session = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sessions[playerId] = session;
        _ = SyncPlayerAsync(playerId, player.SteamID, session);
    }

    private async Task SyncPlayerAsync(int playerId, ulong steamId, CancellationTokenSource session)
    {
        var cancellationToken = session.Token;
        try
        {
            var result = await _api!.FetchAsync(steamId, cancellationToken).ConfigureAwait(false);
            if (result.IsRegistered)
            {
                await OnGameThread(() => ApplyRegistered(playerId, result.Name!, result.Avatar, session), cancellationToken).ConfigureAwait(false);
                return;
            }

            await OnGameThread(() => ApplyGuest(playerId, session), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[SyncAccount] Player sync failed for SteamID {SteamId}", steamId);
            try
            {
                await OnGameThread(() => ApplyHealthOffline(playerId, session), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    private bool IsCurrentSession(int playerId, CancellationTokenSource session) =>
        _sessions.TryGetValue(playerId, out var current) && ReferenceEquals(current, session) && !session.IsCancellationRequested;

    private Task OnGameThread(Action action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        Core.Scheduler.NextTick(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }
            try { action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private IPlayer? GetPlayer(int playerId)
    {
        var player = Core.PlayerManager.GetPlayer(playerId);
        return player is { IsValid: true } ? player : null;
    }

    private void ApplyRegistered(int playerId, string name, byte[]? avatar, CancellationTokenSource session)
    {
        var player = GetPlayer(playerId);
        if (player == null || !IsCurrentSession(playerId, session))
            return;

        _blockedGuests.TryRemove(playerId, out _);
        _registeredNames[playerId] = name;
        player.VoiceFlags = VoiceFlagValue.Normal;
        player.Controller.PlayerName = name;
        player.Controller.PlayerNameUpdated();

        if (avatar is { Length: > 0 })
        {
            _customAvatarApplied[playerId] = 1;
            ApplyAvatar(player, avatar);
        }

        BroadcastChat("sync.welcome", name);
        PlaySoundToPlayer(player, _config.SoundSuccess);
    }

    private void ApplyMockAvatar(IPlayer player)
    {
        if (!player.IsValid || player.IsFakeClient) return;
        if (_customAvatarApplied.ContainsKey(player.PlayerID)) return;

        if (_mockAvatarBytes is { Length: > 0 })
        {
            ApplyAvatar(player, _mockAvatarBytes);
        }
        else
        {
            _ = FetchAndApplyMockAvatarAsync(player.PlayerID);
        }
    }

    private async Task FetchAndApplyMockAvatarAsync(int? targetPlayerId = null)
    {
        if (_api == null) return;
        try
        {
            var bytes = await _api.GetMockAvatarAsync(_lifetime.Token).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                _mockAvatarBytes = bytes;
                await OnGameThread(() =>
                {
                    if (targetPlayerId.HasValue)
                    {
                        var player = GetPlayer(targetPlayerId.Value);
                        if (player != null && !_customAvatarApplied.ContainsKey(targetPlayerId.Value))
                            ApplyAvatar(player, bytes);
                    }
                    else
                    {
                        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
                        {
                            if (player is { IsValid: true, IsFakeClient: false } && !_customAvatarApplied.ContainsKey(player.PlayerID))
                                ApplyAvatar(player, bytes);
                        }
                    }
                }, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[SyncAccount] Failed to fetch mock avatar: {Message}", ex.Message);
        }
    }

    private bool ApplyAvatar(IPlayer player, byte[]? avatar)
    {
        if (avatar is not { Length: > 0 }) return false;
        var table = Core.StringTable.FindTable("ServerAvatarOverrides");
        if (table == null)
        {
            Core.Logger.LogWarning("[SyncAccount] ServerAvatarOverrides string table is unavailable for SteamID {SteamId}.", player.SteamID);
            return false;
        }

        table.SetOrAddStringUserData(player.SteamID.ToString(), StringTableUserData.FromRaw(avatar));
        return true;
    }

    private void ApplyGuest(int playerId, CancellationTokenSource session)
    {
        var player = GetPlayer(playerId);
        if (player == null || !IsCurrentSession(playerId, session))
            return;

        _blockedGuests[playerId] = 1;
        player.SwitchTeam(Team.Spectator);
        player.VoiceFlags = VoiceFlagValue.Muted;
        PlaySoundToPlayer(player, _config.SoundFailed);
        SendPlayerChat(player, "sync.register_chat");

        int delay = Math.Max(1, _config.GuestKickDelaySeconds);
        player.SendCenterHTML(Core.Translation.GetPlayerLocalizer(player)["sync.register_html"], delay * 1000);

        int countdownStart = Math.Max(1, delay - 3);
        if (delay > 3)
        {
            Core.Scheduler.DelayBySeconds(countdownStart, () =>
            {
                if (!IsCurrentSession(playerId, session)) return;
                var current = GetPlayer(playerId);
                if (current != null && !current.IsFakeClient)
                    SendPlayerChat(current, "sync.kick_countdown", 3);
            });

            Core.Scheduler.DelayBySeconds(countdownStart + 1, () =>
            {
                if (!IsCurrentSession(playerId, session)) return;
                var current = GetPlayer(playerId);
                if (current != null && !current.IsFakeClient)
                    SendPlayerChat(current, "sync.kick_countdown", 2);
            });

            Core.Scheduler.DelayBySeconds(countdownStart + 2, () =>
            {
                if (!IsCurrentSession(playerId, session)) return;
                var current = GetPlayer(playerId);
                if (current != null && !current.IsFakeClient)
                    SendPlayerChat(current, "sync.kick_countdown", 1);
            });
        }

        Core.Scheduler.DelayBySeconds(delay, () =>
        {
            if (!IsCurrentSession(playerId, session)) return;
            var current = GetPlayer(playerId);
            if (current == null || current.IsFakeClient) return;
            current.Kick("NETWORK_DISCONNECT_REJECT_BADCHALLENGE", ENetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BADCHALLENGE);
            _blockedGuests.TryRemove(playerId, out _);
        });
    }

    private void ApplyHealthOffline(int playerId, CancellationTokenSource session)
    {
        var player = GetPlayer(playerId);
        if (player == null || !IsCurrentSession(playerId, session))
            return;

        _blockedGuests[playerId] = 1;
        player.SwitchTeam(Team.Spectator);
        player.VoiceFlags = VoiceFlagValue.Muted;
        PlaySoundToPlayer(player, _config.SoundFailed);

        SendPlayerChat(player, "sync.health_offline");
        player.SendCenterHTML(Core.Translation.GetPlayerLocalizer(player)["sync.health_offline_html"], 5000);

        Core.Scheduler.DelayBySeconds(5, () =>
        {
            if (!IsCurrentSession(playerId, session)) return;
            var current = GetPlayer(playerId);
            if (current == null || current.IsFakeClient) return;
            current.Kick("NETWORK_DISCONNECT_REJECT_BADCHALLENGE", ENetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BADCHALLENGE);
            _blockedGuests.TryRemove(playerId, out _);
        });
    }

    private void PlaySoundToPlayer(IPlayer player, string soundName)
    {
        if (string.IsNullOrWhiteSpace(soundName) || !player.IsValid || player.IsFakeClient)
            return;

        try
        {
            // 1. Execute play command on client (works directly for .vsnd_c file paths)
            player.ExecuteCommand($"play \"{soundName}\"");

            // 2. Also emit via native SoundEvent (works for registered SoundEvent names)
            using var soundEvent = new SoundEvent(soundName);
            soundEvent.Recipients.RemoveAllPlayers();
            soundEvent.Recipients.AddRecipient(player.PlayerID);
            soundEvent.Emit();
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[SyncAccount] Failed to emit sound '{SoundName}': {Message}", soundName, ex.Message);
        }
    }

    private void SendPlayerChat(IPlayer player, string key, params object[] args)
    {
        if (!player.IsValid || player.IsFakeClient) return;
        var localizer = Core.Translation.GetPlayerLocalizer(player);
        string prefix = localizer["prefix"];
        string message = args.Length > 0 ? localizer[key, args] : localizer[key];
        if (!string.IsNullOrWhiteSpace(prefix) && !string.Equals(prefix, "prefix", StringComparison.OrdinalIgnoreCase))
        {
            player.SendChat($"{prefix}{message}");
        }
        else
        {
            player.SendChat(message);
        }
    }

    private void BroadcastChat(string key, params object[] args)
    {
        foreach (var p in Core.PlayerManager.GetAllValidPlayers())
        {
            if (p.IsFakeClient) continue;
            SendPlayerChat(p, key, args);
        }
    }

    // ── Name Lock ──────────────────────────────────────────────

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true, IsFakeClient: false })
            Core.Scheduler.NextTick(() => EnforcePlayerName(@event.UserId));
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event)
    {
        if (@event.UserIdPlayer is { IsValid: true, IsFakeClient: false })
            Core.Scheduler.NextTick(() => EnforcePlayerName(@event.UserId));
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerChangeName(EventPlayerChangename @event)
    {
        if (!_registeredNames.TryGetValue(@event.UserId, out var targetName))
            return HookResult.Continue;

        if (@event.NewName != targetName)
        {
            Core.Scheduler.NextTick(() => EnforcePlayerName(@event.UserId));
            return HookResult.Stop;
        }
        return HookResult.Continue;
    }

    private void EnforceAllRegisteredNames()
    {
        foreach (var (playerId, _) in _registeredNames)
            EnforcePlayerName(playerId);
    }

    private void EnforcePlayerName(int playerId)
    {
        if (!_registeredNames.TryGetValue(playerId, out var targetName))
            return;

        var player = GetPlayer(playerId);
        if (player is not { IsValid: true } || player.IsFakeClient)
            return;

        if (player.Controller is { IsValid: true } && player.Controller.PlayerName != targetName)
        {
            player.Controller.PlayerName = targetName;
            player.Controller.PlayerNameUpdated();
        }
    }
}
