# Printable Jaw + ArUco Guide

- Units: **millimetres** (STL itself is unitless; choose mm in the slicer).
- Assembly envelope: approximately 117.55 × 157.44 × 65.86 mm.
- Marker: OpenCV DICT_5X5_50, ID 1.
- Marker black outer square: 56.0 mm.
- Cell size: 8.0 mm; black border: one cell.
- White quiet zone: at least 8.0 mm on every side.
- Marker center in model coordinates: (63.816292, -62.000000, 43.899998) mm.
- Inlay depth: 0.6 mm; black and white finish flush at Z=43.899998 mm.

## Printing

Load `HumanSkull_Jaw_ArUco_WHITE.stl` and `HumanSkull_Jaw_ArUco_BLACK.stl`
together as parts of one multi-material object; do not auto-center them separately.
Assign white to WHITE and matte black to BLACK. The geometry shares an identical
coordinate system and is already registered. Use a 0.2 mm layer height so the
0.6 mm inlay is exactly three layers. A 0.4 mm nozzle is appropriate.

For a single-extruder printer, print the white body and black inlay separately only
if your slicer cannot perform a filament/color assignment; the black geometry is
made of multiple marker regions and is best handled as a multi-part color object.

Keep the marker top flat, clean, and matte. Avoid glossy filament, elephant-foot
expansion into white cells, supports on the marker, paint bleed, and sanding that
rounds the square corners. Verify detection from the intended camera and distance
before relying on pose estimates.

White mesh: {'vertices': 75649, 'edges': 224552, 'faces': 148897, 'components': 1, 'boundary': 0, 'non_manifold': 0, 'degenerate': 0}

Black mesh: {'vertices': 126, 'edges': 264, 'faces': 132, 'components': 1, 'boundary': 0, 'non_manifold': 0, 'degenerate': 0}
