// Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved.

using UnityEngine;
#if USE_UNITY_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Meta.XR.Movement.AI
{
    /// <summary>
    /// Input system mode for <see cref="AIMotionSynthesizerJoystickInput"/>.
    /// </summary>
    public enum InputMode
    {
        /// <summary>Use OVRInput for controller input.</summary>
        OVRInput,

        /// <summary>Use Unity's new Input System.</summary>
        UnityInputSystem,

        /// <summary>Use both systems, preferring whichever has larger input magnitude.</summary>
        Both
    }

    /// <summary>
    /// Movement direction mode for <see cref="AIMotionSynthesizerJoystickInput"/>.
    /// </summary>
    public enum MovementDirectionMode
    {
        /// <summary>
        /// Relative: joystick input is relative to the reference transform.
        /// Forward moves toward where the reference is facing.
        /// </summary>
        Relative,

        /// <summary>
        /// Absolute: joystick input maps directly to world axes.
        /// Up=+Z, Down=-Z, Right=+X, Left=-X.
        /// </summary>
        Absolute
    }

    /// <summary>
    /// Facing direction mode for <see cref="AIMotionSynthesizerJoystickInput"/>.
    /// Controls how the character's facing direction is determined.
    /// </summary>
    public enum FacingDirectionMode
    {
        /// <summary>
        /// Character faces the direction of movement velocity.
        /// </summary>
        FaceMovementDirection,

        /// <summary>
        /// Character maintains the reference transform's forward direction.
        /// Useful for strafing or keeping focus on a target.
        /// </summary>
        FaceReferenceForward
    }

    /// <summary>
    /// Joystick input provider for AI Motion Synthesizer.
    /// Supports OVRInput, Unity Input System, or both simultaneously.
    /// </summary>
    public class AIMotionSynthesizerJoystickInput : MonoBehaviour, IAIMotionSynthesizerInputProvider
    {
        /// <inheritdoc/>
        public Vector3 GetVelocity() => _currentVelocity;

        /// <inheritdoc/>
        public Vector3 GetDirection() => _currentDirection;

        /// <inheritdoc/>
        public bool IsInputActive() => _inputActive;

        /// <inheritdoc/>
        public Transform GetReferenceTransform() => _referenceTransform != null ? _referenceTransform : transform;

        [SerializeField]
        [Tooltip("Select which input system to use")]
        private InputMode _inputMode = InputMode.OVRInput;

        [SerializeField]
        [Tooltip(
            "Reference transform for input calculations. Determines coordinate space for velocity/direction. If empty, uses this component's transform.")]
        private Transform _referenceTransform;

#if USE_UNITY_INPUT_SYSTEM
        [SerializeField]
        [Tooltip("Move action (left stick) using Unity's Input System")]
        private InputActionReference _moveAction;

        [SerializeField]
        [Tooltip("Look action (right stick) using Unity's Input System")]
        private InputActionReference _lookAction;

        [SerializeField]
        [Tooltip("Sprint action (optional)")]
        private InputActionReference _sprintAction;
#endif

        [SerializeField]
        [Tooltip("The main controller to use for input when using OVRInput mode")]
        private OVRInput.Controller _mainController = OVRInput.Controller.LTouch;

        [SerializeField]
        [Tooltip("Axis to use for movement (left thumbstick)")]
        private OVRInput.Axis2D _moveAxis = OVRInput.Axis2D.PrimaryThumbstick;

        [SerializeField]
        [Tooltip("Axis to use for looking (right thumbstick)")]
        private OVRInput.Axis2D _lookAxis = OVRInput.Axis2D.SecondaryThumbstick;

        [SerializeField]
        [Tooltip("Button to hold for sprint (optional)")]
        private OVRInput.Button _sprintButton = OVRInput.Button.PrimaryThumbstickDown;

        [SerializeField]
        [Tooltip("Speed multiplier applied while moving normally (m/s)")]
        private float _speedFactor = 2.5f;

        [SerializeField]
        [Tooltip("Speed multiplier applied while sprinting (m/s)")]
        private float _sprintSpeedFactor = 4.5f;

        [SerializeField]
        [Tooltip("The rate of acceleration during movement")]
        private float _acceleration = 5f;

        [SerializeField]
        [Tooltip("The rate of damping on movement while grounded")]
        private float _groundDamping = 40f;

        [SerializeField]
        [Tooltip("Joystick dead zone threshold")]
        [Range(0f, 1f)]
        private float _joystickThreshold = 0.25f;

        [SerializeField]
        [Tooltip("Movement direction mode: Relative or Absolute")]
        private MovementDirectionMode _movementDirectionMode = MovementDirectionMode.Relative;

        [SerializeField]
        [Tooltip("Facing direction mode: determines how the character's facing direction is calculated")]
        private FacingDirectionMode _facingDirectionMode = FacingDirectionMode.FaceMovementDirection;

        [SerializeField]
        [Tooltip("Lock direction at input start until stop. Useful for strafing.")]
        private bool _lockDirectionOnInputStart;

        private bool _inputActive;
        private Vector3 _currentVelocity;
        private Vector3 _currentDirection = Vector3.forward;
        private Vector3 _targetVelocity;

        private bool _hasCapturedMoveReferenceForward;
        private Vector3 _capturedMoveReferenceForward = Vector3.forward;
        private bool _hasCapturedLookReferenceForward;
        private Vector3 _capturedLookReferenceForward = Vector3.forward;

        private void Update()
        {
            CalculateVelocityAndDirection();
        }

        private (Vector2 move, Vector2 look, bool sprint) ReadOVRInput()
        {
            return (
                OVRInput.Get(_moveAxis, _mainController),
                OVRInput.Get(_lookAxis, _mainController),
                OVRInput.Get(_sprintButton, _mainController)
            );
        }

#if USE_UNITY_INPUT_SYSTEM
        private (Vector2 move, Vector2 look, bool sprint) ReadUnityInput()
        {
            return (
                _moveAction?.action?.ReadValue<Vector2>() ?? Vector2.zero,
                _lookAction?.action?.ReadValue<Vector2>() ?? Vector2.zero,
                _sprintAction?.action?.IsPressed() ?? false
            );
        }

        private (Vector2 move, Vector2 look, bool sprint) ReadCombinedInput()
        {
            var (unityMove, unityLook, unitySprint) = ReadUnityInput();

            var ovrMove = Vector2.zero;
            var ovrLook = Vector2.zero;
            var ovrSprint = false;

            if (OVRInput.IsControllerConnected(_mainController))
            {
                (ovrMove, ovrLook, ovrSprint) = ReadOVRInput();
            }

            return (
                unityMove.magnitude > ovrMove.magnitude ? unityMove : ovrMove,
                unityLook.magnitude > ovrLook.magnitude ? unityLook : ovrLook,
                unitySprint || ovrSprint
            );
        }
#endif

        private (Vector2 move, Vector2 look, bool sprint) ReadInputForMode()
        {
            switch (_inputMode)
            {
#if USE_UNITY_INPUT_SYSTEM
                case InputMode.UnityInputSystem:
                    return ReadUnityInput();
                case InputMode.Both:
                    return ReadCombinedInput();
#endif
                case InputMode.OVRInput:
                default:
                    return ReadOVRInput();
            }
        }

        private Vector3 ApplyDeadzone(Vector2 input)
        {
            var input3D = new Vector3(input.x, 0f, input.y);
            var magnitude = input3D.magnitude;
            if (magnitude < _joystickThreshold)
            {
                return Vector3.zero;
            }
            var scaledMagnitude = (magnitude - _joystickThreshold) / (1f - _joystickThreshold);
            return input3D.normalized * Mathf.Clamp01(scaledMagnitude);
        }

        private Vector3 GetReferenceForward(
            bool hasInput,
            Vector3 currentForward,
            ref bool hasCapturedForward,
            ref Vector3 capturedForward)
        {
            if (!_lockDirectionOnInputStart || !hasInput)
            {
                hasCapturedForward = false;
                return currentForward;
            }

            if (!hasCapturedForward)
            {
                capturedForward = currentForward;
                hasCapturedForward = true;
            }
            return capturedForward;
        }

        private void CalculateVelocityAndDirection()
        {
            var (moveInput, lookInput, isSprinting) = ReadInputForMode();

            var move = ApplyDeadzone(moveInput);
            var look = ApplyDeadzone(lookInput);

            var currentReferenceForward = _referenceTransform != null
                ? Vector3.ProjectOnPlane(_referenceTransform.forward, Vector3.up).normalized
                : Vector3.forward;

            if (currentReferenceForward.sqrMagnitude < 0.001f)
            {
                currentReferenceForward = Vector3.forward;
            }

            var moveReferenceForward = GetReferenceForward(
                move.magnitude > 0f,
                currentReferenceForward,
                ref _hasCapturedMoveReferenceForward,
                ref _capturedMoveReferenceForward);

            var lookReferenceForward = GetReferenceForward(
                look.magnitude > 0f,
                currentReferenceForward,
                ref _hasCapturedLookReferenceForward,
                ref _capturedLookReferenceForward);

            var moveRotation = GetDirectionRotation(moveReferenceForward);
            var lookRotation = GetDirectionRotation(lookReferenceForward);

            _inputActive = move.magnitude > 0f || look.magnitude > 0f;
            _targetVelocity = Vector3.zero;

            if (move.magnitude > 0f)
            {
                var worldMove = moveRotation * move;
                var speed = isSprinting ? _sprintSpeedFactor : _speedFactor;
                _targetVelocity = worldMove * speed;
            }

            var lerpRate = _targetVelocity.magnitude > 0f ? _acceleration : _groundDamping;
            _currentVelocity = Vector3.Lerp(_currentVelocity, _targetVelocity, lerpRate * Time.deltaTime);

            if (look.magnitude > 0f)
            {
                _currentDirection = lookRotation * look;
            }
            else if (move.magnitude > 0f)
            {
                if (_facingDirectionMode == FacingDirectionMode.FaceMovementDirection)
                {
                    _currentDirection = _targetVelocity;
                }
                else
                {
                    _currentDirection = currentReferenceForward;
                }
            }
            else
            {
                _currentDirection = currentReferenceForward;
            }
        }

        private Quaternion GetDirectionRotation(Vector3 referenceForward)
        {
            if (_movementDirectionMode == MovementDirectionMode.Absolute)
            {
                return Quaternion.identity;
            }
            return Quaternion.LookRotation(referenceForward, Vector3.up);
        }

#if USE_UNITY_INPUT_SYSTEM
        /// <summary>Sets the Unity Input System action for movement input.</summary>
        public void SetMoveAction(InputActionReference moveAction) => _moveAction = moveAction;

        /// <summary>Sets the Unity Input System action for sprint input.</summary>
        public void SetSprintAction(InputActionReference sprintAction) => _sprintAction = sprintAction;

        /// <summary>Sets the Unity Input System action for look/rotation input.</summary>
        public void SetLookAction(InputActionReference lookAction) => _lookAction = lookAction;
#endif

        /// <summary>Sets the reference transform for input coordinate space.</summary>
        public void SetReferenceTransform(Transform referenceTransform) => _referenceTransform = referenceTransform;

        /// <summary>Sets the movement direction mode.</summary>
        public void SetMovementDirectionMode(MovementDirectionMode mode) => _movementDirectionMode = mode;

        /// <summary>Sets whether to lock direction at input start.</summary>
        public void SetLockDirectionOnInputStart(bool locked) => _lockDirectionOnInputStart = locked;
    }
}
