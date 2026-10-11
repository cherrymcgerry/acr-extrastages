# Textured visuals without the editor: plan (nz01r5)

Status: v1.2 renders DR2 geometry in flat debug colours via the start-trigger ProceduralMeshComponent (PMC), verified
in game; v1.3 adds the objects (docs/decisions.md D6, D7). This plan replaces the flat colours with textured materials
without the UE editor. Nothing beyond the material proof below is implemented.

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

1. **Material proof (built, untested):** `work\acr\tex-proof\dist` is v1.3 with `MI_Terrain9MatBlend1001` on the
   terrain section and `MI_WalesS4HafrenSouthGravelRoad1` on the road section. Config:
   `work\acr\tex-proof\visuals-texproof.toml`, built with `build.ps1 -Version v1.3 -VisualsConfig ...`.
   - It answers: does a lit, textured Hafren material render on the PMC? And how are its masks and layers sampled:
     by UV0, which we set to world/4 m and which the masks clamp over a 350 m tile, or by world position?
   - A screenshot of the ground decides what follows.
   - Effort: done (config only). Risk: low; the worst case is a black or flat ground, and it is fully reversible.
2. **Road textured (likely cheap):** if the gravel material renders, write proper road UVs (U across the road
   0..1, V = distance / tile length) and use the Hafren gravel MI as is. These are Welsh gravel textures, not NZ ones,
   but they read as gravel.
   - Effort: about half a day. Risk: low.
3. **Terrain painted to DR2's layout:** drive the 9-material blend with masks generated from DR2's terrain materials
   (which DR2 surface sits where, from `terrain.glb` materials or `collision.glb` surface codes). Two variants:
   - **3a (if the masks are UV-sampled):** assign each DR2 terrain triangle to one of the 34 Hafren tiles' MIs in
     separate sections, and give it tile UVs. Overwrite those tiles' `_M1`/`_M2` with masks rendered from DR2's
     layout. This needs a **virtual-texture writer**: DXT5 encoding, 128+4 px tiles, the mip chain, and the VTData
     chunk layout, with the bulk data in the `.ubulk`.
   - **3b (simpler, if the mask textures can also be non-VT):** replace the texture's platform data with a plain mip
     chain and clear the VT flag. This depends on whether the material's texture sampler is a VT sampler. If it is,
     a non-VT texture will not bind, and 3a is required.
   - Effort: 3–5 days (the texture writer is most of it). Risk: medium. There is no editor to validate VT layouts, and
     a wrong layout shows as garbage, not a crash (inferred).
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
