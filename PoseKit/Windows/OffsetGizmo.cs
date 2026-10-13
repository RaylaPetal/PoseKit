using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace PoseKit.Windows;

/// <summary>
/// A move/rotate gizmo on the character's root, as an alternative to the live-offset sliders.
///
/// The drawn model sits at the actual position plus the draw offset turned by the drawn facing (see
/// BoneAlignService.Finish), so the gizmo is placed there and a drag is turned back into an offset in
/// that frame. Edits go through ApplyManualOffset, like the sliders.
///
/// The overlay covers the screen but only takes the mouse while it's over a handle (as of the last
/// frame) or dragging one, so the game keeps every other click and camera drag.
/// </summary>
public static unsafe class OffsetGizmo
{
    private const string WindowName = "##PoseKitOffsetGizmo";

    // A share of the viewport.
    private const float GizmoSize = 0.12f;

    private static bool takingMouse;

    public static void Draw(Plugin plugin)
    {
        var configuration = plugin.Configuration;
        if (!configuration.ShowOffsetGizmo) return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || PoseIdentifier.FromCharacter(localPlayer) == null) return;
        if (!TryGetCamera(out var view, out var projection)) return;

        var engine = plugin.OffsetEngine;
        var rotate = configuration.OffsetGizmoMode == OffsetGizmoMode.Rotate && engine.RotationHookResolved;
        var offset = engine.DesiredOffset;
        var obj = (GameObject*)localPlayer.Address;

        // The whole draw offset, which may include the game's own on top of ours.
        Vector3 drawOffset = obj->DrawOffset;
        var facing = localPlayer.Rotation + (engine.RotationHookResolved ? offset.Rotation : 0f);
        var root = localPlayer.Position + Vector3.Transform(drawOffset, Yaw(facing));

        // Off screen or behind the camera.
        if (!Plugin.GameGui.WorldToScreen(root, out _))
        {
            takingMouse = false;
            return;
        }

        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(display);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar;
        if (!takingMouse) flags |= ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin(WindowName, flags))
        {
            ImGuizmo.BeginFrame();
            // The window's own draw list: ImGuizmo checks hovering against the list's window.
            ImGuizmo.SetDrawlist(ImGui.GetWindowDrawList());
            ImGuizmo.SetRect(0, 0, display.X, display.Y);
            ImGuizmo.SetOrthographic(false);
            ImGuizmo.SetGizmoSizeClipSpace(GizmoSize);
            ImGuizmo.SetID(0x504B);

            var matrix = Matrix4x4.CreateRotationY(facing) * Matrix4x4.CreateTranslation(root);
            var operation = rotate ? ImGuizmoOperation.RotateY : ImGuizmoOperation.Translate;
            if (ImGuizmo.Manipulate(ref view, ref projection, operation, ImGuizmoMode.Local, ref matrix))
            {
                var newRoot = matrix.Translation;
                var newFacing = rotate ? MathF.Atan2(-matrix.M13, matrix.M11) : facing;

                // Back into the drawn frame, minus the part of the draw offset that isn't ours.
                var local = Vector3.Transform(newRoot - localPlayer.Position, Quaternion.Inverse(Yaw(newFacing)));
                plugin.PoseTrigger.ApplyManualOffset(new PoseOffset
                {
                    Position = local - (drawOffset - offset.Position),
                    Rotation = offset.Rotation + Wrap(newFacing - facing),
                });
            }
            takingMouse = ImGuizmo.IsOver() || ImGuizmo.IsUsing();
        }
        ImGui.End();
    }

    /// The game camera's view and projection, adjusted to the conventions ImGuizmo expects.
    private static bool TryGetCamera(out Matrix4x4 view, out Matrix4x4 projection)
    {
        view = default;
        projection = default;

        var manager = CameraManager.Instance();
        var camera = manager == null ? null : manager->CurrentCamera;
        if (camera == null || camera->RenderCamera == null) return false;

        view = camera->ViewMatrix;
        view.M44 = 1f;

        // The game's projection uses reverse depth; give ImGuizmo a conventional one.
        var renderCamera = camera->RenderCamera;
        projection = renderCamera->ProjectionMatrix;
        var near = renderCamera->NearPlane;
        var far = renderCamera->FarPlane;
        var clip = far / (far - near);
        projection.M43 = -(clip * near);
        projection.M33 = -((far + near) / (far - near));
        return true;
    }

    private static Quaternion Yaw(float yaw) => Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);

    private static float Wrap(float angle) => MathF.IEEERemainder(angle, MathF.Tau);
}

public enum OffsetGizmoMode { Move, Rotate }
