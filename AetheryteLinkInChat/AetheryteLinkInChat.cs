﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Divination.Common.Api.Dalamud;
using Dalamud.Divination.Common.Api.Ui.Window;
using Dalamud.Divination.Common.Boilerplate;
using Dalamud.Divination.Common.Boilerplate.Features;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Divination.AetheryteLinkInChat.Config;

namespace Divination.AetheryteLinkInChat;

public class AetheryteLinkInChat : DivinationPlugin<AetheryteLinkInChat, PluginConfig>,
    IDalamudPlugin,
    ICommandSupport,
    IConfigWindowSupport<PluginConfig>
{
    private const uint LinkCommandId = 0;
    private const string TeleportGcCommand = "/teleportgc";

    private readonly DalamudLinkPayload linkPayload;
    private readonly AetheryteSolver solver;
    private readonly Teleporter teleporter;

    public AetheryteLinkInChat(DalamudPluginInterface pluginInterface) : base(pluginInterface)
    {
        Config = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        linkPayload = pluginInterface.AddChatLinkHandler(LinkCommandId, HandleLink);
        solver = new AetheryteSolver(Dalamud.DataManager);
        teleporter = new Teleporter(Dalamud.Condition, Dalamud.AetheryteList, Divination.Chat);

        Dalamud.ChatGui.ChatMessage += OnChatReceived;
        Dalamud.CommandManager.AddHandler(TeleportGcCommand,
            new CommandInfo(OnTeleportGcCommand)
            {
                HelpMessage = Localization.TeleportGcHelpMessage,
            });
    }

    public string MainCommandPrefix => "/alic";

    public ConfigWindow<PluginConfig> CreateConfigWindow()
    {
        return new PluginConfigWindow();
    }

    private void OnChatReceived(XivChatType type, uint senderId, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        try
        {
            AppendNearestAetheryteLink(ref message);
        }
        catch (Exception exception)
        {
            DalamudLog.Log.Error(exception, nameof(OnChatReceived));
        }
    }

    private void AppendNearestAetheryteLink(ref SeString message)
    {
        var mapLink = message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (mapLink == default)
        {
            DalamudLog.Log.Verbose("AppendNearestAetheryteLink: mapLink == null");
            return;
        }

        // 最短のテレポート経路を計算する
        var paths = solver.CalculateTeleportPathsForMapLink(mapLink).ToList();
        if (paths.Count == 0)
        {
            DalamudLog.Log.Debug("AppendNearestAetheryteLink: paths.Count == 0");
            return;
        }

        // ワールド間テレポの経路を追加する
        if (Config.ConsiderTeleportsToOtherWorlds)
        {
            solver.AppendGrandCompanyAetheryte(paths,
                (uint)Enum.GetValues<GrandCompanyAetheryte>()[Config.PreferredGrandCompanyAetheryte],
                message,
                Dalamud.ClientState.LocalPlayer?.CurrentWorld.GameData,
                Dalamud.ClientState.TerritoryType);
        }

        foreach (var (index, path) in paths.Select((x, i) => (i, x)))
        {
            switch (path)
            {
                // テレポ可能なエーテライト
                case AetheryteTeleportPath { Aetheryte.IsAetheryte: true } aetheryte:
                    var payloads = new List<Payload>
                    {
                        new IconPayload(BitmapFontIcon.Aetheryte),
                        new UIForegroundPayload(069),
                        linkPayload,
                        new TextPayload(aetheryte.Aetheryte.PlaceName.Value?.Name.RawString),
                        new AetherytePayload(aetheryte.Aetheryte).ToRawPayload(),
                        RawPayload.LinkTerminator,
                        UIForegroundPayload.UIForegroundOff,
                    };
                    payloads.InsertRange(2, SeString.TextArrowPayloads);

                    message = message.Append(payloads);
                    break;
                // 仮設エーテライト・都市内エーテライト
                case AetheryteTeleportPath { Aetheryte.IsAetheryte: false } aetheryte:
                    message = message.Append(new List<Payload>
                    {
                        new IconPayload(BitmapFontIcon.Aethernet),
                        new TextPayload(aetheryte.Aetheryte.AethernetName.Value?.Name.RawString),
                    });
                    break;
                // マップ境界
                case BoundaryTeleportPath boundary:
                    message = message.Append(new List<Payload>
                    {
                        new IconPayload(BitmapFontIcon.FlyZone),
                        new TextPayload(boundary.ConnectedMarker.PlaceNameSubtext.Value?.Name.RawString),
                    });
                    break;
                // ワールド間テレポ
                case WorldTeleportPath world:
                    message = message.Append(new List<Payload>
                    {
                        new IconPayload(BitmapFontIcon.Aetheryte),
                        new UIForegroundPayload(069),
                        linkPayload,
                        new TextPayload(world.Aetheryte.PlaceName.Value?.Name.RawString),
                        new AetherytePayload(world.Aetheryte).ToRawPayload(),
                        RawPayload.LinkTerminator,
                        UIForegroundPayload.UIForegroundOff,
                        new TextPayload($" {SeIconChar.ArrowRight.ToIconString()} "),
                        new IconPayload(BitmapFontIcon.CrossWorld),
                        new TextPayload(world.World.Name.RawString),
                    });
                    break;
            }

            if (index != paths.Count - 1)
            {
                message = message.Append(new TextPayload($" {SeIconChar.ArrowRight.ToIconString()} "));
            }
        }
    }

    private void HandleLink(uint id, SeString link)
    {
        DalamudLog.Log.Verbose("HandleLink: link = {Json}", link.ToJson());

        if (id != LinkCommandId)
        {
            DalamudLog.Log.Debug("HandleLink: id ({Id}) != LinkCommandId", id);
            return;
        }

        var aetheryte = link.Payloads.OfType<RawPayload>().Select(AetherytePayload.Parse).FirstOrDefault(x => x != default);
        if (aetheryte == default)
        {
            DalamudLog.Log.Error("HandleLink: aetheryte ({Text}) == null", link.ToString());
            return;
        }

        teleporter.TeleportToAetheryte(aetheryte);
    }

    private void OnTeleportGcCommand(string command, string arguments)
    {
        var aetheryteId = (uint)Enum.GetValues<GrandCompanyAetheryte>()[Config.PreferredGrandCompanyAetheryte];
        if (aetheryteId == default)
        {
            return;
        }

        var aetheryte = solver.GetAetheryteById(aetheryteId);
        if (aetheryte == default)
        {
            return;
        }

        teleporter.TeleportToAetheryte(aetheryte);
    }

    protected override void ReleaseManaged()
    {
        Dalamud.PluginInterface.SavePluginConfig(Config);
        Dalamud.PluginInterface.RemoveChatLinkHandler();
        Dalamud.ChatGui.ChatMessage -= OnChatReceived;
        Dalamud.CommandManager.RemoveHandler(TeleportGcCommand);
        teleporter.Dispose();
    }
}
