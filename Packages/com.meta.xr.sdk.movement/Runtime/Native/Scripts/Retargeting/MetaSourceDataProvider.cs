// Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved.

using Meta.XR.Movement.AI;
using Unity.Collections;
using UnityEngine;
using static Meta.XR.Movement.MSDKUtility;

namespace Meta.XR.Movement.Retargeting
{
    /// <summary>
    /// Source data provider for Meta body tracking with optional AI Motion Synthesizer blending.
    /// Inherits from <see cref="OVRBody"/> and implements <see cref="ISourceDataProvider"/>.
    /// </summary>
    public class MetaSourceDataProvider : OVRBody, ISourceDataProvider
    {
        public const string HalfBodyManifestation = "halfbody";

        private const int FullBodyJointCount = (int)SkeletonData.FullBodyTrackingBoneId.End;
        private const int UpperBodyJointCount = (int)SkeletonData.BodyTrackingBoneId.End;

        public bool DebugDrawSkeleton { get => _debugDrawSkeleton; set => _debugDrawSkeleton = value; }
        public bool EnableAIMotionSynthesizer { get => _enableAIMotionSynthesizer; set => _enableAIMotionSynthesizer = value; }

        [SerializeField] protected float _validBodyTrackingDelay = 0.25f;
        [SerializeField] protected bool _debugDrawSkeleton;
        [SerializeField] protected Color _debugSkeletonColor = Color.white;
        [SerializeField] protected bool _enableAIMotionSynthesizer;
        [SerializeField] protected AIMotionSynthesizerConfig _aiMotionSynthesizerConfig = new();

        protected AI.AIMotionSynthesizer _aiMotionSynthesizer;
        protected OVRPlugin.BodyJointSet _currentSkeletonType;
        protected int _skeletalChangedCount = -1;
        protected int _currentSkeletalChangeCount = -1;
        protected float _currentValidBodyTrackingTime;
        protected bool _isValid;

        private bool IsUpperBody => ProvidedSkeletonType == OVRPlugin.BodyJointSet.UpperBody;
        private bool IsSynthesizerActive => _enableAIMotionSynthesizer && _aiMotionSynthesizer is { IsInitialized: true };

        protected virtual void Start()
        {
            _currentSkeletonType = ProvidedSkeletonType;
            if (!_enableAIMotionSynthesizer)
            {
                return;
            }
            _aiMotionSynthesizer = new AI.AIMotionSynthesizer(_aiMotionSynthesizerConfig, transform);
            _aiMotionSynthesizer.Initialize();
        }

        protected virtual void LateUpdate()
        {
            if (!_enableAIMotionSynthesizer)
            {
                return;
            }
            _aiMotionSynthesizer.Update(Time.smoothDeltaTime);
            _aiMotionSynthesizer.ApplyRootMotion();
        }

        protected virtual void OnDestroy()
        {
            _aiMotionSynthesizer?.Dispose();
        }

        protected virtual void OnValidate()
        {
            _aiMotionSynthesizer?.ValidateInputProvider();
        }

        /// <inheritdoc />
        public virtual NativeArray<NativeTransform> GetSkeletonPose()
        {
            var sourcePose = SkeletonUtilities.GetPosesFromTheTracker(this, Pose.identity, true, out _currentSkeletalChangeCount, out _isValid);
            if (_currentValidBodyTrackingTime < _validBodyTrackingDelay)
            {
                _currentValidBodyTrackingTime += Time.smoothDeltaTime;
                _isValid = false;
            }
            if (_debugDrawSkeleton)
            {
                MeshDraw.DrawOVRSkeleton(this, _debugSkeletonColor, 0.04f, new Pose(transform.position, transform.rotation));
            }
            if (!_isValid || !IsSynthesizerActive)
            {
                return sourcePose;
            }
            return IsUpperBody ? ProcessUpperBodyPose(sourcePose) : ProcessFullBodyPose(sourcePose);
        }

        private NativeArray<NativeTransform> ProcessFullBodyPose(NativeArray<NativeTransform> sourcePose)
        {
            var blendedPose = _aiMotionSynthesizer.GetBlendedPose(sourcePose);
            _aiMotionSynthesizer.DrawVisualization(sourcePose, blendedPose, new Pose(transform.position, transform.rotation));
            if (sourcePose.IsCreated)
            {
                sourcePose.Dispose();
            }
            return blendedPose;
        }

        private NativeArray<NativeTransform> ProcessUpperBodyPose(NativeArray<NativeTransform> sourcePose)
        {
            var fullBodyPose = new NativeArray<NativeTransform>(FullBodyJointCount, Allocator.Temp);
            var copyCount = Mathf.Min(UpperBodyJointCount, sourcePose.Length);
            NativeArray<NativeTransform>.Copy(sourcePose, fullBodyPose, copyCount);
            var identity = NativeTransform.Identity();
            for (var i = UpperBodyJointCount; i < FullBodyJointCount; i++)
            {
                fullBodyPose[i] = identity;
            }

            var blendedPose = _aiMotionSynthesizer.GetBlendedPose(fullBodyPose);
            _aiMotionSynthesizer.DrawVisualization(fullBodyPose, blendedPose, new Pose(transform.position, transform.rotation));
            fullBodyPose.Dispose();

            var upperBodyPose = new NativeArray<NativeTransform>(UpperBodyJointCount, Allocator.Temp);
            NativeArray<NativeTransform>.Copy(blendedPose, upperBodyPose, Mathf.Min(UpperBodyJointCount, blendedPose.Length));
            if (blendedPose.IsCreated)
            {
                blendedPose.Dispose();
            }
            if (sourcePose.IsCreated)
            {
                sourcePose.Dispose();
            }
            return upperBodyPose;
        }

        /// <inheritdoc />
        public virtual NativeArray<NativeTransform> GetSkeletonTPose()
        {
            var sourcePose = SkeletonUtilities.GetBindPoses(this);
            _skeletalChangedCount = _currentSkeletalChangeCount;
            if (IsSynthesizerActive)
            {
                _aiMotionSynthesizer.UpdateTPose(sourcePose);
            }
            return sourcePose;
        }

        /// <inheritdoc />
        public virtual string GetManifestation()
        {
            return _isValid && IsUpperBody ? HalfBodyManifestation : null;
        }

        /// <inheritdoc />
        public virtual bool IsPoseValid()
        {
            return _isValid;
        }

        /// <inheritdoc />
        public virtual bool IsNewTPoseAvailable()
        {
            if (_currentSkeletonType == ProvidedSkeletonType)
            {
                return _currentSkeletalChangeCount != _skeletalChangedCount;
            }
            _currentSkeletonType = ProvidedSkeletonType;
            return true;
        }
    }
}
