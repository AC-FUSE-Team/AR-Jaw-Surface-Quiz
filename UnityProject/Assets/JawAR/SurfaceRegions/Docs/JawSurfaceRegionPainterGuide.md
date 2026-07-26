# Jaw Surface Region Painter Guide

This is an experimental alternative to the existing box hitboxes. It does not replace them. The original working scene remains `Assets/Scenes/JawArUcoAnatomy_AR.unity`; all surface work belongs in `Assets/Scenes/JawArUcoAnatomy_SurfacePaint_AR.unity`.

The experimental scene currently uses `JawSurfaceRegionMap_CodexDraft.asset`, a fully editable Codex draft guided by the local references in `/home/omar/JawRepair/Images of Parts of Skull I want/`. The untouched all-empty starting point is preserved as `JawSurfaceRegionMap.asset`.

The draft uses one label per triangle. Where a muscle attachment overlaps a broad bony region, the more specific attachment label owns the triangle and the broad region uses the surrounding remainder. This makes every tap result unambiguous. All selections are starting boundaries for review, not locked anatomy.

## 1. Open the safe experimental scene

1. In Unity's Project window, open `Assets/Scenes`.
2. Double-click `JawArUcoAnatomy_SurfacePaint_AR`.
3. Confirm the Hierarchy contains `JawMarkerAlignedRoot`, `AnatomyHitboxes_EDITABLE`, and `SurfaceRegions_EXPERIMENTAL`.
4. Never paint in or save changes to `JawArUcoAnatomy_AR` for this experiment.

## 2. Open and connect the painter

1. Choose **Tools > Jaw Anatomy > Surface Region Painter**.
2. Click **Use Experimental Scene Jaw + Map** at the top of the window.
3. The mesh-validation box must say **Valid** before painting is enabled.
4. The fields should show the jaw `MeshFilter`, its experimental `MeshCollider`, and `JawSurfaceRegionMap`.

If a map is ever missing, assign the jaw first, click **Create**, and save it under `Assets/JawAR/SurfaceRegions/Data`. Do not bind an old map when the tool reports a topology mismatch.

## 3. Review or adjust LowerIncisors first

1. In the region list, click **Lower Incisors [LowerIncisors]**.
2. Choose **Add**.
3. Start with a brush radius near `0.003` metres and a normal-angle threshold near `45–55°`.
4. Choose **Selected only** to view the cyan Codex draft on the four central lower teeth.
5. Point at a visible lower incisor triangle in the Scene view. The exact collider triangle is outlined yellow.
6. Use **Add** to extend the draft or **Erase**/Shift+drag to remove triangles you do not want.
7. Rotate the jaw frequently to inspect the boundary; hold **Alt** while using Unity's normal Scene-view orbit controls so the painter does not paint.
8. Use **Inspect / Query**, click a painted triangle, and check the Console for `JAW_SURFACE_QUERY` with `LowerIncisors`.
9. Click **Save Region Map** to save your adjustment to the draft asset.

The Codex draft is only a starting selection. You remain in control of the final anatomical boundary.

## 4. Erase mistakes and resize the brush

- Choose **Erase** and left-drag, or hold **Shift** while left-dragging in Add mode.
- Use the brush-radius slider.
- With the cursor in the Scene view, use **Ctrl/Cmd + mouse wheel** or the **[** and **]** keys.
- Use **Edit > Undo** / **Ctrl/Cmd+Z** to undo a stroke, erase, clear, reassignment, or colour edit.
- **Revert Unsaved Changes** returns to the last state loaded or saved by this painter window.

To subtract from any Codex selection later:

1. Select that region in the list.
2. Choose **Erase**, or keep **Add** selected and hold **Shift**.
3. Left-drag over the unwanted coloured triangles.
4. Use **Edit > Undo** immediately if you remove too much.
5. Click **Save Region Map** only when the revised boundary looks right.

The command **Tools > Jaw Anatomy > Regenerate Full Editable Codex Draft** deliberately replaces manual edits to the draft and therefore asks for confirmation. Do not run it after hand-tuning unless you intentionally want to return to the generated starting selections.

The brush expands only through edge-connected triangles and accumulates distance along the surface. It stops at the selected normal-angle threshold, which helps prevent spills across sharp folds. The initial ray hit is the nearest surface of the selected jaw collider, so the brush does not start on the hidden rear surface.

## 5. Review or adjust LeftRamus second

1. Save LowerIncisors first.
2. Select **Left Ramus [LeftRamus]**.
3. Leave **Reassign painted triangles owned by another region** off initially.
4. Rotate to a clear lateral view of the anatomical-left/model−X ramus. The draft is the posterior plate and angle; condylar and coronoid tips are intentionally excluded because they have separate region IDs.
5. Choose **Add** and use a broader radius, around `0.005–0.009` metres.
6. Paint in short strokes while checking the coloured footprint.
7. If a triangle already belongs to LowerIncisors, the default policy refuses to change it. Turn on the explicit reassign option only if that ownership really should move.
8. Erase boundary mistakes, use **Inspect / Query** to confirm `LeftRamus`, then click **Save Region Map**.

Do not begin with MentalForamen; it is small and is a poor first validation target.

## 6. Overlay controls and colour legend

- **All overlays** shows every labelled region.
- **Selected only** shows only the current region.
- **Hide** turns overlays off without deleting data.
- **Overlay opacity** changes only Editor visualization.
- Each region button uses its display colour. Change the selected region colour with **Selected region colour**.

The original mesh, material, vertices, UVs, normals, STL/OBJ, and import settings are never rewritten. Scene overlays are drawn by Editor handles with a `0.12 mm` visual offset. Saving builds sparse per-region overlay submeshes only for labelled triangles; these use one shared runtime flash material and do not affect collider geometry.

## 7. Save and persistence

Triangle memberships are sorted integer arrays inside `JawSurfaceRegionMap.asset`, one array per stable region ID. The data survives scene changes and closing Unity. The tool does not save during every mouse move: an asterisk on **Save Region Map \*** means memory contains unsaved edits.

The current single-label policy allows only one region owner per triangle. Specific landmarks and muscle attachments take priority over broad bone regions in the generated draft. The representation keeps regions separate, so a future data version could add multi-label semantics without changing stable region IDs.

To abandon all draft painting without deleting it, choose **Tools > Jaw Anatomy > Use Untouched Empty Baseline Map**. To return to the draft, choose **Tools > Jaw Anatomy > Use Editable Codex Draft Map**. Both commands affect only the duplicate experimental scene and keep surface lookup disabled.

## 8. Triangle diagnostics

1. Choose **Inspect / Query**.
2. Hover a jaw triangle and read its triangle index, submesh, and owner in the window.
3. Click it to log the collider `RaycastHit.triangleIndex` and stable label.
4. The yellow outline is built from that same flattened collider triangle index.

The painter requires the renderer and collider to reference the exact same mesh and transform. It refuses to paint when they differ. Unity's collider triangle index is treated as a flattened list in submesh order; the stored submesh index counts and full mesh signature validate that convention.

## 9. Test box and surface selection modes

The duplicate scene starts safely with:

- `JawSurfaceRegionSelectionCoordinator` disabled
- selection mode `ExistingBoxesOnly`
- `JawSurfaceRegionTarget.surfaceLookupEnabled` off

This leaves the existing box behavior as the default. After Editor query validation:

1. Select `SurfaceRegions_EXPERIMENTAL` and enable **Surface Lookup Enabled** on `JawSurfaceRegionTarget`.
2. Select `JawMarkerAlignedRoot` and enable `JawSurfaceRegionSelectionCoordinator`.
3. Choose one of `ExistingBoxesOnly`, `SurfaceRegionsOnly`, `SurfaceThenBoxes`, or `BoxesThenSurface`.
4. Return both settings to their safe defaults if a surface test is inconclusive.

Saved sparse region overlay meshes let a valid surface selection flash only that region orange. The base jaw material is untouched and no mesh/material is rebuilt every frame.

## 10. Mesh-change warnings

The map stores the source mesh reference, asset GUID, name, vertex count, triangle count, submesh count, per-submesh index counts, and SHA-256 signature of all vertex positions and flattened triangle indices. If any check changes, painting and lookup are blocked. Do not dismiss this as cosmetic: triangle labels can point to different anatomy after a reimport or topology change. Create a new map or deliberately migrate/repaint after investigating the source change.

Read/Write remains disabled on the imported OBJ. The Editor reads mesh data through Unity's read-only mesh-data API. Runtime lookup needs only the precomputed integer labels and the `MeshCollider` triangle index, avoiding a permanent CPU-readable copy of the complete jaw mesh.

## 11. Return to the untouched working scene

1. Save the region map if wanted.
2. Open `Assets/Scenes/JawArUcoAnatomy_AR.unity`.
3. The original scene has no surface-region components or jaw MeshCollider added by this experiment.
4. The original `AnatomyHitboxes_EDITABLE` transforms, colliders, labels, tap feedback, application settings, and APK are independent of this tool.

## Performance notes

The current imported jaw has 82,652 vertices, 165,316 triangles, and one submesh. The persistent label payload is approximately four bytes per labelled triangle plus small per-region/YAML overhead: labelling every triangle once is roughly 646 KiB of integer data before Unity text-serialization overhead. Editor adjacency uses three integer neighbor slots per triangle (about 1.9 MiB) plus a temporary edge dictionary and mesh caches while the painter is open.

Runtime label lookup is dictionary-based and the collider uses one mesh, not one collider per triangle. Sparse overlays duplicate only labelled triangle positions (three vertices per labelled triangle) and are cached on Save. This is reasonable for limited educational regions on a Galaxy Note 9, but painting nearly all 165k triangles would increase overlay memory substantially; keep regions bounded and profile before enabling surface mode in an Android build.

No Android build is part of this workflow.
