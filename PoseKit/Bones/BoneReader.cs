using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace PoseKit.Bones;

/// <summary>
/// Read-only world-space bone positions for any drawn player character. Works outside gpose.
/// </summary>
public static unsafe class BoneReader
{
    /// The average world position of the named bones that exist, for body parts without a single
    /// center bone. False when none exist.
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

    /// The drawn model's position and yaw, including any render offset (unlike GameObject.Position).
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

        // Copy to System.Numerics types; mixing vector types makes operators ambiguous.
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
