# Meta XR Movement SDK 207.0.0 — Unity Editor bug report

## Environment

| | |
|---|---|
| Unity | 6000.3.22f1 (Apple Silicon) |
| Movement SDK | `com.meta.xr.sdk.movement` **207.0.0**, embedded at `Packages/com.meta.xr.sdk.movement/` |
| Other Meta SDKs | `com.meta.xr.sdk.all` 205.0.0 |
| Samples | `Assets/Samples/Meta XR Movement SDK/207.0.0/` |
| Render pipeline | URP 17.3.0 |
| OS | macOS (Darwin 25.6.0) |

## Method

Each defect below was reproduced by invoking the real code path in a Unity EditMode
test against the embedded v207 package, in a throwaway clone of this project. Measured
output is quoted verbatim. Where I could not reproduce something, it is in
[§7 Unverified](#7-unverified) rather than presented as fact.

## Version context — read this first

I extracted v205 from git (`git archive 4675414`) and diffed it against v207 on disk:

```
Only in V207: .Documentation
Only in V207: Samples~
Only in V207/Runtime/Native/Plugins: Mac          <- empty directory
Files differ: CHANGELOG.md, package.json
Files differ: Runtime/.../CharacterRetargeter.cs
Files differ: Runtime/.../SkeletonJobs.cs
Files differ: Runtime/.../SkeletonRetargeter.cs
Files differ: Runtime/.../SkeletonUtilities.cs
Files differ: Runtime/.../SourceProcessors/ISDKSkeletalProcessor.cs
Files differ: Shared/Scripts/UI/MovementSuggestBodyTrackingCalibration.cs
Files differ: 4 native plugin binaries, 3 UI prefabs
```

**The entire `Editor/` folder is byte-identical between v205 and v207.** Zero editor
scripts changed. Every Editor-level defect from v205 is present unchanged in v207.

v207 *did* fix two runtime defects, which I am therefore **not** reporting:
`SkeletonJobs.ApplyPoseJob` is no longer an `IJobParallelForTransform` — it is now a
plain struct invoked from a sequential loop in `SkeletonRetargeter.ApplyPose`. That
removes both the rotation-only cursor desync and the unassigned-`MappedJointMask`
scheduling failure.

---

# 1. Opening the VisemeDriver Inspector destroys your data

**Severity: High** — silent, non-undoable data loss triggered by selection alone.
**File:** `Packages/com.meta.xr.sdk.movement/Editor/Tracking/Scripts/VisemeDriverEditor.cs`

There are two independent writes. Neither is behind a button, a change check, or an
`Undo` call. The string `Undo.` does not appear anywhere in this file.

## 1a. The Viseme Mesh reference is reverted on every selection

### The code — `:28-33`

```csharp
protected virtual void OnEnable()
{
    _visemeDriver = (VisemeDriver)target;
    _visemeDriver.VisemeMesh = _visemeDriver.GetComponent<SkinnedMeshRenderer>();   // <-- line 31
    ...
}
```

`VisemeMesh` is not a helper — it is the accessor for the serialized field the
Inspector displays (`Runtime/Tracking/Scripts/Viseme/VisemeDriver.cs:22-25, :51`):

```csharp
public SkinnedMeshRenderer VisemeMesh
{
    get { return _mesh; }
    set { _mesh = value; }
}
...
[SerializeField]
protected SkinnedMeshRenderer _mesh;
```

`Editor.OnEnable` runs every time the Inspector builds an editor for the component —
i.e. every time you select the GameObject. The write goes straight to the field,
bypassing `SerializedObject`, so Unity records no undo step and does not dirty the
scene.

### Preconditions
- A GameObject with a `VisemeDriver`.
- `VisemeDriver` carries `[RequireComponent(typeof(SkinnedMeshRenderer))]`
  (`VisemeDriver.cs:16`), so adding it always creates a renderer on the same object.
- A *second* `SkinnedMeshRenderer` elsewhere — the realistic case, since face meshes
  normally live on a child, not on the root that holds the driver.

### Steps
1. `File → New Scene` → **Basic (Built-in)** → Create.
2. `GameObject → Create Empty`. Rename it **`VisemeHost`**.
3. With `VisemeHost` selected: **Add Component**, type `VisemeDriver`, press Enter.
   Unity also adds a `Skinned Mesh Renderer` to `VisemeHost` because of
   `[RequireComponent]`.
4. `GameObject → Create Empty Child` under `VisemeHost`. Rename it **`Face`**.
5. With `Face` selected: **Add Component → Skinned Mesh Renderer**.
6. Select `Face`'s Skinned Mesh Renderer → set its **Mesh** to a blendshaped mesh, e.g.
   drag `Assets/Samples/Meta XR Movement SDK/207.0.0/Face Tracking Samples/Face/Models/BlendshapeMappingExampleARKIT.fbx`
   into the Hierarchy first, then pick its skinned mesh.
7. Select **`VisemeHost`**. In the **Viseme Driver** component, find the field labelled
   **`SkinnedMeshRenderer`** *(the label is literally the type name — the editor draws
   it with `new GUIContent(nameof(SkinnedMeshRenderer))` at `:44`, not a friendly name)*.
8. Drag the **`Face`** object's Skinned Mesh Renderer into that field.
9. Click any other object in the Hierarchy (e.g. `Main Camera`).
10. Click **`VisemeHost`** again.

### Expected
The `SkinnedMeshRenderer` field still points at `Face`.

### Actual
The field has silently reverted to `VisemeHost`'s own renderer. Measured:

```
user assigns Viseme Mesh = 'Face'
after the Inspector opens: Viseme Mesh = 'VisemeHost'
reverted to the component's own SMR? YES
Undo entry created: NO - not undoable
```

11. Press **Ctrl+Z / Cmd+Z** → nothing happens. The assignment cannot be recovered, and
    the scene was never marked dirty, so there was nothing to save in the first place.

---

## 1b. The viseme mapping array is wiped during a repaint

### The code — `:63-70`

```csharp
if (_mappings.arraySize != renderer.sharedMesh.blendShapeCount)
{
    _mappings.ClearArray();                                        // <-- line 65
    _mappings.arraySize = renderer.sharedMesh.blendShapeCount;     // <-- line 66
#if META_XR_CORE_V78_MIN
    for (int i = 0; i < renderer.sharedMesh.blendShapeCount; ++i)
    {
        _mappings.GetArrayElementAtIndex(i).intValue = (int)OVRFaceExpressions.FaceViseme.Invalid;
    }
#endif
}
```

This is inside `OnInspectorGUI` (`:39`), which Unity calls many times per second while
the object is selected. Any mismatch between the stored mapping length and the current
mesh's blendshape count discards every mapping and replaces them with `Invalid`.

Measured against the source:
```
ClearArray() called inside OnInspectorGUI:        True
guarded by a user action (button/changecheck):    False
Undo.RecordObject anywhere in the editor:         False
```

### Why the mismatch is easy to hit
The two sample face meshes use different, non-overlapping blendshape sets — I read the
shape names straight out of the FBX binaries:

| Model | Naming scheme | Example shapes |
|---|---|---|
| `BlendshapeMappingExampleARKIT.fbx` | ARKit | `browDownLeft`, `browInnerUp`, `jawOpen`, `eyeBlinkLeft` |
| `Lina.fbx` | FACS | `browLowerer`, `cheekPuff`, `cheekSuck`, `jawDrop` |

Swapping one for the other — or importing an updated mesh from your DCC tool with one
shape added — changes the count and triggers the wipe.

### Steps
1. Continue from 1a, or set up a `VisemeDriver` whose `SkinnedMeshRenderer` field points
   at a mesh **that has blendshapes** (this matters: with no blendshapes the editor
   returns early at `:47-53` and never reaches the wipe).
2. In the Inspector, expand the **Blendshapes** foldout.
3. Click **Auto Generate Mapping**, or set several dropdowns by hand. Confirm the
   mappings are populated.
4. Save the scene (Ctrl+S) so you have a known-good baseline.
5. Now change the renderer's **Mesh** to one with a different blendshape count — e.g.
   swap `BlendshapeMappingExampleARKIT` for `Lina`.
6. Look at the Inspector.

### Expected
Unity warns that the mapping no longer matches, or preserves what it can, or at minimum
makes the change undoable.

### Actual
Every mapping is `Invalid`. No prompt, no console message, no undo step, and the scene
is not marked dirty — so the loss is invisible until you next play the scene.

### Suggested fix
```csharp
// Only resize on an explicit user action, and go through Undo:
if (_mappings.arraySize != renderer.sharedMesh.blendShapeCount)
{
    EditorGUILayout.HelpBox(
        $"Mapping has {_mappings.arraySize} entries but the mesh has " +
        $"{renderer.sharedMesh.blendShapeCount} blendshapes.", MessageType.Warning);
    if (GUILayout.Button("Resize mapping to match mesh"))
    {
        Undo.RecordObject(target, "Resize viseme mapping");
        _mappings.ClearArray();
        _mappings.arraySize = renderer.sharedMesh.blendShapeCount;
    }
    return;
}
```
And in `OnEnable`, only default the mesh when the field is empty, via `SerializedObject`:
```csharp
if (_mesh.objectReferenceValue == null)
{
    _mesh.objectReferenceValue = _visemeDriver.GetComponent<SkinnedMeshRenderer>();
    serializedObject.ApplyModifiedProperties();
}
```

### Cleanup
Delete `VisemeHost` and the imported FBX instance; do not save the scene.

---

# 2. MetaSourceDataProvider shows the wrong AI Motion Synthesizer help box

**Severity: Medium** — the Inspector actively states the opposite of the truth.
**File:** `Packages/com.meta.xr.sdk.movement/Editor/Native/Scripts/Retargeting/MetaSourceDataProviderEditor.cs:81`

### The code

```csharp
var skeletonType = (OVRPlugin.BodyJointSet)_providedSkeletonType.enumValueIndex;
if (skeletonType == OVRPlugin.BodyJointSet.FullBody)
{
    EditorGUILayout.HelpBox("AI Motion Synthesizer will blend with Full Body tracking.", MessageType.Info);
}
else if (skeletonType == OVRPlugin.BodyJointSet.UpperBody)
{
    EditorGUILayout.HelpBox(
        "AI Motion Synthesizer is compatible with Upper Body tracking. Lower body will use " +
        "AI Motion Synthesizer, upper body will blend based on configuration.", MessageType.Info);
}
```

### Why it is wrong

`SerializedProperty.enumValueIndex` is the **ordinal position in the enum's name list**,
not the enum's value. The enum does not start at zero
(`Library/PackageCache/com.meta.xr.sdk.core@…/Scripts/OVRPlugin.cs:2541-2547`):

```csharp
public enum BodyJointSet
{
    [InspectorName(null)] // None should not be selectable
    None = -1,
    UpperBody = 0,
    FullBody = 1,
}
```

So index and value are permanently off by one. Measured at runtime — the enum name list
Unity reports is `None, UpperBody, FullBody`:

| User picks in the dropdown | stored `intValue` | `enumValueIndex` | `(BodyJointSet)enumValueIndex` | Help box actually shown |
|---|---|---|---|---|
| **Upper Body** | 0 | 1 | `FullBody` | *"AI Motion Synthesizer will blend with **Full Body** tracking."* — **wrong** |
| **Full Body** | 1 | 2 | `2` — not a defined member | **No help box at all** — **wrong** |

There is no input for which the control behaves correctly.

### Preconditions
Any component deriving from `MetaSourceDataProvider`. Every character in every shipped
sample scene has one.

### Steps
1. Open `Assets/Samples/Meta XR Movement SDK/207.0.0/Body Tracking Samples/Scenes/MovementBody.unity`.
2. In the Hierarchy, expand **`Objects`** and select **`StylizedCharacter`**.
3. In the Inspector, find the **Meta Source Data Provider** component. It renders three
   boxed sections: **Body Tracking**, **Debug**, **AI Motion Synthesizer**.
4. Under **AI Motion Synthesizer**, tick **Enable AI Motion Synthesizer**. The help box
   only renders when this is on (`:79`).
5. Under **Body Tracking**, set **Body Joint Set** to **Upper Body**.
6. Read the help box.

### Expected
*"AI Motion Synthesizer is compatible with Upper Body tracking…"*

### Actual
*"AI Motion Synthesizer will blend with Full Body tracking."*

7. Now set **Body Joint Set** to **Full Body**.

### Expected
*"AI Motion Synthesizer will blend with Full Body tracking."*

### Actual
The help box disappears entirely.

### Suggested fix
```csharp
var skeletonType = (OVRPlugin.BodyJointSet)_providedSkeletonType.intValue;
```

### Cleanup
Untick **Enable AI Motion Synthesizer**, restore **Body Joint Set** to **Full Body**,
and close the scene without saving.

---

# 3. Unchecking "Apply Root Scale" makes "Apply Head Scale" permanently unreachable

**Severity: Medium** — a serialized setting becomes impossible to edit from the UI.
**File:** `Packages/com.meta.xr.sdk.movement/Editor/Native/Scripts/Retargeting/SkeletonRetargeterDrawer.cs`

### The property table — `:17-31`

```
index 0  _retargetingBehavior                 "Retargeting Behavior"
index 1  _hideLowerBodyWhenUpperBodyTracking  "Hide Lower Body when using Upper Body Tracking"
index 2  _hideLegScale                        "Leg Scale when using Hide Lower Body"
index 3  _applyRootScale                      "Apply Root Scale"        <- _applyRootScalePropertyIndex
index 4  _applyHeadScale                      "Apply Head Scale"        <- _applyHeadScalePropertyIndex
index 5  _headScaleFactor                     "Head Scale Multiplier"
index 6  _scaleRange                          "Scale Range"
index 7  _currentScale                        "Current Scale"           (drawn read-only)
```

### The bug — `:46-50`

```csharp
// Skip scale-related properties if scale is not applied
if (i >= _applyRootScalePropertyIndex + 1 && !applyRootScale)
{
    continue;
}
```

`_applyRootScalePropertyIndex` is `3`, so the condition is `i >= 4` — and index 4 **is
the Apply Head Scale toggle itself**. The intent (per the comment) was to hide the scale
*values*; it also swallows the independent head-scale checkbox.

### The secondary bug — `GetPropertyHeight` uses different rules, `:92-107`

```csharp
if (!applyRootScale)                    { hiddenProperties += 2; } // _scaleRange, _currentScale
if (!applyRootScale || !applyHeadScale) { hiddenProperties += 1; } // _headScaleFactor
if (!hideLowerBody)                     { hiddenProperties += 1; } // _hideLegScale
```

It never accounts for `_applyHeadScale` being hidden, so it over-reserves by one row
whenever `applyRootScale` is false.

### Measured across all eight combinations

| Apply Root Scale | Apply Head Scale | Hide Lower Body | rows drawn | rows reserved | delta |
|---|---|---|---|---|---|
| ✔ | ✔ | ✔ | 8 | 8 | 0 |
| ✔ | ✔ | ✘ | 7 | 7 | 0 |
| ✔ | ✘ | ✔ | 7 | 7 | 0 |
| ✔ | ✘ | ✘ | 6 | 6 | 0 |
| **✘** | ✔ | ✔ | 4 | 5 | **+1** |
| **✘** | ✔ | ✘ | 3 | 4 | **+1** |
| **✘** | ✘ | ✔ | 4 | 5 | **+1** |
| **✘** | ✘ | ✘ | 3 | 4 | **+1** |

With Apply Root Scale off, the only fields drawn are:
`_retargetingBehavior`, `_hideLowerBodyWhenUpperBodyTracking`, `_hideLegScale`,
`_applyRootScale` — plus one reserved-but-empty row underneath.

### Steps
1. Open `.../207.0.0/Body Tracking Samples/Scenes/MovementBody.unity`.
2. Hierarchy → expand **`Objects`** → select **`StylizedCharacter`**.
3. In the **Character Retargeter** component, scroll to the **Retargeting** section
   header. Below it the Skeleton Retargeter drawer renders its rows directly (there is
   no foldout).
4. Confirm you can see both **Apply Root Scale** (ticked) and **Apply Head Scale**
   (ticked), plus **Head Scale Multiplier**, **Scale Range**, **Current Scale**.
5. Untick **Apply Root Scale**.

### Expected
**Head Scale Multiplier**, **Scale Range** and **Current Scale** hide, while
**Apply Head Scale** remains so head scaling can still be toggled independently.

### Actual
**Apply Head Scale disappears too.** There is now no way to change it from the
Inspector — its serialized value is frozen at whatever it was. An empty row is left
where it used to be.

6. To recover, re-tick **Apply Root Scale**, or edit
   `_skeletonRetargeter._applyHeadScale` directly in the scene YAML.

### Suggested fix
```csharp
// hide only the values below the head-scale toggle
if (i >= _applyHeadScalePropertyIndex + 1 && !applyRootScale) { continue; }
```
and rebuild `GetPropertyHeight` from the same loop rather than a parallel hand-count:
```csharp
public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
{
    var visible = CountVisible(property);               // shared with OnGUI
    return EditorGUIUtility.singleLineHeight * visible +
           EditorGUIUtility.standardVerticalSpacing * (visible - 1);
}
```

### Cleanup
Re-tick **Apply Root Scale**; close the scene without saving.

---

# 4. Every `[InspectorButton]` throws on a freshly added component, and none is undoable

**Severity: High** — this is the first interaction a developer has with these components.
**File:** `Packages/com.meta.xr.sdk.movement/Runtime/Native/Scripts/Utils/InspectorButtonAttribute.cs:64-83`

### The code

```csharp
public override void OnGUI(Rect positionRect, SerializedProperty prop, GUIContent label)
{
    var inspectorButtonAttribute = (InspectorButtonAttribute)attribute;
    var rect = positionRect;
    rect.height = inspectorButtonAttribute._buttonHeight;
    if (!GUI.Button(rect, label.text))            // <-- always enabled
    {
        return;
    }
    var eventType = prop.serializedObject.targetObject.GetType();
    var eventName = inspectorButtonAttribute._methodName;
    if (_method == null)
    {
        _method = eventType.GetMethod(eventName, BindingFlags.Public | BindingFlags.NonPublic
                                               | BindingFlags.Instance | BindingFlags.Static);
    }
    _method?.Invoke(prop.serializedObject.targetObject, null);   // <-- line 82
}
```

Four problems in one drawer: the button is never disabled; there is no play-mode check;
the target methods are called with no guard on their unassigned serialized references;
and there is no `Undo.RecordObject` before or `EditorUtility.SetDirty` after.

A fifth, cosmetic: the label is `label.text`, the prettified **field** name, not the
method. The button that calls `ToggleSourceSkeletonDraw()` reads **"Toggle Source
Button"**, because the backing field is `_toggleSourceButton`.

### Measured — 16 of 17 buttons threw; 17 of 17 created no Undo entry

```
MovementBodyAnimationToggle         "Toggle Button"                       -> SwapAnimState()                        : NullReferenceException
MovementBodyTrackingFidelityToggle  "Toggle Button"                       -> SwapFidelity()                         : NullReferenceException
MovementBodyTrackingJointToggle     "Toggle Button"                       -> SwapJointSet()                         : NullReferenceException
MovementCharacterSpawnMenu          "Spawn Stylized Button"               -> AddStylizedCharacter()                 : ArgumentException
MovementCharacterSpawnMenu          "Spawn High Fidelity Button"          -> AddHighFidelityCharacter()             : ArgumentException
MovementCharacterSwapMenu           "Toggle Button"                       -> SwapToNextCharacter()                  : NullReferenceException
MovementDebugDrawSkeletonMenu       "Toggle Source Button"                -> ToggleSourceSkeletonDraw()             : NullReferenceException
MovementDebugDrawSkeletonMenu       "Toggle Target Button"                -> ToggleTargetSkeletonDraw()             : NullReferenceException
MovementDebugDrawSkeletonMenu       "Toggle Source T Pose Button"         -> ToggleSourceTPoseDraw()                : NullReferenceException
MovementDebugDrawSkeletonMenu       "Toggle Target T Pose Button"         -> ToggleTargetTPoseDraw()                : NullReferenceException
MovementHipPinningCalibration       "Trigger Calibration"                 -> Calibrate()                            : NullReferenceException
MovementRetargetingBehaviorMenu     "Cycle Button"                        -> CycleRetargetingBehavior()             : NullReferenceException
MovementRetargetingBehaviorMenu     "Source Body Proportions Button"      -> SetSourceBodyProportions()             : NullReferenceException
MovementRetargetingBehaviorMenu     "Source Body Proportions Target Hands Button" -> SetSourceBodyProportionsTargetHands() : NullReferenceException
MovementRetargetingBehaviorMenu     "Source Body Proportions Target Scale Button" -> SetSourceBodyProportionsTargetScale() : NullReferenceException
MovementRetargetingBehaviorMenu     "Target Body Proportions Button"      -> SetTargetBodyProportions()             : NullReferenceException
MovementSceneLoader                 "Load Editor Scene"                   -> LoadEditorScene()                      : "Could not load , did you build with it?"
-> 16/17 buttons threw an unhandled exception; 17/17 created no Undo entry
```

## 4a. Repro — the exception on an unconfigured component

1. `File → New Scene` → **Basic (Built-in)**.
2. `GameObject → Create Empty`.
3. **Add Component** → type `MovementDebugDrawSkeletonMenu` → Enter.
   *(You must type the class name; see §6 — no MSDK component has an
   `[AddComponentMenu]`, so none of them appear in a browsable category.)*
4. The component renders: **Debug Draw Transforms** (array), **Retargeters** (array),
   then four buttons — **Toggle Source Button**, **Toggle Target Button**,
   **Toggle Source T Pose Button**, **Toggle Target T Pose Button**.
5. Right-click the Console → **Clear**. Ensure *Collapse* is off.
6. Click **Toggle Source Button**.

### Expected
The button is greyed out while **Retargeters** is empty, or clicking reports
"no retargeters assigned".

### Actual
```
NullReferenceException: Object reference not set to an instance of an object
  Meta.XR.Movement.Samples.MovementDebugDrawSkeletonMenu.ToggleSourceSkeletonDraw ()
```
`_retargeters` is `null`; `ToggleSourceSkeletonDraw()` foreach-es it unguarded.

7. Repeat steps 2-6 with each of the other eight components in the table above.

## 4b. Repro — the silent, unsaveable edit on a *configured* component

This is the more damaging variant: on a correctly configured component the click
"works", but Unity is never told.

1. Open `.../207.0.0/Body Tracking Samples/Scenes/MovementBody.unity`.
2. Confirm the scene is **clean** — no `*` after the scene name in the Hierarchy.
3. Hierarchy → expand **`Objects`** → select **`StylizedCharacter`**. In its
   **Character Retargeter**, under the **Debugging** header, note **Debug Draw Source
   Skeleton** is **unticked**.
4. Hierarchy → expand **`UI`** → select **`SkeletonDebugMenu`**.
   *(A prefab instance of `Packages/com.meta.xr.sdk.movement/Shared/Prefabs/UI/SkeletonDebugMenu.prefab`.
   Its root carries `MovementDebugDrawSkeletonMenu`, whose `_retargeters` array is
   already wired to this scene's two `CharacterRetargeter`s. At runtime it is the
   wall panel you poke in VR.)*
5. Click **Toggle Source Button**.
6. Re-select **`Objects/StylizedCharacter`**.

### Expected
**Debug Draw Source Skeleton** is ticked, the scene shows `*`, and `Edit → Undo` offers
to revert it.

### Actual — measured
```
retargeter.DebugDrawSourceSkeleton: False -> True   (changed on 2 CharacterRetargeters)
scene dirty before=False   after=False
Undo stack top: ""
```
- The checkbox **is** ticked.
- The scene title shows **no `*`** → `File → Save` writes nothing.
- **Ctrl+Z does nothing.**

7. `File → Open Scene` → reopen `MovementBody.unity`. The checkbox is unticked again;
   your change is gone.

### The nastier variant
Because the scene is never dirtied but the in-memory object *is* modified, if you now
change anything else (move a light, nudge a transform), the scene becomes dirty and
your unintended debug-draw change is saved along with it.

### Suggested fix
```csharp
public override void OnGUI(Rect positionRect, SerializedProperty prop, GUIContent label)
{
    var attr = (InspectorButtonAttribute)attribute;
    var rect = positionRect; rect.height = attr._buttonHeight;

    using (new EditorGUI.DisabledScope(!Application.isPlaying))   // or a per-component IsReady()
    {
        if (!GUI.Button(rect, label.text)) { return; }
    }

    var target = prop.serializedObject.targetObject;
    Undo.RegisterFullObjectHierarchyUndo(target, attr._methodName);
    _method ??= target.GetType().GetMethod(attr._methodName, ...);
    _method?.Invoke(target, null);
    EditorUtility.SetDirty(target);
}
```
…and null-guard the handlers themselves.

### Cleanup
Close both scenes without saving; delete the temporary GameObject.

---

# 5. Two Face Tracking menu items throw a raw NullReferenceException, and no menu item is ever greyed out

**Severity: Medium**
**Files:**
`Packages/com.meta.xr.sdk.movement/Editor/Tracking/Scripts/HelperMenusFace.cs:20-32`
`Packages/com.meta.xr.sdk.movement/Runtime/Tracking/Scripts/AddComponentsHelper.cs:38-62, :283-286`

### The code

```csharp
[MenuItem(AddComponentsHelper._MOVEMENT_SAMPLES_MENU + _MOVEMENT_SAMPLES_FT_MENU + _A2E_FACE_MENU)]
private static void SetupCharacterForA2EFace()
{
    AddComponentsHelper.SetUpCharacterForA2EFace(Selection.activeGameObject, false);   // :24 — no null check
}
```

```csharp
public static void SetUpCharacterForA2EFace(GameObject gameObject, bool allowDuplicates = true, bool runtimeInvocation = false)
{
    try
    {
        ValidateChildGameObjectsForFaceMapping(gameObject);      // :45
    }
    catch (InvalidOperationException e)                          // :47 — only this type is caught
    { ... return; }
```

```csharp
public static void ValidateChildGameObjectsForFaceMapping(GameObject gameObject)
{
    var childRenderers = gameObject.GetComponentsInChildren<SkinnedMeshRenderer>();   // :285 — NRE here
```

With nothing selected, `Selection.activeGameObject` is `null`, `:285` dereferences it,
and the `catch` at `:47` only handles `InvalidOperationException` — so the
`NullReferenceException` escapes to the Console unhandled.

### Measured — with nothing selected, only these two are unhandled

```
[Assets/Movement SDK/Body Tracking/Open Retargeting Configuration Editor]
    validate fn: NO   result: handled: Error: An object must be selected.
[Assets/Movement SDK/Body Tracking/Run Default Retargeting Setup]
    validate fn: NO   result: handled: Error: An asset must be selected.
[GameObject/Movement SDK/Body Tracking/Add Character Retargeter]
    validate fn: NO   result: handled: Error: Must select a model to setup the skeleton retargeter on!
[GameObject/Movement SDK/Body Tracking/Create Retargeting Configuration]
    validate fn: NO   result: handled: Error: Cannot create config: Target object is null
[GameObject/Movement SDK/Body Tracking/Open Retargeting Configuration Editor]
    validate fn: NO   result: handled: Error: An object must be selected.
[GameObject/Movement SDK/Face Tracking/Add A2E ARKit Face]
    validate fn: NO   result: UNHANDLED NullReferenceException
[GameObject/Movement SDK/Face Tracking/Add A2E Face]
    validate fn: NO   result: UNHANDLED NullReferenceException
[GameObject/Movement SDK/Networking/Add Networked Character Retargeter]
    validate fn: NO   result: (no selection check at all; proceeds into the Building Block installer)
-> 9 action items, 0 with a validate function
```

The contrast matters: every sibling menu item degrades gracefully. These two do not.

### Steps
1. `File → New Scene` → **Empty**.
2. Click an empty area of the **Hierarchy** so nothing is selected. Confirm the
   Inspector is blank.
3. Right-click that empty area → **Movement SDK → Face Tracking → Add A2E Face**.
   (Equivalently: menu bar → `GameObject → Movement SDK → Face Tracking → Add A2E Face`.)

### Expected
The item is greyed out, or you get a clear message like its siblings'
*"Must select a model to setup the skeleton retargeter on!"*

### Actual
```
NullReferenceException: Object reference not set to an instance of an object
  Oculus.Movement.Utils.AddComponentsHelper.ValidateChildGameObjectsForFaceMapping (UnityEngine.GameObject gameObject)
    Packages/com.meta.xr.sdk.movement/Runtime/Tracking/Scripts/AddComponentsHelper.cs:285
  Oculus.Movement.Utils.AddComponentsHelper.SetUpCharacterForA2EFace (UnityEngine.GameObject gameObject, System.Boolean allowDuplicates, System.Boolean runtimeInvocation)
    Packages/com.meta.xr.sdk.movement/Runtime/Tracking/Scripts/AddComponentsHelper.cs:45
```

4. Repeat with **Add A2E ARKit Face** — identical NRE via `:86`.

### Second half: nothing is ever disabled
5. With nothing selected, open `GameObject → Movement SDK` and hover each submenu.
   Every one of the 9 items is enabled. **0 of 9** declare a
   `[MenuItem(path, validate: true)]` companion.

### Related, same family
`GameObject → Movement SDK → Networking → Add Networked Character Retargeter`
(`InstallMovementBuildingBlock.cs:29`) has **no selection guard at all**, while its
sibling at `:19` does:
```csharp
[MenuItem("GameObject/Movement SDK/Body Tracking/Add Character Retargeter")]
private static void InstallRetargetedCharacterBuildingBlock()
{
    if (Selection.activeGameObject == null)                    // :19 — guarded
    { Debug.LogError("Must select a model to setup the skeleton retargeter on!"); return; }
    InstallBuildingBlock("CharacterRetargeter");
}

[MenuItem("GameObject/Movement SDK/Networking/Add Networked Character Retargeter")]
private static void InstallNetworkedRetargetedCharacterBuildingBlock()
{
    InstallBuildingBlock("INetworkedCharacterRetargeter");      // :31 — unguarded
}
```

### Suggested fix
```csharp
[MenuItem(_path, true)]
private static bool ValidateSetupCharacterForA2EFace() => Selection.activeGameObject != null;

[MenuItem(_path)]
private static void SetupCharacterForA2EFace()
{
    var go = Selection.activeGameObject;
    if (go == null) { Debug.LogError("Select a character to add A2E face tracking to."); return; }
    AddComponentsHelper.SetUpCharacterForA2EFace(go, false);
}
```

---

# 6. Also confirmed, not counted among the five

Each re-verified against v207.

### 6a. Multi-object editing broken on 7 of 10 custom Inspectors
```
AIMotionSynthesizerJoystickInput       -> AIMotionSynthesizerJoystickInputEditor        NO
AIMotionSynthesizerSourceDataProvider  -> AIMotionSynthesizerSourceDataProviderEditor   NO
BodyPoseBoneTransforms                 -> BodyPoseBoneTransformsEditor                  NO
BodyPoseController                     -> BodyPoseControllerEditor                      NO
CharacterRetargeterConfig              -> CharacterRetargeterConfigEditor               NO
MetaSourceDataProvider                 -> MetaSourceDataProviderEditor                  NO
VisemeDriver                           -> VisemeDriverEditor                            NO
CharacterRetargeter                    -> CharacterRetargeterEditor                     yes
MirrorTransforms                       -> MirrorTransformsEditor                        yes
NetworkCharacterRetargeter             -> NetworkCharacterRetargeterEditor              yes
```
**Repro:** open `MovementBody.unity`, select `Objects/StylizedCharacter`, Ctrl/Cmd-click
`Objects/RealisticCharacter`. The **Meta Source Data Provider** section reads
*"Multi-object editing is not supported"*.

Worse, `CharacterRetargeterEditor` *declares* `[CanEditMultipleObjects]`
(`CharacterRetargeterEditor.cs:12`) but reads the singular `target`
(`CharacterRetargeterConfigEditor.cs:45`, `CharacterRetargeterEditor.cs:88`) while
`LoadConfig` writes through `serializedObject`, which spans all selected objects
(`CharacterRetargeterConfigEditor.cs:164-211`). Select two different characters, press
**Map**, and both receive the *first* character's joint transforms.

### 6b. 0 of 42 components declare `[AddComponentMenu]`
There is no "Movement SDK" category in **Add Component**; you must know and type the
exact class name to reach `CharacterRetargeter`, `MetaSourceDataProvider`, `FaceDriver`,
`HandDeformation`, `VisemeDriver`, and the other 37.

### 6c. 0 of 42 components declare `[DisallowMultipleComponent]`
**Repro:** Create Empty → Add Component `CharacterRetargeter` → Add Component
`CharacterRetargeter` again. Both are added. Two retargeting pipelines then drive the
same skeleton every frame, with no warning. Verified for `CharacterRetargeter`,
`MetaSourceDataProvider`, `FaceDriver`, `VisemeDriver`, `HandDeformation`,
`NetworkCharacterRetargeter`, `BodyPoseController`, `SkeletalDrawContainer`,
`RecalculateNormals`.

---

# 7. Unverified

**`CharacterRetargeterEditor` mutates the scene from inside `OnInspectorGUI`.**
`Editor/Native/Scripts/Retargeting/CharacterRetargeterEditor.cs:86-108`:

```csharp
var dataProvider = retargeter.GetComponent<ISourceDataProvider>();
var ovrBodies = retargeter.GetComponentsInChildren<OVRBody>();      // children too
if (ovrBodies is { Length: > 0 })
{
    if (dataProvider == null)
        retargeter.gameObject.AddComponent<MetaSourceDataProvider>();   // not Undo.AddComponent
    else
        foreach (var ovrBody in ovrBodies)
            if (ovrBody is not ISourceDataProvider)
                DestroyImmediate(ovrBody);                              // not Undo.DestroyObjectImmediate
}
```

If this behaves as written, merely **selecting** a `CharacterRetargeter` would add a
`MetaSourceDataProvider` and then, on a subsequent repaint, delete any plain `OVRBody`
found anywhere in its children — with no undo step. (`MetaSourceDataProvider` derives
from `OVRBody`, so the second pass sees two and removes the one that is not an
`ISourceDataProvider`.) On a prefab instance, `DestroyImmediate` of a prefab-owned
component is illegal and should raise a Unity error.

**Why I could not confirm it:** Unity refuses to create an IMGUI context without a
native window — `Editor.CreateEditor(cr).OnInspectorGUI()` fails with *"You can only
call GUI functions from inside OnGUI"* before reaching this code. A second *headed*
Editor cannot run on this Mac while your main Editor is open: both contend for the
`com.apple.metalfe` shader-module cache, producing 396 write failures and then a
segfault in `GfxDeviceMetalBase::CommonDrawSetup`. I tried the shell (sandboxed and
unsandboxed), the file reader, Finder and System Events.

**Manual repro to try yourself:**
1. New Scene → Empty. `GameObject → Create Empty`, name it `MyCharacter`.
2. Create an empty child, name it `TrackingChild`. **Add Component → OVRBody** on it.
3. Select `MyCharacter` → **Add Component → CharacterRetargeter**.
4. Click `MyCharacter` in the Hierarchy and leave the Inspector open for a second.
5. Inspect both objects; check `Edit → Undo`.

Watch for a `MetaSourceDataProvider` you never added, the child's `OVRBody` vanishing,
and Undo being unable to restore it.

---

# 8. Environment note — macOS

`Packages/com.meta.xr.sdk.movement/Runtime/Native/Plugins/Mac/` is **empty** in v207,
exactly as in v205. Win64 (5 files), Linux (1) and Android (3) all ship binaries;
macOS ships none, and `MSDKUtility` has no `DllNotFoundException` guard — unlike
`MSDKAIMotionSynthesizer.cs:240,336`, which does catch it.

Consequence in the macOS Editor: every native retargeting entry point throws, including
`CharacterRetargeterConfig.OnValidate()` → `ValidateConfiguration()`, which fires on
every inspector edit and every scene load:

```
Character Retargeter Configuration Error: Failed to validate configuration:
MetaMovementSDK_Utility assembly:<unknown assembly> type:<unknown type> member:(null)
```

This is why opening any Movement sample scene on this machine logs an error. It does not
affect the Windows PC or on-device builds.

---

# 9. Reproducing the automated checks

The verification harness is EditMode-only and gated behind `UNITY_INCLUDE_TESTS`, so it
never enters a build.

```
Unity -batchmode -nographics -projectPath <project> \
      -runTests -testPlatform EditMode \
      -testResults results.xml -logFile run.log
```

To re-derive the v205↔v207 comparison:
```
git archive 4675414 Packages/com.meta.xr.sdk.movement | tar -x -C /tmp/v205pkg
diff -rq /tmp/v205pkg/Packages/com.meta.xr.sdk.movement Packages/com.meta.xr.sdk.movement
```
