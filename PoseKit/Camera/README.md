# Freecam

Toggle with `/posekit tfc` or the main-window freecam button. Stop moving and disable autorun before enabling it outside GPose.

- W/S: forward/backward
- A/D: strafe
- E/Q: up/down
- Right mouse drag: look
- `/posekit tfc`: return to the normal camera

Camera navigation pauses during text entry, plugin UI interaction, or lost window focus. Character input restrictions remain active until freecam exits. Freecam exits on world/camera transitions and does not resume automatically.

Cammy and Ktisis are not required. Only one plugin should actively control the camera at a time.

This implementation still requires the in-game acceptance checks recorded in `openspec/changes/native-freecam/verification.md`, particularly character immobility and emote preservation.
