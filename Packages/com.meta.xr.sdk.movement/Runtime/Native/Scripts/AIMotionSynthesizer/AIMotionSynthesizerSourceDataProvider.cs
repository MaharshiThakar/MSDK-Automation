// Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved.

using Meta.XR.Movement.Retargeting;
using Unity.Collections;
using UnityEngine;
using static Meta.XR.Movement.MSDKUtility;

namespace Meta.XR.Movement.AI
{
    /// <summary>
    /// Source data provider that generates poses from the AI Motion Synthesizer system.
    /// Use with <see cref="Meta.XR.Movement.Retargeting.CharacterRetargeter"/> for AI motion synthesizer-driven characters.
    /// </summary>
    public class AIMotionSynthesizerSourceDataProvider : MonoBehaviour, ISourceDataProvider
    {
        private const string BasePath = "Packages/com.meta.xr.sdk.movement/Runtime/Native/Data/";

        [SerializeField]
        [Tooltip("JSON configuration file for the AIMotionSynthesizer skeleton data")]
        private TextAsset _config;

        [SerializeField]
        [Tooltip("Neural network model asset for AIMotionSynthesizer")]
        private TextAsset _modelAsset;

        [SerializeField]
        [Tooltip("Guidance asset for animation variations")]
        private TextAsset _guidanceAsset;

        [SerializeField]
        [Tooltip("Input provider (must implement IAIMotionSynthesizerInputProvider)")]
        private MonoBehaviour _inputProvider;

        [SerializeField]
        [Tooltip("Use manual velocity/direction instead of input provider")]
        private bool _useManualInput;

        [SerializeField]
        [Tooltip("Manual velocity (used when UseManualInput is true)")]
        private Vector3 _manualVelocity = Vector3.zero;

        [SerializeField]
        [Tooltip("Manual direction (used when UseManualInput is true)")]
        private Vector3 _manualDirection = Vector3.forward;

        [SerializeField]
        [Tooltip("Apply root motion from the AI Motion Synthesizer to this transform")]
        private bool _applyRootMotion;

        [SerializeField]
        [Tooltip("Enable debug skeleton visualization")]
        private bool _debugDrawSkeleton;

        [SerializeField]
        [Tooltip("Color for debug skeleton visualization")]
        private Color _debugSkeletonColor = Color.cyan;

        private IAIMotionSynthesizerInputProvider _inputProviderCasted;
        private ulong _aiMotionSynthesizerHandle = MSDKAIMotionSynthesizer.INVALID_HANDLE;
        private NativeArray<NativeTransform> _currentPose;
        private NativeArray<NativeTransform> _tPose;
        private NativeTransform _rootPose;
        private bool _isPoseValid;
        private int[] _parentIndices;

        protected virtual void Awake()
        {
            _inputProviderCasted = _inputProvider as IAIMotionSynthesizerInputProvider;

            if (!_config || !_modelAsset)
            {
                Debug.LogError("[AIMotionSynthesizerSourceDataProvider] Config or Model asset is missing");
                return;
            }

            if (!MSDKAIMotionSynthesizer.CreateOrUpdateHandle(_config.text, out _aiMotionSynthesizerHandle))
            {
                Debug.LogError("[AIMotionSynthesizerSourceDataProvider] Failed to create AIMotionSynthesizer handle");
                return;
            }

            if (!MSDKAIMotionSynthesizer.Initialize(_aiMotionSynthesizerHandle, _modelAsset.bytes, _guidanceAsset?.bytes))
            {
                Debug.LogError("[AIMotionSynthesizerSourceDataProvider] Failed to initialize AIMotionSynthesizer");
                CleanupHandle();
                return;
            }

            if (!MSDKAIMotionSynthesizer.GetSkeletonInfo(_aiMotionSynthesizerHandle, SkeletonType.TargetSkeleton, out var info))
            {
                Debug.LogError("[AIMotionSynthesizerSourceDataProvider] Failed to get skeleton info");
                CleanupHandle();
                return;
            }

            _currentPose = new NativeArray<NativeTransform>(info.JointCount, Allocator.Persistent);
            _tPose = new NativeArray<NativeTransform>(info.JointCount, Allocator.Persistent);
            MSDKAIMotionSynthesizer.GetTPoseByRef(_aiMotionSynthesizerHandle, ref _tPose);
        }

        protected virtual void Update()
        {
            if (_aiMotionSynthesizerHandle == MSDKAIMotionSynthesizer.INVALID_HANDLE)
            {
                return;
            }

            var dt = Time.smoothDeltaTime;
            var velocity = _useManualInput ? _manualVelocity : (_inputProviderCasted?.GetVelocity() ?? Vector3.zero);
            var direction = _useManualInput ? _manualDirection : (_inputProviderCasted?.GetDirection() ?? Vector3.forward);

            if (MSDKAIMotionSynthesizer.Process(_aiMotionSynthesizerHandle, dt, direction, velocity))
            {
                MSDKAIMotionSynthesizer.Predict(_aiMotionSynthesizerHandle, dt);
            }
        }

        protected virtual void OnDestroy()
        {
            if (_currentPose.IsCreated)
            {
                _currentPose.Dispose();
            }

            if (_tPose.IsCreated)
            {
                _tPose.Dispose();
            }

            CleanupHandle();
        }

        private void CleanupHandle()
        {
            if (_aiMotionSynthesizerHandle != MSDKAIMotionSynthesizer.INVALID_HANDLE)
            {
                MSDKAIMotionSynthesizer.DestroyHandle(_aiMotionSynthesizerHandle);
                _aiMotionSynthesizerHandle = MSDKAIMotionSynthesizer.INVALID_HANDLE;
            }
        }

        private void Reset()
        {
            AutoFindInputProvider();
            AutoLoadDefaultAssets();
        }

        private void OnValidate()
        {
            _inputProviderCasted = _inputProvider as IAIMotionSynthesizerInputProvider;

            if (_inputProvider == null)
            {
                AutoFindInputProvider();
            }

            AutoLoadDefaultAssets();
        }

        private void AutoFindInputProvider()
        {
            if (_inputProvider != null)
            {
                return;
            }

            foreach (var component in GetComponents<MonoBehaviour>())
            {
                if (component is IAIMotionSynthesizerInputProvider provider)
                {
                    _inputProvider = component;
                    _inputProviderCasted = provider;
#if UNITY_EDITOR
                    if (!Application.isPlaying)
                    {
                        UnityEditor.EditorUtility.SetDirty(this);
                    }
#endif
                    break;
                }
            }
        }

        private void AutoLoadDefaultAssets()
        {
#if UNITY_EDITOR
            if (Application.isPlaying)
            {
                return;
            }

            bool changed = TryLoadAsset(ref _config, BasePath + "AIMotionSynthesizerSkeletonData.json");
            changed |= TryLoadAsset(ref _modelAsset, BasePath + "AIMotionSynthesizerModel.bytes");
            changed |= TryLoadAsset(ref _guidanceAsset, BasePath + "AIMotionSynthesizerGuidance.bytes");

            if (changed)
            {
                UnityEditor.EditorUtility.SetDirty(this);
            }
#endif
        }

#if UNITY_EDITOR
        private static bool TryLoadAsset(ref TextAsset field, string path)
        {
            if (field != null)
            {
                return false;
            }

            field = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            return field != null;
        }
#endif

        /// <summary>
        /// Gets the current AI motion synthesizer pose.
        /// </summary>
        public virtual NativeArray<NativeTransform> GetSkeletonPose()
        {
            _isPoseValid = MSDKAIMotionSynthesizer.GetPoseByRef(
                _aiMotionSynthesizerHandle,
                ref _currentPose,
                out _rootPose,
                _applyRootMotion ? MSDKAIMotionSynthesizer.RootMotionMode.LocalSpace : MSDKAIMotionSynthesizer.RootMotionMode.None);

            if (_isPoseValid && _applyRootMotion)
            {
                transform.SetLocalPositionAndRotation(_rootPose.Position, _rootPose.Orientation);
            }

            if (_debugDrawSkeleton && _currentPose is { IsCreated: true, Length: > 0 })
            {
                DrawDebugSkeleton();
            }

            return _currentPose;
        }

        private void DrawDebugSkeleton()
        {
            var jointCount = (int)SkeletonData.FullBodyTrackingBoneId.End;
            if (_parentIndices == null || _parentIndices.Length != jointCount)
            {
                _parentIndices = new int[jointCount];
                for (int i = 0; i < jointCount; i++)
                {
                    _parentIndices[i] = (int)SkeletonData.ParentBoneId[i];
                }
            }

            MeshDraw.DrawSkeleton(_currentPose, _parentIndices, _debugSkeletonColor);
        }

        /// <summary>Gets the T-pose for the AI motion synthesizer skeleton.</summary>
        public virtual NativeArray<NativeTransform> GetSkeletonTPose() => _tPose;

        /// <summary>Gets the manifestation string. Returns null for AI motion synthesizer.</summary>
        public virtual string GetManifestation() => null;

        /// <summary>Whether the last pose retrieval succeeded.</summary>
        public virtual bool IsPoseValid() => _isPoseValid;

        /// <summary>Whether a new T-pose is available. Always false for AI motion synthesizer.</summary>
        public virtual bool IsNewTPoseAvailable() => false;
    }
}
