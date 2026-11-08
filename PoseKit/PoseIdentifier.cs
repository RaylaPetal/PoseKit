using System;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace PoseKit;

/// <summary>
/// Which pose/emote variant is playing, read from the character's emote state.
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

    /// Standing idles are pose mode 0, reached by stepping /cpose. Kept apart from FromCharacter, whose
    /// callers treat any non-null result as "in a looping pose".
    public const uint StandingIdleMode = 0;

    /// The standing idle (mode 0) the character is on, or null unless it's standing normally.
    public static PoseIdentifier? StandingIdle(Character* character)
    {
        if (character == null || character->Mode != CharacterModes.Normal) return null;
        return new PoseIdentifier(StandingIdleMode, character->EmoteController.CPoseState);
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

    /// The slash command (e.g. "sweep"), which can differ from the display name ("Sweep Up").
    public string? SlashCommand => EmoteModeId == StandingIdleMode ? null : FetchNameAndCommand(EmoteModeId).Command;

    /// GroundSit(1)/Sit(2)/Doze(3) are the pose-cycling emotes; every other EmoteModeId has a single variant.
    public string DisplayName => EmoteModeId switch
    {
        StandingIdleMode => $"Idle {CPoseState}",
        1 or 2 or 3 => $"{EmoteName} Pose {CPoseState}",
        _ => EmoteName,
    };
}
