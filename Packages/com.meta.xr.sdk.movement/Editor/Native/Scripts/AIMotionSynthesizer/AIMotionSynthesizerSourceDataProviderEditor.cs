// Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved.

using UnityEditor;
using UnityEngine;

namespace Meta.XR.Movement.AI.Editor
{
    [CustomEditor(typeof(AIMotionSynthesizerSourceDataProvider))]
    public class AIMotionSynthesizerSourceDataProviderEditor : UnityEditor.Editor
    {
        private const string BasePath = "Packages/com.meta.xr.sdk.movement/Runtime/Native/Data/";

        private static readonly Color HeaderColor = new(0.3f, 0.5f, 0.7f, 0.2f);

        private static readonly (string prop, string path)[] DefaultAssets =
        {
            ("_config", "AIMotionSynthesizerSkeletonData.json"),
            ("_modelAsset", "AIMotionSynthesizerModel.bytes"),
            ("_guidanceAsset", "AIMotionSynthesizerGuidance.bytes")
        };

        private SerializedProperty _configProperty;
        private SerializedProperty _modelAssetProperty;
        private SerializedProperty _guidanceAssetProperty;
        private SerializedProperty _inputProviderProperty;
        private SerializedProperty _applyRootMotionProperty;
        private SerializedProperty _debugDrawSkeletonProperty;
        private SerializedProperty _debugSkeletonColorProperty;

        private void OnEnable()
        {
            _configProperty = serializedObject.FindProperty("_config");
            _modelAssetProperty = serializedObject.FindProperty("_modelAsset");
            _guidanceAssetProperty = serializedObject.FindProperty("_guidanceAsset");
            _inputProviderProperty = serializedObject.FindProperty("_inputProvider");
            _applyRootMotionProperty = serializedObject.FindProperty("_applyRootMotion");
            _debugDrawSkeletonProperty = serializedObject.FindProperty("_debugDrawSkeleton");
            _debugSkeletonColorProperty = serializedObject.FindProperty("_debugSkeletonColor");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawScriptField();
            EditorGUILayout.Space(4);

            AIMotionSynthesizerEditorUtils.DrawSection("AIMotionSynthesizer Configuration", HeaderColor, () =>
            {
                EditorGUILayout.PropertyField(_configProperty);
                EditorGUILayout.PropertyField(_modelAssetProperty);
                EditorGUILayout.PropertyField(_guidanceAssetProperty);
                EditorGUILayout.Space(4);

                if (GUILayout.Button("Load Default Assets"))
                {
                    LoadDefaultAssets();
                }
            });

            EditorGUILayout.Space(4);

            AIMotionSynthesizerEditorUtils.DrawSection("Input Control", HeaderColor, () =>
            {
                EditorGUILayout.PropertyField(_inputProviderProperty, new GUIContent("Input Provider"));
            });

            EditorGUILayout.Space(4);

            AIMotionSynthesizerEditorUtils.DrawSection("Root Motion", HeaderColor, () =>
            {
                EditorGUILayout.PropertyField(_applyRootMotionProperty, new GUIContent("Apply Root Motion",
                    "Apply root motion (position and rotation) from the AI Motion Synthesizer to this transform"));
            });

            EditorGUILayout.Space(4);

            AIMotionSynthesizerEditorUtils.DrawSection("Debug Visualization", HeaderColor, () =>
            {
                EditorGUILayout.PropertyField(_debugDrawSkeletonProperty, new GUIContent("Draw Skeleton"));
                if (_debugDrawSkeletonProperty.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_debugSkeletonColorProperty, new GUIContent("Skeleton Color"));
                    EditorGUI.indentLevel--;
                }
            });

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawScriptField()
        {
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.ObjectField("Script", MonoScript.FromMonoBehaviour((MonoBehaviour)target), GetType(), false);
            EditorGUI.EndDisabledGroup();
        }

        private void LoadDefaultAssets()
        {
            bool hasChanges = false;

            foreach (var (prop, path) in DefaultAssets)
            {
                var property = serializedObject.FindProperty(prop);
                if (property?.objectReferenceValue == null)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(BasePath + path);
                    if (asset != null)
                    {
                        property.objectReferenceValue = asset;
                        hasChanges = true;
                    }
                }
            }

            if (_inputProviderProperty.objectReferenceValue == null)
            {
                var provider = (AIMotionSynthesizerSourceDataProvider)target;
                foreach (var component in provider.GetComponents<MonoBehaviour>())
                {
                    if (component is IAIMotionSynthesizerInputProvider)
                    {
                        _inputProviderProperty.objectReferenceValue = component;
                        hasChanges = true;
                        break;
                    }
                }
            }

            if (hasChanges)
            {
                serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(target);
                AssetDatabase.SaveAssets();
            }
        }
    }
}
