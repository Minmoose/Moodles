﻿using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using ECommons;
using ECommons.EzEventManager;
using ECommons.GameHelpers;
using ECommons.Interop;
using ECommons.MathHelpers;
using ECommons.PartyFunctions;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Moodles.Data;
using Moodles.GameGuiProcessors;

namespace Moodles.Processors;
public unsafe class CommonProcessor : IDisposable
{
    public PartyListProcessor PartyListProcessor;
    public StatusCustomProcessor StatusCustomProcessor;
    public TargetInfoProcessor TargetInfoProcessor;
    public FocusTargetInfoProcessor FocusTargetInfoProcessor;
    public StatusProcessor StatusProcessor;
    public TargetInfoBuffDebuffProcessor TargetInfoBuffDebuffProcessor;
    public FlyPopupTextProcessor FlyPopupTextProcessor;
    public readonly HashSet<uint> NegativeStatuses = [];
    public readonly HashSet<uint> PositiveStatuses = [];
    public readonly HashSet<uint> SpecialStatuses = [];
    public readonly HashSet<uint> DispelableIcons = [];
    public readonly Dictionary<uint, uint> IconStackCounts = [];
    public nint HoveringOver = 0;
    public List<nint> CancelRequests = [];
    public bool WasRightMousePressed = false;
    public bool NewMethod = true;
    private nint TooltipMemory;

    public List<(MyStatusManager StatusManager, MyStatus Status)> CleanupQueue = [];

    public CommonProcessor()
    {
        foreach (var x in Svc.Data.GetExcelSheet<Status>())
        {
            if (IconStackCounts.TryGetValue(x.Icon, out var count))
            {
                if (count < x.MaxStacks)
                {
                    IconStackCounts[x.Icon] = x.MaxStacks;
                }
            }
            else
            {
                IconStackCounts[x.Icon] = x.MaxStacks;
            }
            if (NegativeStatuses.Contains(x.RowId) || PositiveStatuses.Contains(x.RowId) || SpecialStatuses.Contains(x.RowId)) continue;
            if (x.CanIncreaseRewards == 1)
            {
                SpecialStatuses.Add(x.RowId);
            }
            else if (x.StatusCategory == 1)
            {
                PositiveStatuses.Add(x.RowId);
            }
            else if (x.StatusCategory == 2)
            {
                NegativeStatuses.Add(x.RowId);
                DispelableIcons.Add(x.Icon);
                for (var i = 1; i < x.MaxStacks; i++)
                {
                    DispelableIcons.Add((uint)(x.Icon + i));
                }
            }
        }
        new EzFrameworkUpdate(Tick);
        PartyListProcessor = new();
        StatusCustomProcessor = new();
        TargetInfoProcessor = new();
        FocusTargetInfoProcessor = new();
        StatusProcessor = new();
        TargetInfoBuffDebuffProcessor = new();
        TooltipMemory = Marshal.AllocHGlobal(2 * 1024);
        FlyPopupTextProcessor = new();
    }

    public void Dispose()
    {
        HideAll();
        PartyListProcessor.Dispose();
        StatusCustomProcessor.Dispose();
        TargetInfoProcessor.Dispose();
        FocusTargetInfoProcessor.Dispose();
        StatusProcessor.Dispose();
        TargetInfoBuffDebuffProcessor.Dispose();
        FlyPopupTextProcessor.Dispose();
        Marshal.FreeHGlobal(TooltipMemory);
    }

    public void HideAll()
    {
        PartyListProcessor.HideAll();
        TargetInfoProcessor.HideAll();
        FocusTargetInfoProcessor.HideAll();
        StatusCustomProcessor.HideAll();
        StatusProcessor.HideAll();
        TargetInfoBuffDebuffProcessor.HideAll();
    }

    private void Tick()
    {
        List<(IPlayerCharacter Player, uint statusIcon)> SHECandidates = [];
        if (HoveringOver != 0)
        {
            if (IsKeyPressed(LimitedKeys.LeftMouseButton)) WasRightMousePressed = false;
            if (IsKeyPressed(LimitedKeys.RightMouseButton)) WasRightMousePressed = true;
        }
        foreach (var x in CleanupQueue)
        {
            x.StatusManager.AddTextShown.Remove(x.Status.GUID);
            x.StatusManager.RemTextShown.Remove(x.Status.GUID);
            x.StatusManager.Statuses.Remove(x.Status);
            if (x.StatusManager.Owner.ObjectIndex == 0 && x.Status.StatusOnDispell != Guid.Empty)
            {
                foreach (var status in C.SavedStatuses)
                {
                    if (status.GUID != x.Status.StatusOnDispell) continue;

                    x.StatusManager.AddOrUpdate(status.PrepareToApply(status.Persistent ? PrepareOptions.Persistent : PrepareOptions.NoOption));
                }
            }
        }
        CleanupQueue.Clear();
        foreach (var statusManager in C.StatusManagers)
        {
            foreach (var x in statusManager.Value.Statuses)
            {
                var rem = x.ExpiresAt - Utils.Time;
                if (rem > 0)
                {
                    if (!statusManager.Value.AddTextShown.Contains(x.GUID))
                    {
                        if (P.CanModifyUI() && Utils.TryFindPlayer(statusManager.Key, out var pc))
                        {
                            if (Utils.CanSpawnFlytext(pc))
                            {
                                FlyPopupTextProcessor.Enqueue(new(x, true, pc.EntityId));
                            }
                            if (Utils.CanSpawnVFX(pc))
                            {
                                if (!SHECandidates.Any(s => s.Player.AddressEquals(pc)))
                                {
                                    SHECandidates.Add((pc, (uint)x.IconID));
                                }
                            }
                        }
                        statusManager.Value.AddTextShown.Add(x.GUID);
                    }
                }
                else
                {
                    if (!statusManager.Value.RemTextShown.Contains(x.GUID))
                    {
                        if (P.CanModifyUI() && Utils.TryFindPlayer(statusManager.Key, out var pc))
                        {
                            if (Utils.CanSpawnFlytext(pc))
                            {
                                FlyPopupTextProcessor.Enqueue(new(x, false, pc.EntityId));
                            }
                            if (Utils.CanSpawnVFX(pc))
                            {
                                if (!SHECandidates.Any(s => s.Player.AddressEquals(pc)))
                                {
                                    SHECandidates.Add((pc, uint.MaxValue));
                                }
                            }
                        }
                        statusManager.Value.RemTextShown.Add(x.GUID);
                    }
                    CleanupQueue.Add((statusManager.Value, x));
                }
            }
            if (statusManager.Value.NeedFireEvent)
            {
                statusManager.Value.NeedFireEvent = false;
                if (Svc.Objects.TryGetFirst(x => x is IPlayerCharacter pc && pc.GetNameWithWorld() == statusManager.Key, out var pc))
                {
                    P.IPCProcessor.StatusManagerModified((IPlayerCharacter)pc);
                }
            }
        }
        CancelRequests.Clear();
        foreach (var x in SHECandidates)
        {
            if (!C.RestrictSHE || x.Player.AddressEquals(Player.Object) || Utils.GetFriendlist().Contains(x.Player.GetNameWithWorld()) || UniversalParty.Members.Any(z => z.NameWithWorld == x.Player.GetNameWithWorld()) || Vector3.Distance(Player.Object.Position, x.Player.Position) < 15f)
            {
                PluginLog.Debug($"StatusHitEffect on: {x.Player} / {x.statusIcon}");
                if (x.statusIcon == uint.MaxValue)
                {
                    P.Memory.SpawnSHE("dk04ht_canc0h", x.Player.Address, x.Player.Address, -1, char.MinValue, 0, char.MinValue);
                }
                else
                {
                    P.Memory.SpawnSHE(x.statusIcon, x.Player.Address, x.Player.Address, -1, char.MinValue, 0, char.MinValue);
                }
            }
            else
            {
                PluginLog.Debug($"Skipping SHE on {x.Player} / {x.statusIcon}");
            }
        }
    }

    public void SetIcon(AtkUnitBase* addon, AtkResNode* container, MyStatus status)
    {
        if (!container->IsVisible()) container->NodeFlags ^= NodeFlags.Visible;
        P.Memory.AtkComponentIconText_LoadIconByID(container->GetAsAtkComponentNode()->Component, (int)status.AdjustedIconID);
        var dispelNode = container->GetAsAtkComponentNode()->Component->UldManager.NodeList[0];
        if (status.Dispelable && !DispelableIcons.Contains((uint)status.IconID)) status.Dispelable = false;
        if (status.Dispelable != dispelNode->IsVisible())
        {
            dispelNode->NodeFlags ^= NodeFlags.Visible;
        }
        var textNode = container->GetAsAtkComponentNode()->Component->UldManager.NodeList[2];
        var timerText = "";
        if (status.ExpiresAt != long.MaxValue)
        {
            var rem = status.ExpiresAt - Utils.Time;
            timerText = rem > 0 ? GetTimerText(rem) : "";
        }
        if (timerText != null)
        {
            if (!textNode->IsVisible()) textNode->NodeFlags ^= NodeFlags.Visible;
        }
        var t = textNode->GetAsAtkTextNode();
        t->SetText((timerText ?? SeString.Empty).Encode());
        if (status.Applier == Player.NameWithWorld)
        {
            t->TextColor = CreateColor(0xc9ffe4ff);
            t->EdgeColor = CreateColor(0x0a5f24ff);
            t->BackgroundColor = CreateColor(0);
        }
        else
        {
            t->TextColor = CreateColor(0xffffffff);
            t->EdgeColor = CreateColor(0x333333ff);
            t->BackgroundColor = CreateColor(0);
        }
        var addr = (nint)(container->GetAsAtkComponentNode()->Component);
        //PluginLog.Debug($"- = - {MemoryHelper.ReadStringNullTerminated((nint)addon->Name)} - = -");
        if (HoveringOver == addr && status.TooltipShown == -1)
        {
            //PluginLog.Debug($"Trigger 0:{addr:X16} / {Utils.Frame} / {GetCallStackID()}");
            C.StatusManagers.Each(f => f.Value.Statuses.Each(z => z.TooltipShown = -1));
            status.TooltipShown = addon->Id;
            AtkStage.Instance()->TooltipManager.HideTooltip(addon->Id);
            var str = status.Title;
            if (status.Description != "")
            {
                str += $"\n{status.Description}";
            }
            MemoryHelper.WriteSeString(TooltipMemory, Utils.ParseBBSeString(str));
            AtkStage.Instance()->TooltipManager.ShowTooltip((ushort)addon->Id, container, (byte*)TooltipMemory);
        }
        if (status.TooltipShown == addon->Id && HoveringOver != addr)
        {
            //PluginLog.Debug($"Trigger 1 {addr:X16} / {Utils.Frame} / {GetCallStackID()}");
            status.TooltipShown = -1;
            if (HoveringOver == 0)
            {
                //PluginLog.Debug($"Trigger 2 / {Utils.Frame} / {GetCallStackID()}");
                AtkStage.Instance()->TooltipManager.HideTooltip(addon->Id);
            }
        }
        if (CancelRequests.Contains(addr))
        {
            CancelRequests.Remove(addr);
            var name = addon->NameString;
            if (name.StartsWith("_StatusCustom") || name == "_Status")
            {
                status.ExpiresAt = 0;
                P.IPCProcessor.StatusManagerModified(Player.Object);
            }
        }
    }

    public string GetTimerText(long rem)
    {
        var seconds = MathF.Ceiling((float)rem / 1000f);
        if (seconds <= 59) return seconds.ToString();
        var minutes = MathF.Floor((float)seconds / 60f);
        if (minutes <= 59) return $"{minutes}m";
        var hours = MathF.Floor((float)minutes / 60f);
        if (hours <= 59) return $"{hours}h";
        var days = MathF.Floor((float)hours / 24f);
        if (days <= 9) return $"{days}d";
        return $">9d";
    }

    private ByteColor CreateColor(uint color)
    {
        color = Endianness.SwapBytes(color);
        var ptr = &color;
        return *(ByteColor*)ptr;
    }
}
