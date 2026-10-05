using UnityEngine;
using UnityEngine.InputSystem;

namespace GunAndParry
{
    /// <summary>Gameplay input (keyboard+mouse and gamepad). UI navigation is handled by the EventSystem.</summary>
    public sealed class GameInput
    {
        readonly InputAction move, aim, parry, dash, pause;
        float rumbleUntil;

        public bool UsingGamepad { get; private set; }

        public GameInput()
        {
            move = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
            move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow").With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
            move.AddBinding("<Gamepad>/leftStick");

            aim = new InputAction("Aim", InputActionType.Value, "<Gamepad>/rightStick");

            parry = new InputAction("Parry", InputActionType.Button);
            parry.AddBinding("<Mouse>/rightButton");
            parry.AddBinding("<Keyboard>/space");
            parry.AddBinding("<Gamepad>/rightShoulder");
            parry.AddBinding("<Gamepad>/rightTrigger");

            dash = new InputAction("Dash", InputActionType.Button);
            dash.AddBinding("<Keyboard>/leftShift");
            dash.AddBinding("<Mouse>/leftButton");
            dash.AddBinding("<Gamepad>/leftShoulder");
            dash.AddBinding("<Gamepad>/leftTrigger");
            dash.AddBinding("<Gamepad>/buttonSouth");

            pause = new InputAction("Pause", InputActionType.Button);
            pause.AddBinding("<Keyboard>/escape");
            pause.AddBinding("<Gamepad>/start");

            move.Enable(); aim.Enable(); parry.Enable(); dash.Enable(); pause.Enable();
        }

        public Vector2 Move => Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f);
        public Vector2 AimStick => aim.ReadValue<Vector2>();
        public bool ParryPressed => parry.WasPressedThisFrame();
        public bool DashPressed => dash.WasPressedThisFrame();
        public bool PausePressed => pause.WasPressedThisFrame();
        public Vector2 MouseScreen => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;

        public void Update()
        {
            var pad = Gamepad.current;
            if (pad != null && (pad.leftStick.ReadValue().sqrMagnitude > 0.06f || pad.rightStick.ReadValue().sqrMagnitude > 0.06f ||
                                pad.buttonSouth.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame || pad.startButton.wasPressedThisFrame))
                UsingGamepad = true;
            var mouse = Mouse.current;
            if (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 4f || mouse.rightButton.wasPressedThisFrame))
                UsingGamepad = false;
            if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame)
                UsingGamepad = false;

            if (rumbleUntil > 0 && Time.unscaledTime > rumbleUntil) StopRumble();
        }

        public void Rumble(float low, float high, float duration)
        {
            if (!UsingGamepad || Gamepad.current == null) return;
            Gamepad.current.SetMotorSpeeds(low, high);
            rumbleUntil = Time.unscaledTime + duration;
        }

        public void StopRumble()
        {
            rumbleUntil = 0;
            Gamepad.current?.SetMotorSpeeds(0, 0);
        }

        public void Dispose()
        {
            StopRumble();
            move.Dispose(); aim.Dispose(); parry.Dispose(); dash.Dispose(); pause.Dispose();
        }
    }
}
