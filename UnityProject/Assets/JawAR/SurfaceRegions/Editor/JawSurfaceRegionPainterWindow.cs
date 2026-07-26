using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BMC.JawAR.SurfaceRegions.Editor
{
    public sealed class JawSurfaceRegionPainterWindow : EditorWindow
    {
        private enum PaintMode { Add, Erase, InspectQuery }
        private enum OverlayMode { ShowAllRegions, ShowSelectedOnly, Hidden }

        private MeshFilter targetMeshFilter;
        private SkinnedMeshRenderer targetSkinnedRenderer;
        private MeshCollider targetCollider;
        private JawSurfaceRegionMap regionMap;
        private JawSurfaceMeshCache meshCache;
        private Mesh cachedMesh;
        private PaintMode paintMode;
        private OverlayMode overlayMode;
        private int selectedRegionIndex;
        private float brushRadius = 0.006f;
        private float normalAngleThreshold = 55f;
        private float overlayOpacity = 0.42f;
        private bool reassignPaintedTriangles;
        private Vector2 regionScroll;
        private int hoveredTriangle = -1;
        private RaycastHit hoveredHit;
        private bool hasHover;
        private bool unsavedChanges;
        private string savedJson;
        private JawSurfaceRegionMap snapshotMap;
        private int paintControlId;
        private bool strokeActive;
        private int undoGroup;
        private readonly Dictionary<string, Mesh> previewOverlayMeshes = new();
        private Material previewOverlayMaterial;

        private Mesh TargetMesh => targetMeshFilter != null
            ? targetMeshFilter.sharedMesh
            : targetSkinnedRenderer != null ? targetSkinnedRenderer.sharedMesh : null;
        private Transform TargetTransform => targetCollider != null ? targetCollider.transform :
            targetMeshFilter != null ? targetMeshFilter.transform : targetSkinnedRenderer?.transform;
        private JawSurfaceRegionMap.RegionDefinition SelectedRegion => regionMap != null &&
            selectedRegionIndex >= 0 && selectedRegionIndex < regionMap.Regions.Count
                ? regionMap.Regions[selectedRegionIndex]
                : null;

        [MenuItem("Tools/Jaw Anatomy/Surface Region Painter")]
        public static void Open() => GetWindow<JawSurfaceRegionPainterWindow>("Jaw Surface Painter");

        private void OnEnable()
        {
            SceneView.duringSceneGui += DuringSceneGui;
            Undo.undoRedoPerformed += OnUndoRedo;
            minSize = new Vector2(390f, 650f);
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader != null)
            {
                previewOverlayMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                previewOverlayMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                previewOverlayMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                previewOverlayMaterial.SetInt("_Cull", (int)CullMode.Off);
                previewOverlayMaterial.SetInt("_ZWrite", 0);
            }
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DuringSceneGui;
            Undo.undoRedoPerformed -= OnUndoRedo;
            meshCache?.Dispose();
            meshCache = null;
            ClearPreviewOverlayMeshes();
            if (previewOverlayMaterial != null) DestroyImmediate(previewOverlayMaterial);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Select the jaw MeshFilter and its experimental MeshCollider. Add paints; Shift+drag erases. " +
                "Hold Alt for normal Scene-view orbit/pan. Ctrl/Cmd+wheel or [ ] changes brush size.",
                MessageType.Info);

            if (GUILayout.Button("Use Experimental Scene Jaw + Map"))
            {
                var target = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionTarget>();
                if (target != null) SetTargets(target.meshFilter, target.meshCollider, target.regionMap);
                else EditorUtility.DisplayDialog("Experimental target not found",
                    "Open JawArUcoAnatomy_SurfacePaint_AR.unity first.", "OK");
            }

            EditorGUI.BeginChangeCheck();
            targetMeshFilter = (MeshFilter)EditorGUILayout.ObjectField("Jaw MeshFilter", targetMeshFilter,
                typeof(MeshFilter), true);
            targetSkinnedRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Or Skinned Renderer",
                targetSkinnedRenderer, typeof(SkinnedMeshRenderer), true);
            targetCollider = (MeshCollider)EditorGUILayout.ObjectField("Jaw MeshCollider", targetCollider,
                typeof(MeshCollider), true);
            if (EditorGUI.EndChangeCheck())
            {
                if (targetMeshFilter != null) targetSkinnedRenderer = null;
                ResetCache();
            }

            EditorGUILayout.BeginHorizontal();
            regionMap = (JawSurfaceRegionMap)EditorGUILayout.ObjectField("Region Map", regionMap,
                typeof(JawSurfaceRegionMap), false);
            if (GUILayout.Button("Create", GUILayout.Width(62f))) CreateMapAsset();
            EditorGUILayout.EndHorizontal();
            TrackMapSnapshot();

            var valid = DrawValidation();
            EditorGUI.BeginDisabledGroup(!valid);
            paintMode = (PaintMode)GUILayout.Toolbar((int)paintMode, new[] { "Add", "Erase", "Inspect / Query" });
            overlayMode = (OverlayMode)GUILayout.Toolbar((int)overlayMode,
                new[] { "All overlays", "Selected only", "Hide" });

            brushRadius = EditorGUILayout.Slider(new GUIContent("Brush radius (metres)",
                "Connected-surface geodesic radius, not a through-mesh sphere."), brushRadius, 0.0005f, 0.03f);
            normalAngleThreshold = EditorGUILayout.Slider(new GUIContent("Normal-angle threshold",
                "Stops brush traversal across sharp folds."), normalAngleThreshold, 5f, 180f);
            overlayOpacity = EditorGUILayout.Slider("Overlay opacity", overlayOpacity, 0.05f, 0.9f);
            reassignPaintedTriangles = EditorGUILayout.ToggleLeft(
                "Reassign painted triangles owned by another region", reassignPaintedTriangles);

            DrawRegions();
            DrawCountsAndHover();
            DrawColorEditor();

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(unsavedChanges ? "Save Region Map *" : "Save Region Map")) SaveMap();
            if (GUILayout.Button("Revert Unsaved Changes")) RevertMap();
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button("Clear Selected Region")) ClearSelectedRegion();
            EditorGUI.EndDisabledGroup();

            if (!valid)
                EditorGUILayout.HelpBox("Painting is locked until the renderer mesh, MeshCollider topology, and saved map signature all match.",
                    MessageType.Warning);
            Repaint();
        }

        private bool DrawValidation()
        {
            if (TargetMesh == null || targetCollider == null || regionMap == null)
            {
                EditorGUILayout.HelpBox("Assign a jaw renderer, its MeshCollider, and a region map.", MessageType.Warning);
                return false;
            }
            if (targetCollider.sharedMesh == null)
            {
                EditorGUILayout.HelpBox("The MeshCollider has no shared mesh.", MessageType.Error);
                return false;
            }
            try { EnsureCache(); }
            catch (Exception exception)
            {
                EditorGUILayout.HelpBox("Mesh read failed: " + exception.Message, MessageType.Error);
                return false;
            }
            var issues = regionMap.ValidateMesh(TargetMesh, targetCollider.sharedMesh, meshCache.Signature, out var message);
            EditorGUILayout.HelpBox(message, issues == JawSurfaceRegionMap.MeshValidationIssue.None
                ? MessageType.Info : MessageType.Error);
            if (targetCollider.transform != (targetMeshFilter != null ? targetMeshFilter.transform : targetSkinnedRenderer.transform))
            {
                EditorGUILayout.HelpBox("Renderer and collider transforms differ; highlighted collider triangles cannot safely represent the rendered surface.",
                    MessageType.Error);
                return false;
            }
            return issues == JawSurfaceRegionMap.MeshValidationIssue.None;
        }

        private void DrawRegions()
        {
            if (regionMap == null) return;
            EditorGUILayout.LabelField("Anatomical regions (initially empty)", EditorStyles.boldLabel);
            regionScroll = EditorGUILayout.BeginScrollView(regionScroll, GUILayout.Height(190f));
            for (var i = 0; i < regionMap.Regions.Count; i++)
            {
                var region = regionMap.Regions[i];
                var old = GUI.backgroundColor;
                GUI.backgroundColor = region.DisplayColor;
                if (GUILayout.Toggle(selectedRegionIndex == i,
                        $"{region.DisplayName}  [{region.StableId}]  ({region.TriangleCount:N0})", "Button"))
                    selectedRegionIndex = i;
                GUI.backgroundColor = old;
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawCountsAndHover()
        {
            var selectedCount = SelectedRegion?.TriangleCount ?? 0;
            EditorGUILayout.LabelField("Selected triangle count", selectedCount.ToString("N0"));
            EditorGUILayout.LabelField("Total labelled triangles", regionMap?.TotalLabelledTriangleCount.ToString("N0") ?? "0");
            EditorGUILayout.LabelField("Overlapping triangles", regionMap?.OverlappingTriangleCount.ToString("N0") ?? "0");
            EditorGUILayout.LabelField("Hovered triangle index", hasHover ? hoveredTriangle.ToString("N0") : "None");
            if (hasHover && regionMap != null && regionMap.TryGetRegionForTriangle(hoveredTriangle, out var owner))
                EditorGUILayout.LabelField("Hovered region", $"{owner.DisplayName} [{owner.StableId}]");
            else
                EditorGUILayout.LabelField("Hovered region", "Unlabelled");
            if (hasHover && meshCache != null)
                EditorGUILayout.LabelField("Hovered submesh", meshCache.GetSubmeshForTriangle(hoveredTriangle).ToString());
        }

        private void DrawColorEditor()
        {
            var region = SelectedRegion;
            if (region == null) return;
            EditorGUI.BeginChangeCheck();
            var color = EditorGUILayout.ColorField("Selected region colour", region.DisplayColor);
            if (!EditorGUI.EndChangeCheck()) return;
            Undo.RecordObject(regionMap, "Change surface region colour");
            regionMap.SetRegionColor(region.StableId, color);
            MarkUnsaved();
        }

        private void DuringSceneGui(SceneView sceneView)
        {
            if (!CanPaint()) return;
            var current = Event.current;
            if (current == null) return;
            paintControlId = GUIUtility.GetControlID("JawSurfaceRegionPainter".GetHashCode(), FocusType.Passive);
            UpdateHover(current.mousePosition);
            DrawOverlays();
            DrawBrushAndHoveredTriangle();

            if (current.alt) return;
            if (current.type == EventType.Layout) HandleUtility.AddDefaultControl(paintControlId);

            if (current.type == EventType.ScrollWheel && (current.control || current.command))
            {
                brushRadius = Mathf.Clamp(brushRadius * (current.delta.y > 0f ? 0.9f : 1.1f), 0.0005f, 0.03f);
                current.Use(); Repaint(); return;
            }
            if (current.type == EventType.KeyDown && (current.keyCode == KeyCode.LeftBracket || current.keyCode == KeyCode.RightBracket))
            {
                brushRadius = Mathf.Clamp(brushRadius * (current.keyCode == KeyCode.LeftBracket ? 0.9f : 1.1f), 0.0005f, 0.03f);
                current.Use(); Repaint(); return;
            }
            if (current.button != 0) return;

            if (current.type == EventType.MouseDown && hasHover)
            {
                if (paintMode == PaintMode.InspectQuery)
                {
                    LogQuery();
                    current.Use();
                    return;
                }
                strokeActive = true;
                GUIUtility.hotControl = paintControlId;
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(current.shift || paintMode == PaintMode.Erase
                    ? "Erase jaw surface region" : "Paint jaw surface region");
                Undo.RecordObject(regionMap, Undo.GetCurrentGroupName());
                ApplyBrush(current.shift || paintMode == PaintMode.Erase);
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && strokeActive && GUIUtility.hotControl == paintControlId)
            {
                if (hasHover) ApplyBrush(current.shift || paintMode == PaintMode.Erase);
                current.Use();
            }
            else if (current.type == EventType.MouseUp && strokeActive)
            {
                strokeActive = false;
                GUIUtility.hotControl = 0;
                Undo.CollapseUndoOperations(undoGroup);
                current.Use();
            }
            sceneView.Repaint();
        }

        private void ApplyBrush(bool erase)
        {
            var region = SelectedRegion;
            if (region == null) return;
            var triangles = meshCache.CollectConnectedBrush(hoveredTriangle, TargetTransform,
                brushRadius, normalAngleThreshold);
            var changed = false;
            foreach (var triangle in triangles)
            {
                if (erase) changed |= regionMap.EraseTriangle(region.StableId, triangle);
                else changed |= regionMap.AssignTriangle(region.StableId, triangle, reassignPaintedTriangles, out _);
            }
            if (changed) MarkUnsaved();
        }

        private void DrawOverlays()
        {
            if (overlayMode == OverlayMode.Hidden || regionMap == null || previewOverlayMaterial == null) return;
            foreach (var region in regionMap.Regions)
            {
                if (overlayMode == OverlayMode.ShowSelectedOnly && region != SelectedRegion) continue;
                if (region.TriangleCount == 0) continue;
                if (!previewOverlayMeshes.TryGetValue(region.StableId, out var overlay) || overlay == null)
                {
                    overlay = meshCache.BuildOverlayMesh(region.TriangleIndices,
                        "JawSurfaceEditorPreview_" + region.StableId, JawSurfaceRegionAssetUtility.OverlayOffset);
                    if (overlay == null) continue;
                    overlay.hideFlags = HideFlags.HideAndDontSave;
                    previewOverlayMeshes[region.StableId] = overlay;
                }
                var color = region.DisplayColor;
                color.a = overlayOpacity;
                previewOverlayMaterial.SetColor("_Color", color);
                previewOverlayMaterial.SetPass(0);
                Graphics.DrawMeshNow(overlay, TargetTransform.localToWorldMatrix);
            }
        }

        private void DrawBrushAndHoveredTriangle()
        {
            if (!hasHover) return;
            Handles.color = Color.yellow;
            meshCache.GetTriangleWorld(hoveredTriangle, TargetTransform,
                JawSurfaceRegionAssetUtility.OverlayOffset * 1.5f, out var a, out var b, out var c);
            Handles.DrawAAPolyLine(3f, a, b, c, a);
            Handles.DrawWireDisc(hoveredHit.point, hoveredHit.normal, brushRadius);
        }

        private void UpdateHover(Vector2 mousePosition)
        {
            hasHover = targetCollider.Raycast(HandleUtility.GUIPointToWorldRay(mousePosition), out hoveredHit, 1000f);
            hoveredTriangle = hasHover ? hoveredHit.triangleIndex : -1;
        }

        private void LogQuery()
        {
            var label = regionMap.TryGetRegionForTriangle(hoveredTriangle, out var region)
                ? $"{region.DisplayName} [{region.StableId}]" : "Unlabelled";
            Debug.Log($"JAW_SURFACE_QUERY: triangle={hoveredTriangle} submesh={meshCache.GetSubmeshForTriangle(hoveredTriangle)} label={label}");
        }

        private void SaveMap()
        {
            EnsureCache();
            JawSurfaceRegionAssetUtility.RebuildPersistentOverlays(regionMap, meshCache);
            EditorUtility.SetDirty(regionMap);
            AssetDatabase.SaveAssets();
            savedJson = EditorJsonUtility.ToJson(regionMap);
            unsavedChanges = false;
            ClearPreviewOverlayMeshes();
            SceneView.RepaintAll();
        }

        private void RevertMap()
        {
            if (!unsavedChanges || string.IsNullOrEmpty(savedJson)) return;
            if (!EditorUtility.DisplayDialog("Revert unsaved surface painting?",
                    "This restores the last state loaded or saved in this painter window.", "Revert", "Cancel")) return;
            Undo.RecordObject(regionMap, "Revert unsaved surface-region changes");
            EditorJsonUtility.FromJsonOverwrite(savedJson, regionMap);
            EditorUtility.SetDirty(regionMap);
            unsavedChanges = false;
            ClearPreviewOverlayMeshes();
            SceneView.RepaintAll();
        }

        private void ClearSelectedRegion()
        {
            var region = SelectedRegion;
            if (region == null || region.TriangleCount == 0) return;
            if (!EditorUtility.DisplayDialog("Clear selected surface region?",
                    $"Remove all {region.TriangleCount:N0} triangles from {region.DisplayName}?", "Clear", "Cancel")) return;
            Undo.RecordObject(regionMap, "Clear jaw surface region");
            if (regionMap.ClearRegion(region.StableId)) MarkUnsaved();
        }

        private void CreateMapAsset()
        {
            if (TargetMesh == null)
            {
                EditorUtility.DisplayDialog("Select jaw mesh", "Assign the jaw MeshFilter or SkinnedMeshRenderer first.", "OK");
                return;
            }
            EnsureCache();
            var path = EditorUtility.SaveFilePanelInProject("Create Jaw Surface Region Map",
                "JawSurfaceRegionMap", "asset", "Save the persistent triangle labels.",
                "Assets/JawAR/SurfaceRegions/Data");
            if (string.IsNullOrEmpty(path)) return;
            var map = CreateInstance<JawSurfaceRegionMap>();
            map.InitializeDefaultRegions();
            JawSurfaceRegionAssetUtility.BindMapToMesh(map, TargetMesh, meshCache);
            AssetDatabase.CreateAsset(map, path);
            AssetDatabase.SaveAssets();
            regionMap = map;
            savedJson = EditorJsonUtility.ToJson(map);
            snapshotMap = map;
            unsavedChanges = false;
        }

        private void TrackMapSnapshot()
        {
            if (regionMap == snapshotMap) return;
            snapshotMap = regionMap;
            savedJson = regionMap != null ? EditorJsonUtility.ToJson(regionMap) : null;
            unsavedChanges = false;
            ClearPreviewOverlayMeshes();
        }

        private void MarkUnsaved()
        {
            unsavedChanges = true;
            EditorUtility.SetDirty(regionMap);
            ClearPreviewOverlayMeshes();
            SceneView.RepaintAll();
            Repaint();
        }

        private bool CanPaint()
        {
            if (TargetMesh == null || targetCollider == null || regionMap == null || targetCollider.sharedMesh != TargetMesh)
                return false;
            try { EnsureCache(); }
            catch { return false; }
            return regionMap.ValidateMesh(TargetMesh, targetCollider.sharedMesh, meshCache.Signature, out _) ==
                   JawSurfaceRegionMap.MeshValidationIssue.None;
        }

        private void EnsureCache()
        {
            if (TargetMesh == null) throw new InvalidOperationException("Target mesh is missing.");
            if (meshCache != null && cachedMesh == TargetMesh) return;
            ResetCache();
            cachedMesh = TargetMesh;
            meshCache = new JawSurfaceMeshCache(TargetMesh);
        }

        private void ResetCache()
        {
            meshCache?.Dispose();
            meshCache = null;
            cachedMesh = null;
            hasHover = false;
            ClearPreviewOverlayMeshes();
        }

        private void OnUndoRedo()
        {
            unsavedChanges = true;
            ClearPreviewOverlayMeshes();
            SceneView.RepaintAll();
            Repaint();
        }

        public void SetTargets(MeshFilter meshFilter, MeshCollider collider, JawSurfaceRegionMap map)
        {
            targetMeshFilter = meshFilter;
            targetSkinnedRenderer = null;
            targetCollider = collider;
            regionMap = map;
            ResetCache();
            TrackMapSnapshot();
        }

        private void ClearPreviewOverlayMeshes()
        {
            foreach (var overlay in previewOverlayMeshes.Values)
                if (overlay != null) DestroyImmediate(overlay);
            previewOverlayMeshes.Clear();
        }
    }
}
