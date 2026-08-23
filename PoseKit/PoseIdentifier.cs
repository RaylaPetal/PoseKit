using System;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace PoseKit;

/// <summary>
/// Identifies "which pose/emote variant is currently playing" — mirrors SimpleHeels' EmoteIdentifier
/// (SimpleHeels-master/EmoteIdentifier.cs), read from the character's animation/EmoteController state
/// rather than guessed off a played .pap filename.
/// </summary>
public readonly unsafe record struct PoseIdentifier(uint EmoteModeId, byte CPoseState)
{
    public static PoseIdentifier? FromCharacter(IPlayerCharacter? playerCharacter)
        => FromCharacter((Character*)(playerCharacter?.Address ?? 0));

    public static PoseIdentifier? FromCharacter(Character* character)
    {
        if (character == null) return null;
        if (character->Mode is not (CharacterModes.InPositionLoop or CharacterModes.EmoteLoop)) return null;
        return new PoseIdentifier(character->ModeParam, character->EmoteController.CPoseState);
    }

    private static (string Name, string? Command) FetchNameAndCommand(uint emoteModeId)
    {
        var emoteMode = Plugin.DataManager.GetExcelSheet<EmoteMode>().GetRowOrDefault(emoteModeId);
        if (emoteMode is null) return ($"EmoteMode#{emoteModeId}", null);
        var emote = emoteMode.Value.StartEmote;
        if (!emote.IsValid || emote.RowId == 0) return ($"EmoteMode#{emoteModeId}", null);

        var name = emote.Value.Name.ExtractText();
        var commandRow = emote.Value.TextCommand;
        var command = commandRow.IsValid ? commandRow.Value.Command.ExtractText().TrimStart('/') : null;
        return (name, string.IsNullOrEmpty(command) ? null : command);
    }

    public string EmoteName => FetchNameAndCommand(EmoteModeId).Name;

    /// The emote's actual slash command (e.g. "sweep"), distinct from its display name (e.g.
    /// "Sweep Up") — using the display name to build a chat command is wrong for any emote whose
    /// command text doesn't match its label.
    public string? SlashCommand => FetchNameAndCommand(EmoteModeId).Command;

    /// GroundSit(1)/Sit(2)/Doze(3) are the pose-cycling emotes; every other EmoteModeId has a single variant.
    public string DisplayName => EmoteModeId is 1 or 2 or 3 ? $"{EmoteName} Pose {CPoseState + 1}" : EmoteName;
}
