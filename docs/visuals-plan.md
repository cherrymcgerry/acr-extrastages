# Textured visuals without the editor: plan (nz01r5)

Status: v1.2 renders DR2 geometry in flat debug colours via the start-trigger ProceduralMeshComponent (PMC), verified
in game; v1.3 adds the objects (docs/decisions.md D6, D7). This plan replaces the flat colours with textured materials
without the UE editor. Step 1 is confirmed in game; step 2 is built in v1.7 (in-game test pending).

## What we have to work with (verified offline)

- **The visuals mesh:** any number of sections, each with its own material slot.
  - Per vertex: position, normal, tangent, colour and UV0–UV3. We write them, and DR2's glbs carry UVs.
  - A PMC draws through the same local vertex factory as a static mesh, so any material usable on Hafren's static
    meshes should work on it. That is inferred; the proof below tests it.
- **Hafren's terrain is static meshes, not a landscape:** 34 tiles, `SM_WalesS4HafrenSouthTerrainNNNN`.
  - Each tile has an `MI_Terrain9MatBlend<tile>` (parent `MI_HafrenSouthTerrainFirstChild`), a 9-material blend
    driven by per-tile textures `T_HafrenSouth<tile>_M1`, `_M2` (masks) and `_N` (normal). `TileSize(meters)` is 350.
  - The masks are Hafren-only: they live in `Environments/WalesS4HafrenSouth/TerrainMasks`, one set per tile. Each
    is 2048², DXT5, `TC_Masks`, clamped, and **virtual-texture streamed**: VTData, 128 px tiles, 4 px border,
    12 mips, RawGPU.
- **Hafren's road:** `MI_WalesS4HafrenSouthGravelRoad1` (parent `M_RoadSurface_Layered`) blends 3 gravel layers.
  - Its BaseColor, normal and packed textures come from `Environments/WalesShared` (shared by all Wales stages, so
    don't overwrite them), plus a macro/puddle mask.
- **DR2 side:** `render/terrain.glb` (71 MB) has the terrain with UVs and materials (`terrain.materials.json`), and
  `render/objects.glb` has object meshes with UVs. Their DDS textures come from the DR2 extractor.

## Steps

1. **Material proof [V in game, during v1.3]:** `work\acr\tex-proof\dist` is v1.3 with `MI_Terrain9MatBlend1001` on
   the terrain section and `MI_WalesS4HafrenSouthGravelRoad1` on the road section. Config:
   `work\acr\tex-proof\visuals-texproof.toml`, built with `build.ps1 -Version v1.3 -VisualsConfig ...`.
   - Result: both materials render on the PMC, lit and textured, and the mapping looked right. The v1.3 green
     clutter made the detail hard to judge.
2. **Road textured (v1.7, built; in-game test pending):** the tex-proof materials on the v1.6 visuals plus
   road-following UVs (`[textured]` in config/visuals.toml, `RoadUv`).
   - Hafren's road meshes were read with `acr-ref mesh-uv` (SM_WalesS4HafrenSouthTrack0104_Road_0). They have 5 UV
     channels:
     - UV0: tiling, 2.02 m across × 3.59 m along per unit.
     - UV1: U 0..1 from edge to edge, V at 7.17 m.
     - UV2 and UV4: a trajectory coordinate, U about −0.2..1.2 at 5.5 m, V at 100 m; UV4's U is 1 − UV2's.
     - UV3: an unknown lateral vector.
     - Vertex colour: R about 0, G 128, B 0, A mostly below 20.
   - v1.7 writes UV0–UV2 in that layout from the DR2 centreline (distance along it, signed offset from it), and
     UV4's mirror in UV3 (a PMC has 4 channels). The road vertex colour is (0, 128, 0, 10).
   - Road-surface triangles more than 15 m from the centreline (DR2 side roads) are mapped world-planar at the same
     scale. 96.6 % of road triangles are within 1.5× of the nominal scale; the rest are mostly bend insides.
   - The terrain stays at the tex-proof's UV0 = world/4 m. The vista uses the terrain material at world/40 m.
3. **Terrain painted to DR2's layout:** drive the 9-material blend with masks generated from DR2's terrain materials
   (which DR2 surface sits where, from `terrain.glb` materials or `collision.glb` surface codes).
   - Now verified: Hafren's terrain meshes (e.g. SM_WalesS4HafrenSouthTrack0104_Terrain_0) carry UV0 as a tile
     coordinate at about 2.5 km per unit, and a constant (0, 1) in UV1 and UV2. So the masks are sampled by UV0.
   - Our world/4 m UV0 is far outside 0..1, so the clamped masks give one edge texel everywhere. That is why the
     ground shows a single uniform blend today.
   - This makes variant 3a below the relevant one. Variants:
   - **3a (if the masks are UV-sampled):** assign each DR2 terrain triangle to one of the 34 Hafren tiles' MIs in
     separate sections, and give it tile UVs. Overwrite those tiles' `_M1`/`_M2` with masks rendered from DR2's
     layout. This needs a **virtual-texture writer**: DXT5 encoding, 128+4 px tiles, the mip chain, and the VTData
     chunk layout, with the bulk data in the `.ubulk`.
   - **3b (simpler, if the mask textures can also be non-VT):** replace the texture's platform data with a plain mip
     chain and clear the VT flag. This depends on whether the material's texture sampler is a VT sampler. If it is,
     a non-VT texture will not bind, and 3a is required.
   - Effort: 3–5 days (the texture writer is most of it). Risk: medium. There is no editor to validate VT layouts, and
     a wrong layout shows as garbage, not a crash (inferred).
   - **3c (cheap experiment first, no texture writing):** decode tile 1001's existing masks (read-only) and find spots
     whose layer mix matches each DR2 surface class (grass, dirt, rock, ...). Then give each DR2 terrain triangle a
     UV0 at its class's spot.
     - This only works if the layer textures tile from world position rather than from UV0. A screenshot answers it:
       if the ground goes blurry or flat, the layers use UV0, and 3a it is.
     - Effort: about a day. Risk: low.
4. **DR2's own colours (optional, more fidelity):** overwrite the BaseColor and normal of a few Hafren-only textures
   with DR2 NZ textures. Candidates: Hafren-only textures referenced by the 9-blend's layers, which still has to be
   checked. Shared Wales textures would change every Wales stage, so they are excluded.
   - Needs the same texture writer (non-VT mips for regular textures) plus a reference scan, so we only touch
     textures used by Hafren alone.
   - Effort: 2–3 days after step 3. Risk: medium.
5. **Objects textured:** objects are 269 render meshes plus proxies today.
   - Map DR2 object materials to existing ACR prop MIs by kind (wood, metal, rock and foliage-free props), using
     DR2 UVs. This needs no texture writing, only a lookup table. Trees stay as proxies, or get a cheap card crown
     with an ACR foliage MI, if one renders without per-instance data.
   - Effort: 1–2 days. Risk: low to medium (materials expecting vertex colours or custom data may look wrong).

## Recommendation

Test the proof in step 1 first: its result decides between step 2 alone (cheap) and steps 2–3, which need the texture
writer. The editor (UE 5.6, about 60 GB) is still not needed for any of these steps. It only becomes attractive for
real foliage, LODs or new master materials.
