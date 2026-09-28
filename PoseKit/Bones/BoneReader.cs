using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace PoseKit.Bones;

/// <summary>
/// Read-only access to a drawn character's bones, by name, in world space — for any player character
/// this client renders, not just the local player (same Human → Skeleton → PartialSkeletons walk
/// EmoteSyncCommand already does to reset emote loops). Never writes a bone. Works outside gpose:
/// gpose is only needed by posing tools to *edit* bones against the animation, not to read them.
///
/// World position = the skeleton's own transform (the drawn model's position/rotation/scale) applied
/// to the bone's model-space translation from the current Havok pose.
/// </summary>
public static unsafe class BoneReader
{
    /// The average world position of every bone in <paramref name="names"/> that exists on the
    /// character (each counted once, from the first partial skeleton it appears in) — for a body part
    /// with no single center bone, like the mouth. False when none of them exist.
    public static bool TryGetAverageBonePosition(IPlayerCharacter character, IReadOnlyList<string> names, out Vector3 world)
    {
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sum = Vector3.Zero;
        ForEachBone(character, (boneName, bonePosition) =>
        {
            if (!wanted.Contains(boneName) || !seen.Add(boneName)) return;
            sum += bonePosition;
        });

        world = seen.Count > 0 ? sum / seen.Count : default;
        return seen.Count > 0;
    }

    /// Where the drawn model itself stands and which way it faces (the game's heading convention: yaw
    /// r faces (sin r, 0, cos r)) — the skeleton's own transform, so it includes any render offset
    /// the character's own plugins applied, unlike GameObject.Position/Rotation.
    public static bool TryGetModelTransform(IPlayerCharacter character, out Vector3 position, out float yaw)
    {
        position = default;
        yaw = 0f;
        var native = (Character*)character.Address;
        if (native == null || native->DrawObject == null) return false;
        if (native->DrawObject->GetObjectType() != ObjectType.CharacterBase) return false;
        var skeleton = ((CharacterBase*)native->DrawObject)->Skeleton;
        if (skeleton == null) return false;

        var transform = skeleton->Transform;
        position = new Vector3(transform.Position.X, transform.Position.Y, transform.Position.Z);
        var rotation = new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        yaw = MathF.Atan2(forward.X, forward.Z);
        return true;
    }

    /// The world position of each bone in <paramref name="names"/>, by index — null where the character
    /// has no such bone. One skeleton walk for all of them.
    public static Vector3?[] GetBonePositions(IPlayerCharacter character, IReadOnlyList<string> names)
    {
        var result = new Vector3?[names.Count];
        var index = new Dictionary<string, int>(names.Count, StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++) index[names[i]] = i;
        ForEachBone(character, (boneName, bonePosition) =>
        {
            if (index.TryGetValue(boneName, out var i) && result[i] == null)
                result[i] = bonePosition;
        });
        return result;
    }

    /// Calls <paramref name="visit"/> with every bone's name and world position, partial skeleton by
    /// partial skeleton — used by TryGetAverageBonePosition and by the "/posekit bones" dump. The
    /// partial skeleton index is passed too, for the dump's grouping.
    public static void ForEachBone(IPlayerCharacter character, Action<string, Vector3> visit) =>
        ForEachBone(character, (_, name, position) => visit(name, position));

    public static void ForEachBone(IPlayerCharacter character, Action<int, string, Vector3> visit)
    {
        var native = (Character*)character.Address;
        if (native == null || native->DrawObject == null) return;
        if (native->DrawObject->GetObjectType() != ObjectType.CharacterBase) return;
        var characterBase = (CharacterBase*)native->DrawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return;

        var skeleton = characterBase->Skeleton;
        if (skeleton == null) return;

        // Explicit System.Numerics copies — the transform's fields are ClientStructs' own vector types,
        // which make mixed operators ambiguous.
        var transform = skeleton->Transform;
        var skeletonPosition = new Vector3(transform.Position.X, transform.Position.Y, transform.Position.Z);
        var skeletonRotation = new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
        var skeletonScale = new Vector3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z);

        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var pose = skeleton->PartialSkeletons[p].GetHavokPose(0);
            if (pose == null || pose->Skeleton == null) continue;

            var bones = pose->Skeleton->Bones;
            for (var i = 0; i < bones.Length; i++)
            {
                var name = bones[i].Name.String;
                if (string.IsNullOrEmpty(name)) continue;

                var modelSpace = pose->AccessBoneModelSpace(i, hkaPose.PropagateOrNot.DontPropagate);
                if (modelSpace == null) continue;

                var local = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z);
                visit(p, name, skeletonPosition + Vector3.Transform(local * skeletonScale, skeletonRotation));
            }
        }
    }
}
