using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace PoseKit.Sync;

/// <summary>Issues a slash command exactly as if the user typed it.</summary>
public static unsafe class ChatCommand
{
    public static void Execute(string command)
    {
        var ui = UIModule.Instance();
        if (ui == null) return;
        using var text = new Utf8String(command);
        ui->ProcessChatBoxEntry(&text);
    }
}
